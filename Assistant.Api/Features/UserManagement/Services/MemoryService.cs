using Assistant.Api.Data;
using Assistant.Api.Features.UserManagement.Models;
using Microsoft.EntityFrameworkCore;

namespace Assistant.Api.Features.UserManagement.Services;

public class MemoryService(
    ApplicationDbContext dbContext
) : IMemoryService
{
    public async Task<string> GetActiveManifestAsync(long chatId, CancellationToken cancellationToken)
    {
        var manifest = await dbContext.UserMemoryManifests
            .AsNoTracking()
            .Where(x => x.TelegramUser.ChatId == chatId && x.IsActive)
            .OrderByDescending(x => x.Version)
            .FirstOrDefaultAsync(cancellationToken);

        return manifest?.Content ?? string.Empty;
    }

    public async Task<bool> SaveManifestAsync(long chatId, string content, CancellationToken cancellationToken)
    {
        var userId = await dbContext.TelegramUsers
            .AsNoTracking()
            .Where(x => x.ChatId == chatId)
            .Select(x => x.Id)
            .FirstOrDefaultAsync(cancellationToken);

        if (userId == 0) return false;

        var existingActive = await dbContext.UserMemoryManifests
            .Where(x => x.TelegramUserId == userId && x.IsActive)
            .ToListAsync(cancellationToken);

        foreach (var manifest in existingActive)
        {
            manifest.IsActive = false;
        }

        var lastVersion = await dbContext.UserMemoryManifests
            .Where(x => x.TelegramUserId == userId)
            .Select(x => (int?)x.Version)
            .MaxAsync(cancellationToken) ?? 0;

        dbContext.UserMemoryManifests.Add(new UserMemoryManifest
        {
            TelegramUserId = userId,
            Content = content,
            Version = lastVersion + 1,
            IsActive = true,
            UpdatedAt = DateTime.UtcNow
        });

        await dbContext.SaveChangesAsync(cancellationToken);
        return true;
    }
}
