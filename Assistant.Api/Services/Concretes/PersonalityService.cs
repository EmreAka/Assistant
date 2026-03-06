using Assistant.Api.Data;
using Assistant.Api.Services.Abstracts;
using Microsoft.EntityFrameworkCore;

namespace Assistant.Api.Services.Concretes;

public class PersonalityService(
    ApplicationDbContext dbContext
) : IPersonalityService
{
    public async Task<string?> GetPersonalityTextAsync(long chatId, CancellationToken cancellationToken)
    {
        return await dbContext.TelegramUsers
            .AsNoTracking()
            .Where(x => x.ChatId == chatId)
            .Select(x => x.AssistantPersonality != null ? x.AssistantPersonality.PersonalityText : null)
            .FirstOrDefaultAsync(cancellationToken);
    }
}
