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

## Architecture Overview
- `IBotCommand`
  - Defines the command contract: `Command`, `Description`, and `ExecuteAsync(...)`.
- `BotCommandFactory`
  - Resolves command handlers by command name.
- `CommandUpdateHandler`
  - Parses incoming updates, extracts the command text from either message text or caption, resolves the handler from the factory, executes it, and logs errors.
- `BotController`
  - Receives webhook updates, validates the Telegram secret token, checks allowed chat IDs, and enqueues accepted updates to Hangfire for background processing.
- `ExpenseCommand`
  - Returns the user's current expense total or handles statement PDF uploads for automated expense registration.
- `ExpenseAnalysisService`
  - Sends uploaded PDFs to Markitdown for text extraction, invokes the AI agent with tool-calling, and persists a single billing-period expense summary.

## How It Works (Request Flow)
1. Telegram sends an update to `POST /bot/update`.
2. The request secret token is validated in `BotController`.
3. If configured, `BotController` checks `Bot:AllowedChatIds` and rejects unauthorized chats.
4. `BotController` enqueues the accepted update as a Hangfire background job.
5. `CommandUpdateJob` invokes `CommandUpdateHandler`.
6. `CommandUpdateHandler` extracts the `/command` from the incoming text or caption.
7. `BotCommandFactory` resolves the matching command handler.
8. The command executes and sends responses through `ITelegramBotClient`.

## Quick Start
### Prerequisites
- .NET 8 SDK
- A Telegram bot token
- A Google Gemini API key
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
    "GoogleApiKey": "YOUR_GOOGLE_GEMINI_API_KEY"
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
