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
  - `Domain/`: Project-wide configuration options (AI, Bot, Markitdown).
  - `Extensions/`: Registration logic for dependency injection and middleware configuration (Bot, Database, Hangfire).
  - `Features/`: Core domain logic organized by feature:
    - `Chat/`: AI agent services, deferred intent handling, and tool functions for memory and tasks.
    - `Expense/`: Expense tracking models and analysis services.
    - `UserManagement/`: User entities (`TelegramUser`, `UserMemory`, `AssistantPersonality`) and management services.
  - `Screens/`: UI components and screens for the web interface (e.g., `Chat`, `Stitch`).
  - `Services/`: Infrastructure for the bot's command-driven architecture, including command factories and update handlers.
  - `wwwroot/`: Static web assets, including HTMX and CSS.
- `Assistant.Api.Tests/`: Contains unit and integration tests (Note: Per project rules, these should not be modified or run by the agent).
- `Assistant.sln`: The Visual Studio Solution file.
