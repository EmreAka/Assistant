# AGENTS.md

This file provides guidance when working with code in this repository.

## Project Overview

A personal Telegram bot built with ASP.NET Core (.NET 10) that provides AI-powered chat, PDF statement expense extraction/querying, deferred tasks/reminders, and Turkish mutual fund (TEFAS) analysis. Uses Hangfire for background execution and Microsoft.Agents.AI for tool-calling orchestration.

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
   - `ExpenseCommand` runs statement analysis / expense workflows
   - `TefasCommand` runs TEFAS analysis
   - `MemoryCommand` shows the active memory manifest
5. Successful chat replies are persisted by **ChatTurnService** for later semantic recall

### AI Agent Pattern

`AgentService` builds a `ChatClientAgent` (Microsoft.Agents.AI) with:
- **Context providers**: personality, memory manifest, pending tasks, and chat-history search context
- **AI tools** registered via `AIFunctionFactory.Create()`: web search, schedule/list/cancel/reschedule tasks, get current time, query expenses, math calculation (`Calculate`)
- **SummarizingChatReducer** to manage chat history window
- Session state cached per chat ID in a `ConcurrentDictionary`

**Memory Consolidation**: Instead of inline memory updates via tools, a background process handled by `MemoryConsolidationAgentService` aggregates recent chat turns and uses an AI model with specific instructions to merge them into a single `UserMemoryManifest`.

Current AI provider usage:
- **xAI** (`AIProviders:XAI`) — main chat/agent model used by `AgentService` and memory consolidation.
- **Google AI Studio** — web search and PDF expense extraction
- **OpenRouter** — configured in options/DI for optional integrations, but not the main agent execution path right now

### Feature Structure

Features in `Assistant.Api/Features/` are self-contained slices:
- `Chat/` — `AgentService`, tool functions (task, time, math, search), `ChatCommand`, deferred task dispatch, chat-turn storage/search
- `Expense/` — PDF analysis via Google Gen AI, expense persistence/query tools, `ExpenseCommand`
- `Tefas/` — HTML scraping of `tefas.gov.tr`, structured parsing, agent/fallback analysis, `TefasCommand`
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

Key entities: `TelegramUser`, `AssistantPersonality`, `ChatTurn`, `Expense`, `UserMemoryManifest`, `DeferredIntent`, `UserMemoryConsolidationState`

Important persistence notes:
- `ChatTurn` stores normalized user/assistant messages and is searched via PostgreSQL full-text search
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
AIProviders:XAI:ApiKey
AIProviders:GoogleAIStudio:ApiKey
AIProviders:OpenRouter:ApiKey
ConnectionStrings:PostgreSQL
ConnectionStrings:HangfireDb
```

`AIProviders:DefaultTimeZoneId` controls timezone for scheduled tasks (default: `Europe/Istanbul`).
