# Assistant
### Personal Telegram Bot (for fun and learning)

## Purpose
Assistant is a personal Telegram bot project built mainly for entertainment, experimentation, and learning.

At this stage, it is not intended to be a production-grade or public SaaS product.

## Current Status
- The implementation is intentionally minimal.
- A clean and extensible command infrastructure is already in place.
- Current command support includes startup, natural-language reminder creation, and expense statement analysis.
- Credit card statement PDFs can now be analyzed and persisted as billing-period expense summaries.
- Incoming Telegram updates are queued and processed in the background via Hangfire.
- A Hangfire recurring job is configured to send end-of-workday reminders to registered users.
- Long-term user memories now store Gemini embeddings in PostgreSQL via `pgvector`, enabling semantic recall instead of only recency-based retrieval.
- AI-backed turns now combine personality context, semantically matched memories, and a rolling chat summary to keep longer conversations coherent.
- A memory-maintenance pipeline has been added to archive stale memories and consolidate highly similar durable memories.

## Commands
The bot currently supports the following commands:

| Command | Description |
| --- | --- |
| `/start` | Starts the assistant and sends a welcome message. |
| `/remind` | Creates one-time or recurring reminders from natural language input (private chat only). |
| `/expense` | Shows the user's current expense summary and can analyze uploaded credit card statement PDFs. |

Bot commands are registered with Telegram during application startup via `SetMyCommands`.

Example:
- `/remind 5 saat sonra Mustafa abiyle toplantımı hatırlat`
- `/expense`

Expense flow:
- Run `/expense` in a private chat to see the currently saved total expense amount.
- Upload a credit card statement PDF with the `/expense` command caption to trigger analysis.
- The bot extracts statement text through Markitdown, asks the AI agent for a single billing-period summary, and saves that result to the `Expenses` table.

## Scheduled Jobs (Hangfire)
The project includes a recurring Hangfire job:

| Job ID | Schedule (Cron) | Time Zone | Description |
| --- | --- | --- | --- |
| `workday-end-reminder` | `0 14 * * 1-5` | UTC | Runs every weekday at 14:00 UTC and sends a workday-end reminder message to all users in `TelegramUsers`. |

Implementation notes:
- `WorkdayEndReminderJob` fetches chat IDs from the database and sends reminders one-by-one via `ITelegramBotClient`.
- Delivery failures are logged per chat; successful and failed counts are summarized at the end of each run.
- The recurring schedule is registered at application startup in `UseHangfireRecurringJobs`.
- `CommandUpdateJob` is also registered as a Hangfire background job and is used to process incoming Telegram updates asynchronously.
- `MemoryMaintenanceJob` and `MemoryMaintenanceService` are now implemented for nightly semantic-memory cleanup, but the recurring `AddOrUpdate(...)` registration is currently commented out.

## Architecture Overview
- `IBotCommand`
  - Defines the command contract: `Command`, `Description`, and `ExecuteAsync(...)`.
- `BotCommandFactory`
  - Resolves command handlers by command name.
- `CommandUpdateHandler`
  - Parses incoming updates, extracts the command text from either message text or caption, resolves the handler from the factory, executes it, and logs errors.
- `BotController`
  - Receives webhook updates, validates the Telegram secret token, checks allowed chat IDs, and enqueues accepted updates to Hangfire for background processing.
- `AgentService`
  - Builds AI-backed conversations with personality context, semantically relevant memories, and reduced chat history before calling the model.
- `ExpenseCommand`
  - Returns the user's current expense total or handles statement PDF uploads for automated expense registration.
- `ExpenseAnalysisService`
  - Sends uploaded PDFs to Markitdown for text extraction, invokes the AI agent with tool-calling, and persists a single billing-period expense summary.
- `EmbeddingService`
  - Generates Gemini document/query embeddings and normalizes vectors before persistence and search.
- `MemoryService`
  - Persists memory embeddings and status metadata, backfills missing embeddings, and performs cosine-distance semantic search for the current user input.
- `AssistantChatReducer`
  - Replaces older chat turns with a rolling assistant summary while preserving recent turns verbatim.
- `MemoryMaintenanceService`
  - Archives stale memories, skips time-bound or contradictory clusters, and asks Gemini to merge durable near-duplicate memories.

## How It Works (Request Flow)
1. Telegram sends an update to `POST /bot/update`.
2. The request secret token is validated in `BotController`.
3. If configured, `BotController` checks `Bot:AllowedChatIds` and rejects unauthorized chats.
4. `BotController` enqueues the accepted update as a Hangfire background job.
5. `CommandUpdateJob` invokes `CommandUpdateHandler`.
6. `CommandUpdateHandler` extracts the `/command` from the incoming text or caption.
7. `BotCommandFactory` resolves the matching command handler.
8. The command executes and sends responses through `ITelegramBotClient`.

For AI-backed flows, `AgentService` injects personality context, semantically relevant long-term memories, and reduced chat history before invoking the model.

## Quick Start
### Prerequisites
- .NET 8 SDK
- A Telegram bot token
- A Google Gemini API key
- A PostgreSQL database with `pgvector` support available
- A Markitdown service endpoint for PDF-to-markdown conversion
- A webhook URL reachable by Telegram
- A secret token for webhook verification

### Configuration
Set the `Bot` and `AI` sections in `Assistant.Api/appsettings.Development.json`:

```json
{
  "Bot": {
    "BotToken": "YOUR_BOT_TOKEN",
    "WebhookUrl": "YOUR_WEBHOOK_URL",
    "SecretToken": "YOUR_SECRET_TOKEN",
    "AllowedChatIds": []
  },
  "AI": {
    "GoogleApiKey": "YOUR_GOOGLE_GEMINI_API_KEY",
    "Model": "gemini-3.1-flash-lite-preview",
    "EmbeddingModel": "gemini-embedding-2-preview",
    "EmbeddingDimensions": 768,
    "MemoryMaintenanceModel": "gemini-3.1-flash-lite-preview",
    "MemoryArchiveAfterDaysLow": 30,
    "MemoryArchiveAfterDaysMedium": 90,
    "MemoryConsolidationCron": "0 3 * * *",
    "DefaultTimeZoneId": "Europe/Istanbul"
  },
  "Markitdown": {
    "Endpoint": "YOUR_MARKITDOWN_ENDPOINT"
  }
}
```

Also configure database connection strings in the same file:

```json
{
  "ConnectionStrings": {
    "PostgreSQL": "Host=...;Port=5432;Database=...;Username=...;Password=...",
    "HangfireDb": "Host=...;Port=5432;Database=...;Username=...;Password=..."
  }
}
```

The semantic-memory migration enables the PostgreSQL `vector` extension and adds a `vector(768)` embedding column to `user_memories`, so the target PostgreSQL instance must have `pgvector` installed.

### Run
```bash
dotnet restore
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
│   ├── Services/
│   │   ├── Abstracts/
│   │   └── Concretes/
│   ├── Extensions/
│   └── Domain/
│       ├── Configurations/
│       └── Entities/
└── Assistant.sln
```

## Roadmap / Upcoming Features
Near-term focus areas:
- Richer expense reporting and breakdowns on top of persisted billing-period summaries
- Additional document-driven workflows using the existing background processing pipeline
