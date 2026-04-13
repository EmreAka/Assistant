# Assistant
### Personal Telegram Bot (for fun and learning)

## Purpose
Assistant is a personal Telegram bot project built mainly for entertainment, experimentation, and learning.

At this stage, it is not intended to be a production-grade or public SaaS product.

## Current Status
- The implementation is still intentionally small, but the core command and agent infrastructure is already in place.
- The bot currently supports `/start`, `/chat`, `/expense`, and `/tefas`.
- Plain text messages without a slash command are routed to the `chat` command automatically.
- The chat agent keeps a versioned long-term user memory manifest and can update it through tool calling.
- Successful chat turns are persisted and recalled through PostgreSQL full-text search so the agent can pull relevant older conversation snippets.
- The chat agent can schedule, list, cancel, and reschedule deferred tasks and reminders through Hangfire-backed tools.
- The chat agent can trigger live web search for fresh information and query previously imported expenses from the database.
- Credit card statement PDFs can be analyzed, normalized, and persisted as expense transactions.
- TEFAS fund pages are fetched live, parsed into structured snapshots, and summarized through the agent with a deterministic fallback.
- Incoming Telegram updates and deferred tasks are processed in the background via Hangfire.

## Commands
The bot currently supports the following commands:

| Command | Description |
| --- | --- |
| `/start` | Registers the Telegram user and sends a welcome message. |
| `/chat` | General-purpose chat entrypoint. The agent can answer questions, remember useful personal context, manage reminders/tasks, search the web when needed, and query saved expenses. |
| `/expense` | Shows a deterministic expense summary and handles uploaded credit card statement PDFs. |
| `/tefas` | Fetches live TEFAS fund data for a fund code and returns a short fund summary. |

Bot commands are registered with Telegram during application startup.

Examples:
- `/chat 5 saat sonra Mustafa abiyle toplantımı hatırlat`
- `/chat NVIDIA stock price current`
- `yarın sabah 9'da su içmeyi hatırlat`
- `/expense`
- `/tefas AFT`

Chat flow:
- The agent combines recent session history, the active memory manifest, pending tasks, and relevant persisted chat turns before answering.
- Memory is stored as versioned `UserMemoryManifest` records rather than individual memory rows.
- Older chat turns are stored in `chat_turns` and searched through PostgreSQL full-text search.
- Agent tools currently include web search, memory manifest update, task scheduling, current time lookup, and expense querying.

Expense flow:
- Run `/expense` in a private chat to see the currently saved deterministic expense summary.
- Upload a credit card statement PDF with the `/expense` command caption to trigger analysis.
- The bot sends the uploaded PDF to Google Gen AI for structured extraction, validates the result, and saves normalized transactions to the `Expenses` table.
- Expense questions such as totals, merchants, or period breakdowns are primarily handled in normal chat through the `QueryExpenses` tool.

TEFAS flow:
- Run `/tefas <FUND_CODE>` such as `/tefas AFT`.
- The bot fetches `https://www.tefas.gov.tr/FonAnaliz.aspx?FonKod=<FUND_CODE>` live on each request.
- Summary metrics, return windows, fund profile fields, asset allocation, one-year comparison data, and the latest available TEFAS date are parsed and normalized.
- That structured snapshot is passed to the agent with TEFAS-specific instructions so the answer stays grounded in the scraped data.
- If the agent fails, the bot still returns a deterministic fallback summary based on the parsed TEFAS snapshot.

## Background Jobs (Hangfire)
Hangfire is currently used for two job types:

| Job | Trigger | Description |
| --- | --- | --- |
| `CommandUpdateJob` | On each accepted Telegram webhook update | Processes incoming Telegram updates asynchronously. |
| `DeferredIntentDispatchJob` | Created dynamically for one-time or recurring deferred intents | Wakes the agent up later to execute scheduled reminders/tasks. |

