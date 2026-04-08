# Assistant
### Personal Telegram Bot (for fun and learning)

## Purpose
Assistant is a personal Telegram bot project built mainly for entertainment, experimentation, and learning.

At this stage, it is not intended to be a production-grade or public SaaS product.

## Current Status
- The implementation is intentionally minimal.
- A clean and extensible command infrastructure is already in place.
- Current command support includes startup, free-form chat/task handling, live TEFAS fund analysis, and expense statement analysis.
- The chat agent now keeps lightweight long-term user memory with optional expiry for time-bound facts such as trips, short-lived plans, and temporary constraints.
- Successful `/chat` turns are persisted and recalled through a lightweight chat-history RAG flow backed by PostgreSQL full-text search, so the agent can pull relevant older conversation snippets when needed.
- The chat agent can trigger live web search for fresh information such as prices, schedules, releases, and other time-sensitive public facts.
- Credit card statement PDFs can now be analyzed and persisted as billing-period expense summaries.
- TEFAS fund pages can now be fetched live, normalized into structured data, and summarized through the agent with a deterministic fallback.
- Incoming Telegram updates are queued and processed in the background via Hangfire.
- A Hangfire recurring job is configured to send end-of-workday reminders to registered users.

## Commands
The bot currently supports the following commands:

| Command | Description |
| --- | --- |
| `/start` | Starts the assistant and sends a welcome message. |
| `/chat` | General-purpose chat entrypoint. The agent can answer questions, remember useful personal context, create reminders/tasks from natural language, reuse relevant older chat turns, and use live web search when the answer depends on fresh public information. |
| `/expense` | Shows the user's current expense summary and can analyze uploaded credit card statement PDFs. |
| `/tefas` | Fetches live TEFAS fund data for a fund code and returns a short AI-generated fund summary. |

Bot commands are registered with Telegram during application startup via `SetMyCommands`.

Example:
- `/chat 5 saat sonra Mustafa abiyle toplantımı hatırlat`
- `/chat NVIDIA stock price current`
- `/expense`
- `/tefas AFT`

Chat flow:
- The agent combines recent session history, saved long-term memory, pending tasks, and relevant older persisted chat turns before answering.
- Time-bound memories can expire without being treated as current forever; expired items remain available as past context instead of active facts.
- Older `/chat` turns are stored in `chat_turns` and searched through a lightweight chat-history RAG layer using PostgreSQL full-text search with a generated `tsvector` plus GIN index.

Expense flow:
- Run `/expense` in a private chat to see the currently saved total expense amount.
- Upload a credit card statement PDF with the `/expense` command caption to trigger analysis.
- The bot sends the uploaded PDF to the OpenRouter file parser flow, validates the returned structured JSON, and saves normalized transactions to the `Expenses` table.

TEFAS flow:
- Run `/tefas <FUND_CODE>` such as `/tefas AFT`.
- The bot fetches `https://www.tefas.gov.tr/FonAnaliz.aspx?FonKod=<FUND_CODE>` live on each request.
- Summary metrics, return windows, fund profile fields, asset allocation, one-year comparison data, and the latest available TEFAS date are parsed and normalized.
- That structured snapshot is passed to the existing agent service with TEFAS-specific instructions so the answer stays grounded in the scraped data.
- If the agent fails, the bot still returns a deterministic fallback summary based on the parsed TEFAS snapshot.

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
  - Sends uploaded PDFs to OpenRouter for structured PDF extraction, validates the JSON output, and persists normalized expense transactions.
- `TefasCommand`
  - Accepts `/tefas <code>`, validates the input, invokes the TEFAS analysis service, and sends the response back to Telegram.
- `TefasAnalysisService`
  - Fetches the TEFAS analysis page live, parses and normalizes the fund snapshot, invokes the AI agent with TEFAS-specific augmentation, and falls back to a deterministic summary if needed.
- `TefasHtmlParser`
  - Uses DOM parsing plus targeted extraction of embedded chart data to turn the TEFAS HTML page into a structured fund snapshot.
- `AgentService`
  - Builds the chat agent, registers tools, injects personality/memory/task context, and runs the chat-history RAG retrieval step over persisted chat turns before each response.
- `MemoryContextProvider`
  - Injects active user memories plus expired time-bound memories that should be treated as past context rather than current facts.
- `ChatTurnService`
  - Persists successful `/chat` turns and searches older turns with PostgreSQL full-text ranking for the chat-history RAG recall step.
- `WebSearchToolFunctions`
  - Executes Google AI Studio-backed web searches for fresh public information and returns a concise grounded summary back to the chat agent.
- `TelegramResponseSender`
  - Centralizes long Telegram message splitting and Markdown fallback handling for agent-style responses.

## How It Works (Request Flow)
1. Telegram sends an update to `POST /bot/update`.
2. The request secret token is validated in `BotController`.
3. If configured, `BotController` checks `Bot:AllowedChatIds` and rejects unauthorized chats.
4. `BotController` enqueues the accepted update as a Hangfire background job.
5. `CommandUpdateJob` invokes `CommandUpdateHandler`.
6. `CommandUpdateHandler` extracts the `/command` from the incoming text or caption.
7. `BotCommandFactory` resolves the matching command handler.
8. For `/chat`, the agent session is invoked with personality context, saved memories, pending tasks, and full-text retrieval over persisted prior chat turns.
9. The command executes and sends its response either through `TelegramResponseSender` or directly through `ITelegramBotClient`, depending on the command path.

## Quick Start
### Prerequisites
- .NET 10 SDK
- PostgreSQL
- A Telegram bot token
- A Google AI Studio API key
- Outbound access to `https://www.tefas.gov.tr/` for live fund scraping
- A webhook URL reachable by Telegram
- A secret token for webhook verification

### Configuration
Set the `Bot` and `AIProviders` sections in `Assistant.Api/appsettings.Development.json`:

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
      "Model": "google/gemini-3.1-flash-lite-preview"
    },
    "GoogleAIStudio": {
      "ApiKey": "YOUR_GOOGLE_AI_STUDIO_API_KEY",
      "Model": "gemini-3.1-flash-lite-preview"
    },
    "DefaultTimeZoneId": "Europe/Istanbul"
  }
}
```

Provider notes:
- `AIProviders:GoogleAIStudio` is currently used by the main chat agent, the live web search flow, and PDF-based expense extraction.
- `AIProviders:OpenRouter` is currently kept in configuration only and is not used by the active request paths.
- `AIProviders:DefaultTimeZoneId` is shared by time-sensitive chat, scheduling, and time-bound memory handling.

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
│   ├── Features/
│   │   ├── Chat/
│   │   ├── Expense/
│   │   ├── Tefas/
│   │   └── UserManagement/
│   ├── Services/
│   │   ├── Abstracts/
│   │   └── Concretes/
│   ├── Extensions/
│   └── Domain/
│       ├── Configurations/
│       └── Entities/
├── Assistant.Api.Tests/
│   └── Tefas/
└── Assistant.sln
```

## Roadmap / Upcoming Features
Near-term focus areas:
- Richer expense reporting and breakdowns on top of persisted billing-period summaries
- Additional document-driven workflows using the existing background processing pipeline
- Broader investment and finance workflows on top of the new TEFAS ingestion path
