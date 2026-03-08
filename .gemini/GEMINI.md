# Project
This project is an assistant that helps users with various tasks. My goal is to learn Microsoft Agent Framework.

# Rules
- Don't add tests to my project. Don't run existing project tests.
- Use Microsof Learn MCP if you need anything about .NET, C#, or Microsoft Agent Framework.
- Use websearch if you need anything else like telegram.net library, Gemini & Grok AI APIs, or anything else.
- Generate migration via: dotnet ef migrations add MigrationName -o Data/Migrations under Assistant.Api project.
- Apply migration via: dotnet ef database update under Assistant.Api project.

# Project Structure
- `Assistant.Api/`: The main Web API project providing the assistant's functionality.
  - `Controllers/`: API endpoints, including the `BotController` for Telegram webhook handling.
  - `Data/`: Entity Framework Core database context, entity configurations, and migrations.
  - `Domain/`: Core domain logic, including:
    - `Entities/`: Database models like `TelegramUser`, `Reminder`, `Expense`, and `AssistantPersonality`.
    - `Configurations/`: Options classes for AI, Bot, and other settings.
    - `Dtos/`: Data Transfer Objects for internal and external communication.
  - `Extensions/`: Registration logic for dependency injection and middleware configuration (e.g., Hangfire, Bot services).
  - `Services/`: Core business logic and assistant capabilities.
    - `Abstracts/`: Service interfaces.
    - `Concretes/`: Implementation of bot commands (`StartCommand`, `RemindCommand`, `ExpenseCommand`), AI agents (`ReminderAgentService`), and background jobs.
- `Assistant.Api.Tests/`: Contains unit and integration tests (Note: Per project rules, these should not be modified or run by the agent).
- `Assistant.sln`: The Visual Studio Solution file.