Implementation notes:
- Incoming Telegram updates are enqueued from `BotController`.
- One-time deferred tasks are scheduled with `IBackgroundJobClient.Schedule`.
- Recurring deferred tasks are registered dynamically with `IRecurringJobManager.AddOrUpdate`.
- There is no fixed startup-time recurring reminder job documented as part of the active runtime flow anymore.

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
  - Invokes `AgentService`, persists successful chat turns, and sends responses through `TelegramResponseSender`.
- `AgentService`
  - Builds the `ChatClientAgent`, registers tools, injects personality/memory/task context, and runs chat-history lookup over persisted chat turns before each response.
- `MemoryContextProvider`
  - Injects the active `UserMemoryManifest` into the chat agent context.
- `MemoryToolFunctions`
  - Exposes the tool that updates the user's memory manifest.
- `TaskToolFunctions`
  - Exposes task scheduling, listing, cancellation, and rescheduling tools backed by `DeferredIntent` plus Hangfire.
- `ChatTurnService`
  - Persists successful chat turns and searches older turns with PostgreSQL full-text ranking for recall.
- `WebSearchToolFunctions`
  - Executes Google AI Studio-backed web searches for fresh public information and returns grounded text back to the chat agent.
- `ExpenseCommand`
  - Returns the user's current expense summary or handles statement PDF uploads for automated expense registration.
- `ExpenseAnalysisService`
  - Sends uploaded PDFs to Google Gen AI for structured extraction, validates the response, and persists normalized expense transactions.
- `ExpenseQueryToolFunctions`
  - Queries saved expenses for totals, merchants, statement periods, and grouped summaries.
- `TefasCommand`
  - Accepts `/tefas <code>`, validates the input, invokes the TEFAS analysis service, and sends the response back to Telegram.
- `TefasAnalysisService`
  - Fetches the TEFAS analysis page live, parses and normalizes the fund snapshot, invokes the AI agent with TEFAS-specific augmentation, and falls back to a deterministic summary if needed.
- `TefasHtmlParser`
  - Uses DOM parsing plus targeted extraction of embedded chart data to turn the TEFAS HTML page into a structured fund snapshot.
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
8. For chat requests, the agent session is invoked with personality context, the active memory manifest, pending tasks, and full-text retrieval over persisted prior chat turns.
9. The command executes and sends its response either through `TelegramResponseSender` or directly through `ITelegramBotClient`, depending on the command path.

## Quick Start
### Prerequisites
- .NET 10 SDK
- PostgreSQL
- A Telegram bot token
- An xAI API key
- A Google AI Studio API key
- Outbound access to `https://www.tefas.gov.tr/` for live fund scraping
- A webhook URL reachable by Telegram
- A secret token for webhook verification

Optional:
- An OpenRouter API key if you want to keep the optional provider config populated

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
    "XAI": {
      "ApiKey": "YOUR_XAI_API_KEY",
      "ApiUrl": "https://api.x.ai/v1",
      "Model": "grok-4-1-fast-reasoning"
    },
    "DefaultTimeZoneId": "Europe/Istanbul"
  }
}
```

Provider notes:
- `AIProviders:XAI` is the main chat/agent provider used by `AgentService`.
- `AIProviders:GoogleAIStudio` is used for live web search and PDF-based expense extraction.
- `AIProviders:OpenRouter` remains configured in the project, but it is not the main active chat path right now.
- `AIProviders:DefaultTimeZoneId` is shared by time-sensitive chat behavior and deferred task scheduling.

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
│   │   ├── Expense/
│   │   ├── Tefas/
│   │   └── UserManagement/
│   ├── Services/
│   │   ├── Abstracts/
│   │   └── Concretes/
│   └── Screens/
├── Assistant.Api.Tests/
│   ├── Chat/
│   ├── Expense/
│   ├── Tefas/
│   ├── UserManagement/
│   └── Fixtures/
└── Assistant.sln
```

## Roadmap / Upcoming Features
Near-term focus areas:
- Richer expense reporting and breakdowns on top of persisted billing-period summaries
- Hardening the memory and deferred-task flows as the agent surface grows
- Broader investment and finance workflows on top of the TEFAS ingestion path
