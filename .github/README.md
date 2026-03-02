# Assistant
### Personal Telegram Bot (for fun and learning)

## Purpose
Assistant is a personal Telegram bot project built mainly for entertainment, experimentation, and learning.

At this stage, it is not intended to be a production-grade or public SaaS product.

## Current Status
- The implementation is intentionally minimal.
- A clean and extensible command infrastructure is already in place.
- Current command support is limited to basic bot startup behavior.
- A Hangfire recurring job is configured to send end-of-workday reminders to registered users.

## Commands
The bot currently supports the following command:

| Command | Description |
| --- | --- |
| `/start` | Starts the assistant and sends a welcome message. |

Bot commands are registered with Telegram during application startup via `SetMyCommands`.

## Scheduled Jobs (Hangfire)
The project includes a recurring Hangfire job:

| Job ID | Schedule (Cron) | Time Zone | Description |
| --- | --- | --- | --- |
| `workday-end-reminder` | `0 14 * * 1-5` | UTC | Runs every weekday at 14:00 UTC and sends a workday-end reminder message to all users in `TelegramUsers`. |

Implementation notes:
- `WorkdayEndReminderJob` fetches chat IDs from the database and sends reminders one-by-one via `ITelegramBotClient`.
- Delivery failures are logged per chat; successful and failed counts are summarized at the end of each run.
- The recurring schedule is registered at application startup in `UseHangfireRecurringJobs`.

## Architecture Overview
- `IBotCommand`
  - Defines the command contract: `Command`, `Description`, and `ExecuteAsync(...)`.
- `BotCommandFactory`
  - Resolves command handlers by command name.
- `CommandUpdateHandler`
  - Parses incoming updates, extracts the command text, resolves the handler from the factory, executes it, and logs errors.
- `BotController`
  - Receives webhook updates, validates the Telegram secret token, checks allowed chat IDs, and forwards updates to the command update handler.

## How It Works (Request Flow)
1. Telegram sends an update to `POST /bot/update`.
2. The request secret token is validated in `BotController`.
3. If configured, `BotController` checks `Bot:AllowedChatIds` and rejects unauthorized chats.
4. `CommandUpdateHandler` extracts the `/command` from the incoming text message.
5. `BotCommandFactory` resolves the matching command handler.
6. The command executes and sends responses through `ITelegramBotClient`.

## Quick Start
### Prerequisites
- .NET 8 SDK
- A Telegram bot token
- A webhook URL reachable by Telegram
- A secret token for webhook verification

### Configuration
Set the `Bot` section in `Assistant.Api/appsettings.Development.json`:

```json
{
  "Bot": {
    "BotToken": "YOUR_BOT_TOKEN",
    "WebhookUrl": "YOUR_WEBHOOK_URL",
    "SecretToken": "YOUR_SECRET_TOKEN",
    "AllowedChatIds": [555322148]
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
│   ├── Services/
│   │   ├── Abstracts/
│   │   └── Concretes/
│   ├── Extensions/
│   └── Domain/
│       └── Configurations/
│           └── BotOptions.cs
└── Assistant.sln
```

## Roadmap / Upcoming Features
There are currently no functional feature modules documented yet.

Feature-specific documentation will be added as new capabilities are implemented.
