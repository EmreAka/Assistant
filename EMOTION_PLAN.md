# Emotion Plan

Give Aurora a mood that carries over between messages. It colours her tone, follows from
what happens in the conversation, and fades back to her default mood over time.

Design rules:
- **Subtle.** The mood shapes the tone of a reply, never whether she helps. No "I'm feeling X!"
  openers.
- **Reacting is separate from replying.** The chat model reads the mood and never sets it.
  A separate cheap structured call updates it after the turn, so the reply stays fast and the
  chat model can't act out its own emotions.
- **Bounded.** Each turn can move the mood only a limited amount, and the mood decays toward a
  baseline, so one message can't make her furious for a week.
- **The model classifies, code decides.** The update model picks an event type and an intensity,
  not numbers. Config maps them to fixed deltas, so the same kind of message always moves the
  mood the same way, and reactions are tuned in config instead of in the prompt.
- **Never breaks chat.** If the mood can't be read, the turn runs with no mood block. If it can't
  be updated, the old mood stays.

Verification follows the repo rule: DB code is checked against the local Docker Postgres from a
scratch harness, not InMemory tests. Pure math (decay, clamping) gets plain unit tests.

---

## Core

### Step 1: Emotion state model, options, service

- `Features/Chat/Models/AgentEmotionState.cs`, one row per `TelegramUserId` (keyed like memory items):
  - `Valence` (double, -1..1): unhappy to happy
  - `Arousal` (double, 0..1): calm to energetic
  - `Mood` (string, ≤ 40 chars): a label such as "cheerful" or "a bit worried about you"
  - `Reason` (string, ≤ 200 chars): the short cause, e.g. "user's exam is tomorrow"
  - `UpdatedAt` (UTC), `LastTurnId` (int?, the last chat turn applied; makes updates idempotent)
- EF configuration + migration `AddAgentEmotionState` (table `agent_emotion_states`, unique index on
  `telegram_user_id`).
- `Domain/Configurations/EmotionOptions.cs`, section `Emotion`:
  - `Enabled` (true), `Model` (`deepseek/deepseek-v4.1-flash`)
  - `BaselineValence` (0.3), `BaselineArousal` (0.5), `BaselineMood` ("relaxed")
  - `HalfLifeHours` (6), `MaxDeltaPerTurn` (0.3, a safety cap against config typos)
  - `Events`: event type → `{ Valence, Arousal }` delta at medium intensity (defaults in step 3)
  - `IntensityMultipliers`: `low` 0.5, `medium` 1.0, `high` 1.5
- `OpenRouterReasoningOptions.Emotion` (default `Low`), sent through `ChatOptions.Reasoning`.
- `IEmotionService` / `EmotionService`:
  - `GetAsync(telegramUserId)` returns the stored row or the baseline when none exists (no decay yet)
  - `ApplyAsync(telegramUserId, turnId, delta)` clamps the delta to `MaxDeltaPerTurn` and the
    values to their ranges, skips it if `turnId <= LastTurnId`, then upserts.

Verify: migration applies; harness round-trips a row, checks clamping and that a repeated turnId
is ignored.

### Step 2: `EmotionContextProvider`

- `Features/Chat/Services/EmotionContextProvider.cs`, next to `PersonalityContextProvider`.
  It resolves the user from `chatId` and emits:

  ```
  Current mood: a bit worried about you (valence -0.2, arousal 0.6)
  Why: user mentioned a stressful deadline yesterday
  ```
- Add it to `AIContextProviders` in `AgentService`, right after the personality provider.
- Add an "Emotion rules" block to `BuildChatInstructions()`:
  - Let the mood colour word choice, energy and emoji use slightly.
  - Don't announce the mood unless asked how you feel or it's natural (e.g. greeting after a long gap).
  - Mood never makes you refuse, delay or do worse at a task.
  - Don't mention numbers.
- Failure to read the mood logs a warning and returns an empty `AIContext`.

Verify: with a hand-inserted row, run one chat turn through the harness and check the
instructions contain the block. A manual Telegram check that the tone changes, but only a little.

### Step 3: Mood update after each turn

