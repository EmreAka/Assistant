using Assistant.Api.Features.UserManagement.Commands;
using Assistant.Api.Features.UserManagement.Models;
using Assistant.Api.Features.UserManagement.Services;
using Assistant.Api.Services.Abstracts;
using Telegram.Bot.Types;

namespace Assistant.Api.Tests.UserManagement;

public class MemoryCommandTests
{
    [Fact]
    public async Task ExecuteAsync_SendsActiveManifest_WhenManifestExists()
    {
        var responseSender = new FakeTelegramResponseSender();
        var memoryService = new FakeMemoryService
        {
            ActiveManifest = new UserMemoryManifest
            {
                Content = "User likes espresso.",
                Version = 3,
                IsActive = true,
                UpdatedAt = new DateTime(2026, 4, 16, 9, 30, 0, DateTimeKind.Utc)
            }
        };
        var command = new MemoryCommand(memoryService, responseSender);

        await command.ExecuteAsync(
            new Update
            {
                Message = new Message
                {
                    Text = "/memory",
                    Chat = new Chat { Id = 42 }
                }
            },
            null!,
            CancellationToken.None);

        Assert.Single(responseSender.Messages);
        Assert.Contains("*🧠 Aktif Memory*", responseSender.Messages[0]);
        Assert.Contains("Versiyon: 3", responseSender.Messages[0]);
        Assert.Contains("Güncellendi: 16.04.2026 09:30 UTC", responseSender.Messages[0]);
        Assert.Contains("User likes espresso.", responseSender.Messages[0]);
    }

    [Fact]
    public async Task ExecuteAsync_SendsEmptyMessage_WhenManifestDoesNotExist()
    {
        var responseSender = new FakeTelegramResponseSender();
        var command = new MemoryCommand(new FakeMemoryService(), responseSender);

        await command.ExecuteAsync(
            new Update
            {
                Message = new Message
                {
                    Text = "/memory",
                    Chat = new Chat { Id = 42 }
                }
            },
            null!,
            CancellationToken.None);

        Assert.Single(responseSender.Messages);
        Assert.Equal("Aktif memory manifest bulunamadı.", responseSender.Messages[0]);
    }

    private sealed class FakeMemoryService : IMemoryService
    {
        public UserMemoryManifest? ActiveManifest { get; init; }

        public Task<string> GetActiveManifestAsync(long chatId, CancellationToken cancellationToken)
        {
            return Task.FromResult(ActiveManifest?.Content ?? string.Empty);
        }

        public Task<UserMemoryManifest?> GetActiveManifestRecordAsync(long chatId, CancellationToken cancellationToken)
        {
            return Task.FromResult(ActiveManifest);
        }

        public Task<bool> SaveManifestAsync(long chatId, string content, CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }
    }

    private sealed class FakeTelegramResponseSender : ITelegramResponseSender
    {
        public List<string> Messages { get; } = [];

        public Task SendResponseAsync(long chatId, string responseText, CancellationToken cancellationToken)
        {
            Messages.Add(responseText);
            return Task.CompletedTask;
        }
    }
}
