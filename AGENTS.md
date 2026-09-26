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
   - `MemoryCommand` shows the active memory manifest
5. Successful chat replies are persisted by **ChatTurnService** for later semantic recall

### AI Agent Pattern

`AgentService` builds a `ChatClientAgent` (Microsoft.Agents.AI) with:
- **Context providers**: personality, memory manifest, pending tasks, and chat-history search context
- **AI tools** registered via `AIFunctionFactory.Create()`: schedule/list/cancel/reschedule tasks, get current time, math calculation (`Calculate`)
- **OpenRouter web search**: the `openrouter:web_search` server tool is patched into the outgoing `tools` array by `OpenRouterOptions.CreateRawChatCompletionOptions()` (wired through `ChatOptions.RawRepresentationFactory`). OpenRouter runs the search server-side, so there is no local web search tool function
- **Reasoning effort per agent**: `AIProviders:OpenRouter:Reasoning` (`Chat`, `MemoryConsolidation`, `ChatSummarization`) is applied through `ChatOptions.Reasoning`, which the OpenAI adapter sends as `reasoning_effort` (OpenRouter's shorthand for `reasoning.effort`)
- **SummarizingChatReducer** to manage chat history window
- Session state (including the in-memory chat history) is serialized with `SerializeSessionAsync` and stored per chat ID in **Ruvio** (Redis-protocol store) by `RuvioAgentSessionStore` under `assistant:agent-session:{chatId}`, so it survives app restarts. The `RuvioClient` singleton is registered with `Ruvio.Client.AspNetCore` (`AddRuvioClient`, `Ruvio` config section). If Ruvio can't be read, the turn runs on a fresh session that is not saved, so stored history is never overwritten

**Memory Consolidation**: Instead of inline memory updates via tools, a background process handled by `MemoryConsolidationAgentService` aggregates recent chat turns and uses an AI model with specific instructions to merge them into a single `UserMemoryManifest`.

Current AI provider usage:
- **OpenRouter** (`AIProviders:OpenRouter`) — main chat/agent model (`google/gemini-3.1-flash-lite`) used by `AgentService` and memory consolidation, plus server-side web search.
- **xAI** (`AIProviders:XAI`) — text-to-speech only (`/tts`).
- **Google AI Studio** (`AIProviders:GoogleAIStudio`) — kept for optional/experimental use. `WebSearchToolFunctions` and `CreateGoogleGenAIChatClient()` still exist but are not registered on any active path.

### Feature Structure

Features in `Assistant.Api/Features/` are self-contained slices:
- `Chat/` — `AgentService`, tool functions (task, time, math, plus the unregistered `WebSearchToolFunctions`), `ChatCommand`, deferred task dispatch, chat-turn storage/search
- `UserManagement/` — `StartCommand`, `MemoryCommand`, personality profile, Telegram user registration, memory manifest persistence, and memory consolidation jobs.

Legacy cross-cutting infrastructure still lives outside the feature folders:
- `Services/Concretes/` — command routing and update handling
- `Extensions/` — DI registration, Hangfire setup, AI option/client helpers

### Background Jobs (Hangfire)

| Job | Trigger |
|-----|---------|
| `CommandUpdateJob` | On each incoming Telegram update |
| `DeferredIntentDispatchJob` | Executes scheduled/recurring user tasks through `AgentService` |
| `MemoryConsolidationJob` | Asynchronously triggered when pending chat turns exceed a threshold |

Hangfire uses PostgreSQL storage. Dashboard at `/hangfire` in development.

### Database (EF Core + PostgreSQL)

Key entities: `TelegramUser`, `AssistantPersonality`, `ChatTurn`, `UserMemoryManifest`, `DeferredIntent`, `UserMemoryConsolidationState`

Important persistence notes:
- `ChatTurn` stores normalized user/assistant messages plus a `vector(768)` embedding (filled by `ChatTurnEmbeddingJob`) and is searched semantically via pgvector cosine distance. The old full-text `search_vector` column still exists but is unused
- Memory is stored as versioned `UserMemoryManifest` rows
- `DeferredIntent.Status` values are `pending`, `scheduled`, `recurring`, `completed`, `cancelled`, `failed`
- `UserMemoryConsolidationState` tracks the background memory consolidation progress per user

### Testing Notes

- Tests live under `Assistant.Api.Tests/`
- Memory tests target the manifest-based API (`SaveManifestAsync`, `GetActiveManifestAsync`, `UpdateMemoryManifest`)
- For EF-backed service tests, this repo commonly uses `UseInMemoryDatabase`

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
