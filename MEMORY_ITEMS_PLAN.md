# Plan: Fact-Level Memory (memory items + embeddings)

**Goal:** replace the single-text memory manifest with a store of small, separate **memory items** (one fact each), each with an embedding. New chat turns are turned into candidate facts, compared with similar existing items, and applied as `ADD` / `UPDATE` / `DELETE` / `NOOP` operations. At chat time the agent gets a small **core** set of items every turn, plus the items **relevant** to the current message.

**Builds on:** `EMBEDDINGS_PLAN.md` (embedding generator, prefixes) and `SEMANTIC_SEARCH_PLAN.md` (pgvector cosine search, distance cutoff, search with the current message only).

**Rules carried over:**
- ZDR stays on: **one input per embedding call**. Reuse `IChatTurnEmbeddingService`, which already enforces this.
- No vector index. A personal bot stays small enough for an exact scan.
- Memory work never breaks a chat reply (try/catch around every trigger and search, like today).

**The old manifest flow stays in the code but is switched off:**
- `MemoryConsolidationAgentService`, `MemoryConsolidationJob`, `MemoryConsolidationCoordinator`, `MemoryService`, `MemoryContextProvider` and their DI registrations **stay as they are**.
- `AgentService`: the `new MemoryContextProvider(...)` line is **commented out**.
- `ChatCommand`: the `memoryConsolidationCoordinator.QueueIfNeededAsync(...)` block is **commented out**.
- Existing `UserMemoryManifest` rows are not touched. They're read once to seed the new store (Step 5).

---

## Why (short version)

| Today (manifest) | After (memory items) |
|---|---|
| The LLM re-types the whole manifest every run, so wording drifts and details fall out | Only the changed facts are touched; everything else is never re-written |
| A single bad response (preamble, truncated output, refusal) can replace the whole memory | Changes are per item, checked in code, and soft-deleted |
| The whole manifest goes into the system prompt on every message | Core items + top-k relevant items, so the prompt stays small as memory grows |
| No way to tell where a fact came from | Every item records its source turn IDs, timestamps and a change reason |
| Assistant replies (web results, guesses) can become "user facts" | Extraction only takes facts the user stated or confirmed |

---

## How it works

```
ChatCommand ─► SaveTurnAsync ─► MemoryExtractionCoordinator (pending turns ≥ threshold?)
                                        │
                                        ▼
                               MemoryExtractionJob (Hangfire, one per user at a time)
   1. load oldest unprocessed turns (ChatTurn.MemoryProcessedAt IS NULL)
   2. EXTRACT   ── 1 LLM call ──► candidate facts
   3. embed each candidate (1 call each) ─► top-k similar active items (pgvector)
   4. RECONCILE ── 1 LLM call for all candidates that have neighbors ──► decisions
   5. validate + apply in one SaveChanges, mark turns processed
   6. re-enqueue if enough turns are still pending

AgentService (every message)
   MemoryItemContextProvider
     ├─ core items (always)
     └─ embed current message (query prefix) ─► top-k relevant non-core items
```

---

## Steps

### Step 0: Structured output ✅
Extraction and reconciliation use `GetResponseAsync<T>` (JSON schema response format) instead of free text.

> Result: ✅ confirmed — `GetResponseAsync<T>` works with the chat model through OpenRouter with the ZDR guardrail on. No fallback needed.

### Step 1: Configuration
New section and options class `MemoryItemOptions`, bound in `BotServiceRegistration`:

```json
"MemoryItems": {
  "TurnsThreshold": 10,
  "MaxTurnsPerRun": 30,
  "MaxCandidatesPerRun": 20,
  "ReconcileTopK": 5,
  "ReconcileMaxCosineDistance": 0.35,
  "MaxCoreItems": 40,
  "RetrievalTopK": 8,
  "RetrievalMaxCosineDistance": 0.5,
  "MaxItemLength": 300
}
```

~~No new reasoning option: extraction reuses `OpenRouter:Reasoning:MemoryConsolidation` (see Step 4).~~
> **Changed later:** the memory agent got its own model and reasoning setting: `MemoryItems:Model` (`deepseek/deepseek-v4.1-flash`, sent per request through `ChatOptions.ModelId`) and `OpenRouter:Reasoning:MemoryExtraction` (`ExtraHigh`, sent as `xhigh`).

The distance values are starting guesses. They get tuned in Step 10.

### Step 2: Database
**New entity `UserMemoryItem`** (`Features/UserManagement/Models/`):