- `IEmotionAgentService` / `EmotionAgentService`, same shape as `MemoryExtractionAgentService`:
  `GetResponseAsync<EmotionDelta>` on the shared `IChatClient` with `ModelId = Emotion:Model`,
  low temperature, `Reasoning:Emotion`. Input: Aurora's personality text, the current mood, and
  the turn (`<user>` / `<assistant>`).
  Output: `EmotionReaction { eventType, intensity, mood, reason }`. No numbers.
  - `eventType` (one, the main thing that happened in the turn):

    | Event | Meaning | Valence | Arousal |
    |-------|---------|--------:|--------:|
    | `neutral` | plain questions, tasks, reminders, small talk | 0 | 0 |
    | `affection` | user is warm, thankful, compliments her | +0.15 | +0.05 |
    | `playful` | jokes, banter, teasing in good spirit | +0.10 | +0.15 |
    | `good_news` | user shares something good in their life | +0.15 | +0.15 |
    | `bad_news` | user shares something bad, is stressed or sad | -0.15 | +0.10 |
    | `cold` | short, dismissive, ignores what she said | -0.05 | -0.10 |
    | `rude` | insults, hostility toward her | -0.20 | +0.15 |

    Values are the medium-intensity defaults in `Emotion:Events`.
  - `intensity`: `low` | `medium` | `high`
  - `mood` / `reason`: the label and cause shown to the chat model; empty reason keeps the current one.
  Prompt rules: classify from Aurora's point of view, mostly by what the user said and how they
  said it; when in doubt pick `neutral` / `low`.
  Like memory decisions, `eventType` and `intensity` are plain strings validated in code: an
  unknown value falls back to `neutral` / `low` and logs a warning.
- Delta = `Events[eventType] * IntensityMultipliers[intensity]`, then capped by `MaxDeltaPerTurn`.
  Computing it is a pure function, unit tested.
- `EmotionUpdateJob(telegramUserId, turnId)`: loads the turn, calls the agent, computes the delta,
  `ApplyAsync`.
  Failures are logged and swallowed (no Hangfire retries, a mood update isn't worth replaying).
- `ChatCommand`: after `SaveTurnAsync`, enqueue the job in its own try/catch like the other
  coordinators. Enqueuing keeps the latency off the reply. Two quick messages can run
  out of order; `LastTurnId` drops the older one.
- Only real chat turns update the mood. Deferred task runs don't save turns, so they don't.

Verify: unit tests for the event → delta mapping (multipliers, unknown values, cap); harness
with a fake `IChatClient` returning fixed reactions checks the row moves and is clamped; one
manual run against the real model with sad, happy, rude and neutral messages to check the
classification.

### Step 4: Decay toward baseline

- In `EmotionService.GetAsync`, before returning:
  `factor = 0.5 ^ (hoursSince(UpdatedAt) / HalfLifeHours)`,
  `value = baseline + (stored - baseline) * factor` for both valence and arousal.
  Nothing is written back; decay is computed on read.
- When `factor < 0.5` the stored `Mood`/`Reason` are stale: use a label derived from the decayed
  numbers (a small fixed quadrant table in code, e.g. high valence + low arousal = "content")
  and drop `Reason`.
- `EmotionUpdateJob` applies the delta to the decayed values, not the stored ones.
- The provider adds "you haven't talked in N hours" only when it's already in Temporal Context;
  no duplication. Missing the user after a gap is left to the persona and the decayed mood.

Verify: unit tests for the decay math and the label table; harness with a backdated `UpdatedAt`.

---

## Fun extras (later)

### Step 5: `/mood` command

- `Features/Chat/Commands/MoodCommand.cs`, registered in `BotServiceRegistration`. Replies with the
  current (decayed) mood and reason, in Turkish like the other command texts, e.g.
  "Şu an biraz endişeliyim — yarınki sınavın aklımda."
- Reply text comes from code, not an LLM call.

### Step 6: Emotions tied to memory

- Pass the user's core memory items (`IMemoryItemService`) to `EmotionAgentService`, so it knows what
  the user cares about (their cat, their team, their exam) and reacts with them, e.g. happy when
  their team wins, sad when their cat is sick.
- Prompt rule: these items explain why something matters; they're not a reason to change the mood
  on their own.

### Step 7: Self-initiated check-ins

- Extend `EmotionDelta` with an optional `followUp { localTime, note }`, filled only when the user
  mentioned a specific upcoming event that matters to them ("exam tomorrow at 10").
- `EmotionUpdateJob` schedules it as a one-off `DeferredIntent` through the same path
  `TaskToolFunctions.ScheduleTask` uses, with `OriginalInstruction` like
  "Check in with the user about: <note>".
- Add `DeferredIntent.Origin` (`user` | `self`, default `user`) + migration, so `ListTasks` can
  hide or label them and the guardrails below can count them.
- Guardrails, all config in `Emotion:CheckIns`:
  - `Enabled` (false by default; opt-in)
  - at most 1 pending self check-in per user
  - quiet hours (e.g. 23:00–09:00 local); times inside are moved to the end of the window
  - minimum gap since the user's last message (don't check in if they're already talking)
- `DeferredIntentDispatchJob` already runs the agent with the personality and mood, so the check-in
  message gets both for free.

### Step 8: Mood-aware TTS

- Research first: check whether the xAI TTS API supports a style or emotion parameter, or inline
  expression tags. Read the API reference, not a guide page.
- If it does, `ITextToSpeechService.SynthesizeAsync` takes an optional mood and `TtsCommand` passes
  the current one. If it doesn't, drop this step.
