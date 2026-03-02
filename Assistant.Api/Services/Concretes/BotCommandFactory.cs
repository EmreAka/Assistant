using Assistant.Api.Services.Abstracts;

namespace Assistant.Api.Services.Concretes;

public class BotCommandFactory(
    IEnumerable<IBotCommand> commands
) : IBotCommandFactory
{
    public IBotCommand? GetCommand(string command)
    {
        return commands.FirstOrDefault(x =>
            x.Command.Equals(command, StringComparison.OrdinalIgnoreCase));
    }

    public IEnumerable<IBotCommand> GetAllCommands()
    {
        return commands;
    }
}