| Column | Type | Notes |
|---|---|---|
| `Id` | int | |
| `TelegramUserId` | int, FK | |
| `Text` | text | One standalone fact, ≤ `MaxItemLength` chars, e.g. "User is allergic to penicillin." |
| `Category` | text | One of the fixed set below |
| `IsCore` | bool | Always injected into the chat prompt |
| `Status` | text | `active`, `superseded`, `deleted` (string, like `DeferredIntent.Status`) |
| `Embedding` | `vector(768)` | Document-prefixed. Filled **before** insert, so active items always have one |
| `SourceTurnIds` | `int[]` | The turns the fact came from (empty for items imported from the manifest) |
| `SupersededById` | int? | Set on the old row when an `UPDATE` replaces it |
| `ChangeReason` | text | The LLM's short reason for the last change (audit/debugging) |
| `CreatedAt`, `UpdatedAt`, `LastConfirmedAt` | timestamp | `LastConfirmedAt` is bumped on `NOOP` (the fact came up again) |

Index: `(TelegramUserId, Status)`.

**Categories** (fixed set, written into the prompts): `identity`, `preference`, `relationship`, `work_education`, `health`, `goal`, `routine`, `interest`, `other`.

**`IsCore`** means "this should shape every reply": name, how the user wants to be addressed, language, strong communication preferences, key identity facts. Everything else is retrieved only when it's relevant.

**New column `ChatTurn.MemoryProcessedAt` (`timestamp?`).** `NULL` means "not extracted yet", so the column itself is the work queue, the same pattern as `ChatTurn.Embedding`. No new state table, and it doesn't share state with the old `UserMemoryConsolidationState`.

**Migration `AddUserMemoryItems`:**
1. Create the table, add the column.
2. Backfill in SQL: set `memory_processed_at = now()` for turns with `id <= user_memory_consolidation_states.last_consolidated_chat_turn_id` for the same user. Those turns are already in the manifest, which gets imported in Step 5. Later turns stay `NULL` and get extracted normally.

**InMemory tests:** add `modelBuilder.Entity<UserMemoryItem>().Ignore(x => x.Embedding)` next to the existing `ChatTurn` ignores in `ApplicationDbContext`. If the InMemory provider can't handle `int[]`, ignore `SourceTurnIds` there too.

### Step 3: `MemoryItemService` (data access)
In `Features/UserManagement/Services/`, behind `IMemoryItemService`:
- `GetCoreItemsAsync(chatId)`: active + core, ordered by category, up to `MaxCoreItems`.
- `GetActiveItemsAsync(chatId)`: for `/memory`.
- `FindNeighborsAsync(telegramUserId, Vector, topK, maxDistance)`: active items by cosine distance (for reconciliation).
- `SearchAsync(chatId, Vector queryVector, topK, maxDistance)`: active **non-core** items (core items are already in the prompt). It takes a vector, not a string, so the caller can share one query embedding (Step 7).
- `HasAnyItemsAsync(telegramUserId)`.

