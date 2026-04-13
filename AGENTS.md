# AGENTS.md

This file provides guidance when working with code in this repository.

## Project Overview

A personal Telegram bot built with ASP.NET Core (.NET 10) that provides AI-powered chat, expense tracking, and Turkish mutual fund (TEFAS) analysis. Uses Hangfire for async job processing and Microsoft.Agents.AI for orchestrating AI interactions with tool calling.

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
3. **CommandUpdateHandler** parses command text → **BotCommandFactory** resolves the **IBotCommand**
4. Command executes (e.g., ChatCommand → AgentService, ExpenseCommand → ExpenseAnalysisService)

### AI Agent Pattern

`AgentService` builds a `ChatClientAgent` (Microsoft.Agents.AI) with:
- **Context providers** (`IAIContextProvider`): personality, memory, pending tasks injected into system prompt
- **AI tools** registered via `AIFunctionFactory.Create()`: web search, save memory, schedule task, get time, query expenses
- **SummarizingChatReducer** to manage chat history window
- Session state cached per chat ID in a `ConcurrentDictionary`

Two AI clients are used:
- **OpenRouter** (Google Gemini via OpenAI-compatible API) — main chat/agent
- **Google AI Studio** — web search tool via `WebSearchToolFunctions`

### Feature Structure

Features in `Assistant.Api/Features/` are self-contained slices:
- `Chat/` — agent service, tool functions, ChatCommand
- `Expense/` — PDF analysis via AI, expense query tools, ExpenseCommand
- `Tefas/` — HTML scraping of `tefas.gov.tr`, chart data parsing, TefasCommand
- `UserManagement/` — personality profile, user memory, DeferredIntent (scheduled tasks)

### Background Jobs (Hangfire)

| Job | Trigger |
|-----|---------|
| `CommandUpdateJob` | On each incoming Telegram update |
| `DeferredIntentDispatchJob` | Runs scheduled/recurring user tasks |

Hangfire uses PostgreSQL storage. Dashboard at `/hangfire` in development.

### Database (EF Core + PostgreSQL)

Key entities: `TelegramUser`, `Expense`, `UserMemory`, `AssistantPersonality`, `DeferredIntent`

`DeferredIntent.Status` enum: `Pending → Scheduled/Recurring → Completed/Failed`

### Configuration

Required secrets (via `dotnet user-secrets` or environment variables):

```
Bot:BotToken
Bot:WebhookUrl
Bot:SecretToken
Bot:AllowedChatIds
AIProviders:OpenRouter:ApiKey
AIProviders:GoogleAIStudio:ApiKey
ConnectionStrings:PostgreSQL
ConnectionStrings:HangfireDb
```

`AIProviders:DefaultTimeZoneId` controls timezone for scheduled tasks (default: `Europe/Istanbul`).

User secrets ID: `35406df2-6a87-433c-918c-1d6f31ce342a`
