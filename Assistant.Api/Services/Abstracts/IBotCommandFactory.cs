namespace Assistant.Api.Services.Abstracts;

public interface IBotCommandFactory
{
    IBotCommand? GetCommand(string command);
    IEnumerable<IBotCommand> GetAllCommands();
}