Pgvector queries go here and nowhere else, so job tests can fake this interface (InMemory can't run `CosineDistance`).

### Step 4: `MemoryExtractionAgentService` (LLM calls)
In `Features/UserManagement/Services/`, behind `IMemoryExtractionAgentService`. It uses the shared `IChatClient` and **the same agent settings as `MemoryConsolidationAgentService`**, with **no** web search `RawRepresentationFactory`:

```csharp
private readonly ReasoningEffort _reasoningEffort = aiOptions.Value.OpenRouter.Reasoning.MemoryConsolidation;

var response = await chatClient.GetResponseAsync<T>(
    message,
    new ChatOptions
    {
        Instructions = BuildInstructions(),
        Temperature = 0.2f,
        Reasoning = new ReasoningOptions { Effort = _reasoningEffort }
    },
    cancellationToken: cancellationToken);
```

Both calls (`ExtractAsync` and `ReconcileAsync`) use these options, each with its own instructions.

**`ExtractAsync(turns) → IReadOnlyList<CandidateFact>`**

`CandidateFact(string Text, string Category, bool IsCore, int[] SourceTurnIds)`

Input format: each turn inside delimiters, with **local** time and the timezone:
```
<turn id="812" at="2026-09-27 01:30 Europe/Istanbul">
<user>…</user>
<assistant>…</assistant>
</turn>
```
Prompt rules (the durable-memory rules come from the old prompt):
- Only store facts the **user** stated or confirmed. Assistant messages are context for understanding the user; never a source of facts, even if they contain web results or claims about the user.
- One fact per item, as a standalone sentence (no "he", "that one", "the thing above").
- Turn relative dates into absolute ones using the turn's local time ("tomorrow" → "2026-09-28").
- Skip task/reminder state, IDs, cron expressions, confirmations and execution results. If a scheduling turn shows a lasting preference, keep only the preference.
- Skip small talk and short-lived moods unless they point to something lasting.
- It's fine to return an empty list.

**`ReconcileAsync(candidates with their neighbors) → IReadOnlyList<MemoryDecision>`**

`MemoryDecision(int CandidateIndex, string Action, int? TargetItemId, string? Text, string? Category, bool? IsCore, string Reason)`

One call for **all** candidates that have neighbors. Each candidate is listed with its numbered neighbor items (id, text, category, created date). Rules:
- `ADD`: new information.
- `UPDATE(target)`: the candidate refines or corrects the target. `Text` is the merged fact.
- `DELETE(target)`: the user explicitly said the target is no longer true, and the candidate adds nothing new.
- `NOOP(target)`: already known; used only to bump `LastConfirmedAt`.
- If facts contradict, the latest explicit user statement wins.

### Step 5: `MemoryExtractionJob` (Hangfire)
`[AutomaticRetry(Attempts = 2)]`, `[DisableConcurrentExecution(10 * 60)]`, like `ChatTurnEmbeddingJob`.

1. **Import the manifest once:** if the user has no items (`HasAnyItemsAsync`) and has an active `UserMemoryManifest`:
   - run `ExtractAsync` with the manifest text as the only source, telling it: "these are already-established facts; split them into items"
   - embed each item and insert it as `ADD` (skip candidates within distance ≤ 0.05 of an item already inserted in this import)
   - `SourceTurnIds = []`, `ChangeReason = "imported from manifest v{n}"`
2. Load up to `MaxTurnsPerRun` turns with `MemoryProcessedAt == null`, ordered by `Id`. Return if there are none.
3. `ExtractAsync(turns)`. Cap at `MaxCandidatesPerRun`, and drop candidates that are empty, longer than `MaxItemLength`, or have an unknown category.
4. For each candidate: `EmbedDocumentAsync(text)` (one call), then `FindNeighborsAsync(ReconcileTopK, ReconcileMaxCosineDistance)`.
   - No neighbors → `ADD` directly, with no LLM call.
   - Otherwise, collect it for reconciliation.
5. `ReconcileAsync(...)` once, if anything was collected.
6. **Validate every decision in code** before applying it:
   - `TargetItemId` must be one of the neighbors **shown for that candidate**. Otherwise drop the decision and log it (this guards against made-up IDs).
   - `UPDATE`/`ADD` text must pass the same checks as step 3.
   - If adding a core item would go over `MaxCoreItems`, store it as non-core and log a warning.
   - Candidates with no decision are treated as `NOOP`.
7. **Apply everything in one `SaveChangesAsync`:**
   - `ADD`: insert, reusing the candidate's vector.
   - `UPDATE`: insert a new row (a new embedding call only if `Text` differs from the candidate's text). Set the old row to `superseded` with `SupersededById`.
   - `DELETE`: set `deleted` (soft delete).
   - `NOOP`: set `LastConfirmedAt = now` on the target.
   - Set `MemoryProcessedAt = now` on the loaded turns.
8. If there are still ≥ `TurnsThreshold` pending turns **and** this run made progress, re-enqueue (the same backfill pattern as the embedding job).

**If something fails:** any exception before step 7 leaves the turns `NULL`, so Hangfire's retry (or the next trigger) runs the batch again. Nothing is half-applied, because step 7 is a single save.

**Cost per run** (about 10–30 turns): 1 extraction call + 1 embedding call per candidate (usually 0–5) + at most 1 reconciliation call + an embedding call for each `UPDATE` whose text changed.

### Step 6: Trigger (`MemoryExtractionCoordinator` + `ChatCommand`)
`MemoryExtractionCoordinator.QueueIfNeededAsync(telegramUserId)`, modeled on `ChatTurnEmbeddingCoordinator` (no "already queued" flag, because the job is serialized and idempotent):
- Enqueue if pending turns ≥ `TurnsThreshold`, **or** if the user has no items but does have an active manifest. That way the first message after deploy starts the import immediately.

`ChatCommand`:
- **Comment out** the `memoryConsolidationCoordinator.QueueIfNeededAsync` try/catch block, with a comment pointing to this plan. Leave the constructor parameter so it's easy to switch back.
- Add the same kind of try/catch block for `memoryExtractionCoordinator.QueueIfNeededAsync`.

### Step 7: Chat-time retrieval (`AgentService`)
New `MemoryItemContextProvider` (an `AIContextProvider` in `Features/Chat/Services/`):
- **Core block** (always): `GetCoreItemsAsync`, grouped by category, under a heading like "Known facts about the user".
- **Relevant block**: `SearchAsync` with the current message's query vector. The block is left out when there are no hits. It uses the **current message only**, the lesson learned in `SEMANTIC_SEARCH_PLAN.md`.
- Search failures are logged and treated as "no hits".

**Embed the query once per message.** Chat-turn search already embeds the current message. So the query vector gets created once per `RunAsync` (a lazily started `Task<Vector>` keyed by the query text, created in `AgentService`) and used by both `SearchChatTurnsAsync` and the new provider. That means one extra embedding call per message instead of two. This needs `IChatTurnService.SearchTurnsAsync` to get an overload that takes a `Vector`.

`AgentService.AIContextProviders`:
```csharp
new PersonalityContextProvider(chatId, personalityService),
// Replaced by MemoryItemContextProvider (see MEMORY_ITEMS_PLAN.md). Kept to allow switching back.
// new MemoryContextProvider(chatId, memoryService),
new MemoryItemContextProvider(...),
new TemporalContextProvider(chatId, dbContext, assistantTimeService),
chatHistorySearchProvider,
```

`DeferredIntentDispatchJob` goes through `AgentService`, so deferred tasks get the new memory automatically.

**Working alongside chat-turn search (optional).** The two providers complement each other: memory items give current facts, chat-turn search gives what was said and when. Two possible friction points, to handle only if Step 10 shows they matter:
- **Stale turns vs. current facts:** turn search can still bring up a turn that states a fact that's since been superseded (for example "I live in Istanbul" after the move to Ankara). Add a precedence line to the memory block or to `FormatChatTurnSearchResults`: *"Memory items are the current truth. Past chat turns are history and may be outdated."*
- **Duplicates:** a fact and the turn it came from can both land in the prompt. Lower the chat-turn search limit (`maxResults` in `SearchChatTurnsAsync`, 10 today) to about 5, since the facts now carry the durable information. Going further, hide turns whose IDs appear in the `SourceTurnIds` of a retrieved item.

### Step 8: `/memory` command
Switch `MemoryCommand` to `IMemoryItemService.GetActiveItemsAsync`:
- core items first, then the rest grouped by category
- each line shows the item ID (handy for a future `/forget <id>`)
- a total count

The old manifest can still be read in the database but is no longer shown.

*(Later, not in this plan: `/forget <id>` to soft-delete an item by hand.)*

### Step 9: Tests
Using `UseInMemoryDatabase`, with fakes for `IMemoryExtractionAgentService`, `IChatTurnEmbeddingService` and `IMemoryItemService.FindNeighborsAsync`.

- **Job:**
  - no neighbors → `ADD` with no reconcile call
  - `UPDATE` → old row superseded and linked, new row active
  - `DELETE` → soft-deleted
  - `NOOP` → only `LastConfirmedAt` changes
  - a made-up `TargetItemId` is dropped
  - `MaxCoreItems` limit is enforced
  - turns are marked processed only on success
  - an exception leaves turns `NULL`
  - `MaxTurnsPerRun` is respected
- **Import:** runs once when there are no items and a manifest exists; doesn't run again once items exist.
- **Coordinator:** below threshold → nothing; at threshold → enqueue; no items + manifest → enqueue.
- **Agent service:** the input contains delimited turns with local timestamps (test the input builder the same way `TemporalContextProviderTests` does).
- **`MemoryCommand`:** update `MemoryCommandTests` for the new output.
- The existing `MemoryConsolidation*` tests stay and should still pass, because that code is unchanged.

### Step 10: Verify and tune by hand
1. Deploy, send one message, and check that the import job ran:
   `SELECT id, category, is_core, text FROM user_memory_items WHERE status = 'active';`
2. Chat 10+ turns that include a correction ("I actually moved to Ankara"). Check that the Istanbul item is `superseded`, points to a new item, and has a sensible `change_reason`.
3. Turn on Debug logging for the new services (distance logging like `ChatTurnService.LogSearchResults`) and tune `ReconcileMaxCosineDistance` and `RetrievalMaxCosineDistance` against real data.
4. Check the prompt size: core items should stay well under the old manifest's size.

### Step 11: Docs
- `AGENTS.md`: update "Memory Consolidation", the background jobs table and the key entities to describe memory items. Mark the manifest flow as kept but switched off.
- `README` (if it describes memory): the same.

---

## Open questions (defaults used unless changed)
1. **Fact language:** facts are stored **in the language the user wrote in** (usually Turkish), so `/memory` reads naturally. Gemini embeddings work across languages, so mixed languages don't hurt matching.
2. **Import vs. replay:** the plan imports the current manifest instead of replaying the whole chat history, because it's much cheaper. The downside is that imported items have no source turns. Replaying every turn (set all `memory_processed_at` to `NULL`) is possible later if provenance matters.
3. **Forgetting over time:** `LastConfirmedAt` is recorded but not used yet. Archiving items that haven't been confirmed or retrieved for N months is left for a later plan.
