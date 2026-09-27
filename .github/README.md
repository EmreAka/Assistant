# Assistant
### Personal Telegram Bot (for fun and learning)

## Purpose
Assistant is a personal Telegram bot project built mainly for entertainment, experimentation, and learning.

At this stage, it is not intended to be a production-grade or public SaaS product.

## Current Status
- The implementation is still intentionally small, but the core command and agent infrastructure is already in place.
- The bot currently supports `/start`, `/chat`, `/memory`, and `/tts`.
- Plain text messages without a slash command are routed to the `chat` command automatically.
- The bot keeps long-term user memory as separate **memory items** (one fact each, with an embedding). A background extraction job turns new chat turns into facts and reconciles them with the stored ones (there is no memory-update tool on the chat agent). The older manifest-based consolidation is still in the code but switched off.
- Successful chat turns are persisted, embedded in the background, and recalled through **semantic search** (pgvector cosine distance) so the agent can pull relevant older conversation snippets.
- The agent session (the last 40 messages of short-term chat history) is persisted per chat in [Ruvio](https://salihcantekin.github.io/ruvio/), a Redis-protocol store, so conversations continue across app restarts.
- The chat agent can schedule, list, cancel, and reschedule deferred tasks and reminders through Hangfire-backed tools.
- The chat agent can run live web search for fresh information through OpenRouter's server-side web search tool.
- Incoming Telegram updates, deferred tasks, memory extraction, and chat-turn embedding are processed in the background via Hangfire.

## Commands
The bot currently supports the following commands:

| Command | Description |
| --- | --- |
| `/start` | Registers the Telegram user and sends a welcome message. |
| `/chat` | General-purpose chat entrypoint. The agent can answer questions, use remembered context and past chat turns, manage reminders/tasks, do exact math, and search the web when needed. |
| `/memory` | Lists the user's active memory items: core items first, then the rest grouped by category, each with its ID. |
| `/tts` | Sends the assistant's last message as audio (xAI text-to-speech). |

Bot commands are registered with Telegram during application startup.

Examples:
- `/chat 5 saat sonra Mustafa abiyle toplantımı hatırlat`
- `/chat NVIDIA stock price current`
- `/memory`
- `/tts`
- `yarın sabah 9'da su içmeyi hatırlat`

Chat flow:
- The agent combines recent session history, personality, core and relevant memory items, temporal context, and semantically relevant persisted chat turns before answering.
- Session history is loaded from Ruvio before each run and written back after it (see [Agent Session Persistence](#agent-session-persistence)).
- Memory is stored as individual `UserMemoryItem` rows (see [Long-Term Memory](#long-term-memory-memory-items)).
- Older chat turns are stored in `chat_turns` and retrieved with semantic search (see [Semantic Chat-Turn Search](#semantic-chat-turn-search)).
- Agent tools currently include `ScheduleTask`, `ListTasks`, `CancelTask`, `RescheduleTask`, `GetCurrentDateTime`, and `Calculate`. Web search runs server-side on OpenRouter.
- Run `/memory` to inspect the memory items that can be injected into chat context.

## Semantic Chat-Turn Search
Past conversations are recalled by meaning rather than by keyword, so a message like "what was that movie we talked about?" can find a turn that never contained the word "movie".

How it works:
1. **Storing** – after every successful reply, `ChatCommand` saves the user/assistant pair as a `ChatTurn` row with a `NULL` embedding.
2. **Embedding (background)** – `ChatTurnEmbeddingCoordinator` checks how many of the user's turns are still un-embedded. Once that count reaches `Embeddings:TurnsThreshold`, it enqueues a `ChatTurnEmbeddingJob`.
   - The job embeds up to `Embeddings:MaxTurnsPerRun` turns (`User: ...\nAssistant: ...`) and writes a `vector(768)` into `chat_turns.embedding`.
   - A `NULL` embedding *is* the work queue: failed turns stay `NULL` and are retried on the next run, and a large backlog re-queues itself as long as progress is being made.
   - The job is serialized (`DisableConcurrentExecution`), so duplicate enqueues are harmless.
3. **Searching** – `AgentService` embeds the **current message only**, once per run, and shares that query vector with memory item search. A `TextSearchProvider` then calls `ChatTurnService.SearchTurnsAsync`, which returns up to 10 of the user's nearest turns by pgvector cosine distance. Hits with a distance above `Embeddings:MaxCosineDistance` are dropped, because vector search always returns *something*, even when nothing is related.
4. **Injecting** – matching turns are added to the agent context as "Relevant past chat turns" with their local timestamps.

Implementation notes:
- Embeddings are generated with `google/gemini-embedding-2` via OpenRouter (same API key as chat), requesting 768 dimensions.
- Gemini Embedding 2 has no `task_type` parameter, so asymmetric retrieval is expressed through text prefixes: stored turns use `title: none | text: ` and search queries use `task: search result | query: `.
- Every embedding request contains exactly **one** input. OpenRouter routes multi-input requests to a batch endpoint that is not Zero Data Retention (ZDR), which the account guardrail rejects, so do not batch.
- Search failures never break a reply: errors are logged and the agent continues without past chat turns.
- Because turns are embedded in batches, the most recent turns (fewer than `TurnsThreshold`) are not searchable yet. They are normally still covered by the session history persisted in Ruvio.
- There is no vector index yet; search is an exact scan over the user's embedded turns, which is fine at personal-bot scale.
- The legacy full-text `search_vector` column still exists in the database but is no longer used.
- To tune `MaxCosineDistance`, set the `Assistant.Api.Features.Chat.Services.ChatTurnService` log level to `Debug` to log every search hit with its cosine distance (`AgentService` logs the query text at `Debug`).

## Long-Term Memory (Memory Items)
Long-term memory is a set of small, separate facts about the user (`UserMemoryItem`), each with an embedding. New facts are compared with similar stored ones instead of rewriting one big memory document, so nothing drifts or gets lost in a rewrite, and the prompt stays small as memory grows.

How it works:
1. **Queueing** – after each saved turn, `MemoryExtractionCoordinator` counts the user's turns with `memory_processed_at IS NULL`. Once that reaches `MemoryItems:TurnsThreshold`, it enqueues a `MemoryExtractionJob`. It also enqueues right away when the user has an active manifest but no memory items yet, to run the one-time import.
2. **Manifest import (once)** – if the user has no items at all, the job splits the active `UserMemoryManifest` into items (`ChangeReason = "imported from manifest v{n}"`).
3. **Extraction** – one LLM call (`MemoryExtractionAgentService`, structured output via `GetResponseAsync<T>`) turns up to `MemoryItems:MaxTurnsPerRun` unprocessed turns into candidate facts. Only facts the **user** stated or confirmed count; assistant text is context only. Turns are sent inside `<turn>` tags with local time, and relative dates are resolved to absolute ones.
4. **Reconciliation** – each candidate is embedded (one call each) and compared with its nearest active items (`ReconcileTopK`, `ReconcileMaxCosineDistance`). Candidates with no neighbors are added directly. The rest go to one reconcile call that returns `add` / `update` / `delete` / `noop` per candidate.
5. **Applying** – decisions are validated in code (the target must be one of the neighbors shown for that candidate, each item changes at most once per run, text length and category are checked, `MaxCoreItems` is enforced) and saved in a single `SaveChanges` together with `memory_processed_at` on the turns.
6. **Using** – on every message, `MemoryItemContextProvider` adds the **core** items (always) and the non-core items relevant to the current message (`RetrievalTopK`, `RetrievalMaxCosineDistance`) to the agent instructions.

Implementation notes:
- `update` never edits in place: it inserts a new row and marks the old one `superseded` with `superseded_by_id`. `delete` is a soft delete (`status = deleted`). `noop` bumps `last_confirmed_at`.
- Every item keeps `source_turn_ids` (the turns it came from) and a `change_reason` from the model, which helps with "why does it think X?".
- `is_core` marks facts that should shape every reply (name, how to be addressed, language, strong communication preferences, key identity facts).
- Facts are written in the language the user writes in; categories are a fixed set (`identity`, `preference`, `relationship`, `work_education`, `health`, `goal`, `routine`, `interest`, `other`).
- A `NULL` `memory_processed_at` *is* the work queue: a failed run leaves the batch `NULL` and it is retried. The job is serialized (`DisableConcurrentExecution`), so duplicate enqueues are harmless.
- The `AddUserMemoryItems` migration marks turns already consolidated into the manifest (up to `last_consolidated_chat_turn_id`) as processed, because the manifest import covers them.
- Extraction and reconciliation run on their own model (`MemoryItems:Model`, default `deepseek/deepseek-v4.1-flash`) at maximum reasoning (`AIProviders:OpenRouter:Reasoning:MemoryExtraction`, default `ExtraHigh`, sent as `xhigh`), temperature 0.2. The model is switched per request through `ChatOptions.ModelId` on the shared OpenRouter chat client, so no second client is needed. An empty `MemoryItems:Model` falls back to `AIProviders:OpenRouter:Model`.
- Memory lookup failures never break a reply; the agent continues without memory.
- To tune the distance cutoffs, set `Assistant.Api.Features.UserManagement.Services.MemoryItemService` to `Debug` to log each hit's item ID and cosine distance.
- The manifest flow (`MemoryConsolidation*`, `MemoryContextProvider`) is kept but switched off: its trigger in `ChatCommand` and its provider in `AgentService` are commented out. See `MEMORY_ITEMS_PLAN.md`.

## Agent Session Persistence
The agent's short-term chat history lives in its `AgentSession` (an `InMemoryChatHistoryProvider` capped at 40 messages by `MessageCountingChatReducer`). To survive app restarts and deployments, the session is stored in Ruvio instead of process memory.

How it works:
1. Before each run, `AgentService` loads `assistant:agent-session:{chatId}` through `RuvioAgentSessionStore` and restores it with `DeserializeSessionAsync`. A missing key starts a new session.
2. The agent runs on that session.
3. After the run, the session is serialized with `SerializeSessionAsync` and written back to the same key.

Implementation notes:
- The key is derived from the Telegram chat ID, which is stable, so a restarted app finds the same session without any in-process state.
- Runs are serialized per chat (`ChatGates`, one `SemaphoreSlim` per chat ID) so two concurrent runs (two quick messages, or a message racing a deferred task) cannot load the same session and overwrite each other's turn. The gate is in-process, so this holds for a single app instance.
- `RuvioClient` is registered as a singleton by `Ruvio.Client.AspNetCore` (`AddRuvioClient`) from the `Ruvio` config section.
- If the session cannot be read, the turn runs on a fresh session that is **not** saved, so stored history is never overwritten. An unreadable (e.g. incompatible) session is replaced. A failed save is logged and does not break the reply.
- An empty `Ruvio:Password` is treated as unset; otherwise the client would send `AUTH` to a server without a password.
- Run Ruvio with a WAL-backed durability profile (`RUVIO_DURABILITY=everysec`) and a volume on `/data`. The default `memory` profile loses every session when the Ruvio container restarts.

## Background Jobs (Hangfire)
Hangfire is currently used for the following job types:

| Job | Trigger | Description |
| --- | --- | --- |
| `CommandUpdateJob` | On each accepted Telegram webhook update | Processes incoming Telegram updates asynchronously. |
| `DeferredIntentDispatchJob` | Created dynamically for one-time or recurring deferred intents | Wakes the agent up later to execute scheduled reminders/tasks. |
| `MemoryExtractionJob` | When unprocessed chat turns reach `MemoryItems:TurnsThreshold`, or once for the manifest import | Extracts facts from new chat turns and reconciles them with the user's memory items. |
| `MemoryConsolidationJob` | Switched off (trigger commented out in `ChatCommand`) | Legacy: merged recent chat turns into a new version of the user's `UserMemoryManifest`. |
| `ChatTurnEmbeddingJob` | When un-embedded chat turns reach `Embeddings:TurnsThreshold` | Generates embeddings for chat turns so they become searchable. |

Implementation notes:
- Incoming Telegram updates are enqueued from `BotController`.
- One-time deferred tasks are scheduled with `IBackgroundJobClient.Schedule`.
- Recurring deferred tasks are registered dynamically with `IRecurringJobManager.AddOrUpdate`.
- Memory extraction and embedding jobs are queued from `ChatCommand` after a turn is saved; a failure in either queue check is logged and does not affect the reply.
- `UserMemoryConsolidationState` tracked the legacy consolidation progress per user and is no longer updated.

## Architecture Overview
- `IBotCommand`
  - Defines the command contract: `Command`, `Description`, and `ExecuteAsync(...)`.
- `BotCommandFactory`
  - Resolves command handlers by command name.
- `CommandUpdateHandler`
  - Parses incoming updates, extracts the command text from message text or caption, defaults plain text messages to `chat`, resolves the handler from the factory, executes it, and logs errors.
- `BotController`
  - Receives webhook updates, validates the Telegram secret token, checks allowed chat IDs, and enqueues accepted updates to Hangfire for background processing.
- `StartCommand`
  - Registers a Telegram user in the database.
- `ChatCommand`
  - Invokes `AgentService`, persists successful chat turns, queues memory extraction and embedding checks, and sends responses through `TelegramResponseSender`.
- `MemoryCommand`
  - Lists the user's active memory items, grouped into core items and categories, and sends them back to Telegram.
- `TtsCommand`
  - Converts the last assistant message to speech with `XaiTextToSpeechService` and sends it as audio.
- `AgentService`
  - Builds the `ChatClientAgent`, registers tools, enables the OpenRouter `openrouter:web_search` server tool, embeds the current message once for memory item and chat-turn search, injects personality/memory/temporal context, loads/saves the agent session through `IAgentSessionStore`, and serializes runs per chat.
- `RuvioAgentSessionStore`
  - Reads and writes serialized agent sessions in Ruvio, keyed by chat ID.
- `PersonalityContextProvider` / `MemoryItemContextProvider` / `TemporalContextProvider`
  - Inject the assistant personality, core and relevant memory items, and current time/last activity context into the agent. (`MemoryContextProvider`, which injected the manifest, is kept but not registered.)
- `TaskToolFunctions` / `TimeToolFunctions` / `MathToolFunctions`
  - Expose task scheduling/listing/cancellation/rescheduling (backed by `DeferredIntent` plus Hangfire), current time lookup, and exact math calculation.
- `ChatTurnService`
  - Persists successful chat turns and performs semantic search over embedded turns with a query vector supplied by the caller.
- `ChatTurnEmbeddingService` / `ChatTurnEmbeddingCoordinator` / `ChatTurnEmbeddingJob`
  - Generate document/query embeddings, decide when to queue embedding work, and embed pending turns in the background.
- `MemoryExtractionCoordinator` / `MemoryExtractionJob` / `MemoryExtractionAgentService` / `MemoryItemService`
  - Decide when to extract, turn new chat turns into candidate facts, reconcile them with similar stored items, and store/query memory items (all pgvector queries live in `MemoryItemService`).
- `MemoryConsolidationCoordinator` / `MemoryConsolidationJob` / `MemoryConsolidationAgentService`
  - Legacy manifest consolidation. Kept in the code but switched off.
- `WebSearchToolFunctions`
  - Google AI Studio-backed web search, kept as an alternative to the OpenRouter server tool. Not registered as an agent tool right now.
- `TelegramResponseSender`
  - Centralizes long Telegram message splitting and Markdown fallback handling for agent-style responses.

## How It Works (Request Flow)
1. Telegram sends an update to `POST /bot/update`.
2. The request secret token is validated in `BotController`.
3. If configured, `BotController` checks `Bot:AllowedChatIds` and rejects unauthorized chats.
4. `BotController` enqueues the accepted update as a Hangfire background job.
5. `CommandUpdateJob` invokes `CommandUpdateHandler`.
6. `CommandUpdateHandler` extracts the slash command from the incoming text or caption; if there is no slash command, it routes the update to `chat`.
7. `BotCommandFactory` resolves the matching command handler.
8. For chat requests, the agent session is loaded from Ruvio, the current message is embedded once, and the agent is invoked with personality context, core and relevant memory items, temporal context, and semantically relevant prior chat turns.
9. After a successful chat reply, the updated session is written back to Ruvio, the turn is saved and memory extraction / embedding jobs are queued if their thresholds are reached.
10. The command sends its response either through `TelegramResponseSender` or directly through `ITelegramBotClient`, depending on the command path.

## Quick Start
### Prerequisites
- .NET 10 SDK
- PostgreSQL with the [pgvector](https://github.com/pgvector/pgvector) extension available (e.g. the `pgvector/pgvector` Docker image); the migrations run `CREATE EXTENSION vector`
- [Ruvio](https://salihcantekin.github.io/ruvio/start.html) for agent session storage (e.g. `docker run -d -p 127.0.0.1:6379:6379 -e RUVIO_DURABILITY=everysec -v ruvio-data:/data salihcantekin/ruvio:linux`)
- A Telegram bot token
- An OpenRouter API key (used for chat, web search, and embeddings)
- A webhook URL reachable by Telegram
- A secret token for webhook verification

Optional:
- An xAI API key if you want the `/tts` text-to-speech command
- A Google AI Studio API key if you want to re-enable `WebSearchToolFunctions` instead of the OpenRouter server tool

### Configuration
Set the `Bot`, `AIProviders`, `MemoryItems`, `Embeddings`, and `Ruvio` sections in `Assistant.Api/appsettings.Development.json` (or via user secrets / environment variables):

```json
{
  "Bot": {
    "BotToken": "YOUR_BOT_TOKEN",
    "WebhookUrl": "YOUR_WEBHOOK_URL",
    "SecretToken": "YOUR_SECRET_TOKEN",
    "AllowedChatIds": []
  },
  "AIProviders": {
    "OpenRouter": {
      "ApiKey": "YOUR_OPENROUTER_API_KEY",
      "ApiUrl": "https://openrouter.ai/api/v1",
      "Model": "google/gemini-3.1-flash-lite",
      "WebSearch": {
        "Enabled": true,
        "Engine": "auto",
        "MaxResults": 5,
        "MaxUses": 3,
        "SearchContextSize": ""
      },
      "Reasoning": {
        "Chat": "Medium",
        "MemoryConsolidation": "High",
        "MemoryExtraction": "ExtraHigh"
      }
    },
    "GoogleAIStudio": {
      "ApiKey": "YOUR_GOOGLE_AI_STUDIO_API_KEY",
      "Model": "gemini-3.1-flash-lite"
    },
    "XAI": {
      "ApiKey": "YOUR_XAI_API_KEY",
      "ApiUrl": "https://api.x.ai/v1",
      "Model": "grok-4.3",
      "TtsVoiceId": "Carina",
      "TtsLanguage": "en"
    },
    "DefaultTimeZoneId": "Europe/Istanbul"
  },
  "MemoryConsolidation": {
    "TurnsThreshold": 20,
    "StaleJobAfterMinutes": 15
  },
  "MemoryItems": {
    "Model": "deepseek/deepseek-v4.1-flash",
    "TurnsThreshold": 10,
    "MaxTurnsPerRun": 30,
    "MaxCandidatesPerRun": 20,
    "ReconcileTopK": 5,
    "ReconcileMaxCosineDistance": 0.35,
    "MaxCoreItems": 40,
    "RetrievalTopK": 8,
    "RetrievalMaxCosineDistance": 0.5,
    "MaxItemLength": 300
  },
  "Embeddings": {
    "Model": "google/gemini-embedding-2",
    "Dimensions": 768,
    "TurnsThreshold": 20,
    "MaxTurnsPerRun": 50,
    "MaxCosineDistance": 0.5
  },
  "Ruvio": {
    "Host": "127.0.0.1",
    "Port": 6379,
    "Password": "",
    "ConnectTimeout": "00:00:05"
  }
}
```

Provider notes:
- `AIProviders:OpenRouter` is the main chat/agent provider used by `AgentService` and memory extraction (which uses its own model, `MemoryItems:Model`). Its API key is also used for embeddings.
- `AIProviders:OpenRouter:Reasoning` sets the reasoning effort per agent: `Chat`, `MemoryConsolidation` (legacy) and `MemoryExtraction`. Allowed values: `None`, `Low`, `Medium`, `High`, `ExtraHigh`.
- `AIProviders:OpenRouter:WebSearch` configures the [`openrouter:web_search` server tool](https://openrouter.ai/docs/guides/features/server-tools/web-search). The model decides when to search and OpenRouter runs the search server-side, so there is no separate web search tool function. `Engine: "auto"` uses the provider's native search when the model supports it (Gemini 3.1 Flash Lite does) and falls back to Exa otherwise.
- `AIProviders:XAI` is only used by the `/tts` text-to-speech command.
- `AIProviders:GoogleAIStudio` is kept for optional/experimental use and is not on any active path. It is only needed if you re-register `WebSearchToolFunctions` in `AgentService`.
- `AIProviders:DefaultTimeZoneId` is shared by time-sensitive chat behavior and deferred task scheduling.
- Keep OpenRouter Zero Data Retention (ZDR) enabled; embeddings are sent one input per request for that reason.

Memory item options:

| Key | Default | Description |
| --- | --- | --- |
| `MemoryItems:Model` | `deepseek/deepseek-v4.1-flash` | OpenRouter model for extraction and reconciliation. Empty uses `AIProviders:OpenRouter:Model`. Must support structured outputs and have ZDR endpoints. |
| `MemoryItems:TurnsThreshold` | `10` | Unprocessed turns needed before an extraction job is queued. |
| `MemoryItems:MaxTurnsPerRun` | `30` | Maximum turns sent to one extraction call. |
| `MemoryItems:MaxCandidatesPerRun` | `20` | Maximum candidate facts kept from one extraction call. |
| `MemoryItems:ReconcileTopK` | `5` | Nearest existing items shown to the model for each candidate. |
| `MemoryItems:ReconcileMaxCosineDistance` | `0.35` | Existing items farther than this aren't treated as neighbors; a candidate with none is added directly. |
| `MemoryItems:MaxCoreItems` | `40` | Cap on active core items; extra core facts are stored as non-core. |
| `MemoryItems:RetrievalTopK` | `8` | Maximum relevant non-core items injected per message. |
| `MemoryItems:RetrievalMaxCosineDistance` | `0.5` | Relevant-item hits farther than this are dropped. |
| `MemoryItems:MaxItemLength` | `300` | Maximum characters per fact; longer facts are dropped. |

Legacy memory consolidation options (only used if the manifest flow is switched back on):

| Key | Default | Description |
| --- | --- | --- |
| `MemoryConsolidation:TurnsThreshold` | `20` | Unconsolidated turns needed before a consolidation job is queued. |
| `MemoryConsolidation:StaleJobAfterMinutes` | `15` | After this long, a queued/running job is treated as stale and can be re-queued. |

Embedding / semantic search options:

| Key | Default | Description |
| --- | --- | --- |
| `Embeddings:Model` | `google/gemini-embedding-2` | OpenRouter embedding model. |
| `Embeddings:Dimensions` | `768` | Requested vector size. Must match the `vector(768)` column; changing it requires a migration and re-embedding. |
| `Embeddings:TurnsThreshold` | `20` | Un-embedded turns needed before an embedding job is queued. |
| `Embeddings:MaxTurnsPerRun` | `50` | Maximum turns embedded per job run. |
| `Embeddings:MaxCosineDistance` | `0.5` | Search hits farther than this are dropped. Lower = stricter. |

Ruvio options:

| Key | Default | Description |
| --- | --- | --- |
| `Ruvio:Host` | `127.0.0.1` | Ruvio host. In Docker Compose, use the service name (e.g. `ruvio`). |
| `Ruvio:Port` | `6379` | Ruvio RESP port. |
| `Ruvio:Password` | empty | Sent as `AUTH` only when set. Leave empty when Ruvio has no `requirepass` (e.g. only reachable inside the Compose network). |
| `Ruvio:ConnectTimeout` | `00:00:05` | TCP connect timeout. |

Also configure database connection strings in the same file:

```json
{
  "ConnectionStrings": {
    "PostgreSQL": "Host=...;Port=5432;Database=...;Username=...;Password=...",
    "HangfireDb": "Host=...;Port=5432;Database=...;Username=...;Password=..."
  }
}
```

### Run
```bash
dotnet restore
dotnet ef database update --project Assistant.Api
dotnet run --project Assistant.Api
```

Webhook endpoint used by this API:
- `POST /bot/update`

In development, Hangfire Dashboard is available at:
- `GET /hangfire`

## Project Structure
```text
Assistant/
├── Assistant.Api/
│   ├── Controllers/
│   │   └── BotController.cs
│   ├── Data/
│   │   ├── Configurations/
│   │   └── Migrations/
│   ├── Domain/
│   │   └── Configurations/
│   ├── Extensions/
│   ├── Features/
│   │   ├── Chat/
│   │   └── UserManagement/
│   ├── Services/
│   │   ├── Abstracts/
│   │   └── Concretes/
│   └── Screens/
├── Assistant.Api.Tests/
│   ├── Chat/
│   ├── Extensions/
│   ├── UserManagement/
│   └── Fixtures/
└── Assistant.sln
```

## Roadmap / Upcoming Features
Near-term focus areas:
- Hardening the memory, deferred-task, and semantic search flows as the agent surface grows
- Tuning `MaxCosineDistance` and adding a vector index if the number of stored turns grows
- Tuning the memory item distance cutoffs against real data, and a `/forget <id>` command to remove a memory item by hand
