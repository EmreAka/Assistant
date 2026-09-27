# AGENTS.md

This file provides guidance when working with code in this repository.

## Project Overview

A personal Telegram bot built with ASP.NET Core (.NET 10) that provides AI-powered chat and deferred tasks/reminders. Uses Hangfire for background execution and Microsoft.Agents.AI for tool-calling orchestration.

## Commands

```bash
# Build
dotnet build

# Run (development)
dotnet run --project Assistant.Api

# Run all tests
dotnet test

# Run a single test class
dotnet test --filter "ClassName=TestClassName"

# Add EF Core migration
dotnet ef migrations add <Name> --project Assistant.Api --startup-project Assistant.Api

# Apply migrations
dotnet ef database update --project Assistant.Api --startup-project Assistant.Api

# Docker build
docker build -t assistant:latest -f Assistant.Api/Dockerfile .
```

## Commits
- Use conventional commits (https://www.conventionalcommits.org/)

## Architecture

### Request Flow

1. Telegram sends `POST /bot/update` → **BotController** validates secret token + chat ID allowlist
2. Update enqueued to Hangfire → **CommandUpdateJob** runs asynchronously
3. **CommandUpdateHandler** routes slash commands via **BotCommandFactory**; plain text messages default to the `chat` command
4. Command executes:
   - `StartCommand` registers the Telegram user
   - `ChatCommand` calls **AgentService**
   - `MemoryCommand` lists the active memory items
5. Successful chat replies are persisted by **ChatTurnService** for later semantic recall and memory extraction

### AI Agent Pattern

`AgentService` builds a `ChatClientAgent` (Microsoft.Agents.AI) with:
- **Context providers**: personality, memory items (`MemoryItemContextProvider`: core items + items relevant to the current message), temporal context, and chat-history search context. The current message is embedded once per run in `AgentService` and the query vector is shared by memory item search and chat-turn search
- **AI tools** registered via `AIFunctionFactory.Create()`: schedule/list/cancel/reschedule tasks, get current time, math calculation (`Calculate`)
- **OpenRouter web search**: the `openrouter:web_search` server tool is patched into the outgoing `tools` array by `OpenRouterOptions.CreateRawChatCompletionOptions()` (wired through `ChatOptions.RawRepresentationFactory`). OpenRouter runs the search server-side, so there is no local web search tool function
- **Reasoning effort per agent**: `AIProviders:OpenRouter:Reasoning` (`Chat`, `MemoryConsolidation`, `MemoryExtraction`) is applied through `ChatOptions.Reasoning`, which the OpenAI adapter sends as `reasoning_effort` (OpenRouter's shorthand for `reasoning.effort`)
- **MessageCountingChatReducer** (40 messages) to manage the chat history window
- Session state (including the in-memory chat history) is serialized with `SerializeSessionAsync` and stored per chat ID in **Ruvio** (Redis-protocol store) by `RuvioAgentSessionStore` under `assistant:agent-session:{chatId}`, so it survives app restarts. The `RuvioClient` singleton is registered with `Ruvio.Client.AspNetCore` (`AddRuvioClient`, `Ruvio` config section). If Ruvio can't be read, the turn runs on a fresh session that is not saved, so stored history is never overwritten

**Memory Items**: Instead of inline memory updates via tools, long-term memory is stored as separate facts (`UserMemoryItem`, each with a `vector(768)` embedding). `MemoryExtractionJob` sends unprocessed chat turns to `MemoryExtractionAgentService`, which extracts candidate facts (structured output via `GetResponseAsync<T>`, only facts the user stated). Each candidate is embedded and compared with its nearest active items; candidates without neighbors are added directly, the rest go through one reconcile call returning `add`/`update`/`delete`/`noop`. Decisions are validated in code (target must be one of the offered neighbors) and applied in one `SaveChanges`. The memory agent runs on its own model (`MemoryItems:Model`, default `deepseek/deepseek-v4.1-flash`) at `Reasoning:MemoryExtraction` (default `ExtraHigh`, sent as `xhigh`); the model is switched per request with `ChatOptions.ModelId` on the shared chat client. `update` supersedes the old row instead of editing it; `delete` is soft. On first run, a user's active `UserMemoryManifest` is imported once. See `MEMORY_ITEMS_PLAN.md`.

**Legacy manifest consolidation** (`MemoryConsolidationAgentService`/`Job`/`Coordinator`, `MemoryContextProvider`) is kept in the code but switched off: its trigger in `ChatCommand` and its provider in `AgentService` are commented out.

Current AI provider usage:
- **OpenRouter** (`AIProviders:OpenRouter`) — main chat/agent model (`google/gemini-3.1-flash-lite`) used by `AgentService`, plus `MemoryItems:Model` (`deepseek/deepseek-v4.1-flash`) for memory extraction, server-side web search, and embeddings (one input per request, ZDR).
- **xAI** (`AIProviders:XAI`) — text-to-speech only (`/tts`).
- **Google AI Studio** (`AIProviders:GoogleAIStudio`) — kept for optional/experimental use. `WebSearchToolFunctions` and `CreateGoogleGenAIChatClient()` still exist but are not registered on any active path.

### Feature Structure

Features in `Assistant.Api/Features/` are self-contained slices:
- `Chat/` — `AgentService`, tool functions (task, time, math, plus the unregistered `WebSearchToolFunctions`), `ChatCommand`, deferred task dispatch, chat-turn storage/search
- `UserManagement/` — `StartCommand`, `MemoryCommand`, personality profile, Telegram user registration, memory items (`MemoryItemService`, extraction agent/job/coordinator), and the legacy memory manifest and consolidation code.

Legacy cross-cutting infrastructure still lives outside the feature folders:
- `Services/Concretes/` — command routing and update handling
- `Extensions/` — DI registration, Hangfire setup, AI option/client helpers

### Background Jobs (Hangfire)

| Job | Trigger |
|-----|---------|
| `CommandUpdateJob` | On each incoming Telegram update |
| `DeferredIntentDispatchJob` | Executes scheduled/recurring user tasks through `AgentService` |
| `ChatTurnEmbeddingJob` | Embeds chat turns when un-embedded turns reach `Embeddings:TurnsThreshold` |
| `MemoryExtractionJob` | Extracts and reconciles memory items when unprocessed turns reach `MemoryItems:TurnsThreshold`, or once for the manifest import |
| `MemoryConsolidationJob` | Legacy, switched off (trigger commented out in `ChatCommand`) |

Hangfire uses PostgreSQL storage. Dashboard at `/hangfire` in development.

### Database (EF Core + PostgreSQL)

Key entities: `TelegramUser`, `AssistantPersonality`, `ChatTurn`, `UserMemoryItem`, `DeferredIntent`, plus the legacy `UserMemoryManifest` and `UserMemoryConsolidationState`

Important persistence notes:
- `ChatTurn` stores normalized user/assistant messages plus a `vector(768)` embedding (filled by `ChatTurnEmbeddingJob`) and is searched semantically via pgvector cosine distance. The old full-text `search_vector` column still exists but is unused
- `ChatTurn.MemoryProcessedAt` is the memory extraction work queue (`NULL` = not extracted yet)
- Memory is stored as `UserMemoryItem` rows: `Text`, `Category` (fixed set in `UserMemoryItemCategories`), `IsCore`, `Status` (`active`, `superseded`, `deleted`), `Embedding`, `SourceTurnIds`, `SupersededById`, `ChangeReason`, `LastConfirmedAt`. All pgvector queries for them live in `MemoryItemService`
- `UserMemoryManifest` rows are kept (read once for the import) but no longer written
- `DeferredIntent.Status` values are `pending`, `scheduled`, `recurring`, `completed`, `cancelled`, `failed`
- `UserMemoryConsolidationState` tracked the legacy consolidation progress per user and is no longer updated

### Testing Notes

- Tests live under `Assistant.Api.Tests/`
- Memory item tests fake `IMemoryItemService` for vector lookups, because the InMemory provider can't run `CosineDistance` (`Embedding` is ignored for non-Npgsql providers in `ApplicationDbContext`)
- Legacy manifest tests target `SaveManifestAsync`, `GetActiveManifestAsync`, `UpdateMemoryManifest`
- For EF-backed service tests, this repo commonly uses `UseInMemoryDatabase`. InMemory database names are shared across all test classes, so prefix them with the class name to avoid clashes between tests with the same method name

### Configuration

Required secrets (via `dotnet user-secrets` or environment variables):

```
Bot:BotToken
Bot:WebhookUrl
Bot:SecretToken
Bot:AllowedChatIds
AIProviders:OpenRouter:ApiKey
AIProviders:XAI:ApiKey
AIProviders:GoogleAIStudio:ApiKey   # only if you re-enable WebSearchToolFunctions
ConnectionStrings:PostgreSQL
ConnectionStrings:HangfireDb
Ruvio:Host / Ruvio:Port / Ruvio:Password   # agent session store
```

`AIProviders:DefaultTimeZoneId` controls timezone for scheduled tasks (default: `Europe/Istanbul`).
