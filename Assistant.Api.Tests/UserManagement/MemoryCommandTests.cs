using Assistant.Api.Features.UserManagement.Commands;
using Assistant.Api.Features.UserManagement.Models;
using Assistant.Api.Features.UserManagement.Services;
using Assistant.Api.Services.Abstracts;
using Pgvector;
using Telegram.Bot.Types;

namespace Assistant.Api.Tests.UserManagement;

public class MemoryCommandTests
{
    [Fact]
    public async Task ExecuteAsync_SendsItemsGroupedByCoreAndCategory_WhenItemsExist()
    {
        var responseSender = new FakeTelegramResponseSender();
        var memoryItemService = new FakeMemoryItemService
        {
            ActiveItems =
            [
                new MemoryItemSummary(1, "User's name is Emre.", UserMemoryItemCategories.Identity, true,
                    new DateTime(2026, 4, 16, 9, 30, 0, DateTimeKind.Utc)),
                new MemoryItemSummary(7, "User likes espresso.", UserMemoryItemCategories.Preference, false,
                    new DateTime(2026, 4, 15, 8, 0, 0, DateTimeKind.Utc)),
                new MemoryItemSummary(9, "User works as a backend developer.", UserMemoryItemCategories.WorkEducation, false,
                    new DateTime(2026, 4, 14, 8, 0, 0, DateTimeKind.Utc))
            ]
        };
        var command = new MemoryCommand(memoryItemService, responseSender);

        await command.ExecuteAsync(CreateMemoryUpdate(), null!, CancellationToken.None);

        var message = Assert.Single(responseSender.Messages);
        Assert.Contains("*🧠 Aktif Memory*", message);
        Assert.Contains("Toplam: 3 kayıt (1 temel)", message);
        Assert.Contains("Son güncelleme: 16.04.2026 09:30 UTC", message);
        Assert.Contains("*Temel*\n#1 User's name is Emre.", message.ReplaceLineEndings("\n"));
        Assert.Contains("*Tercihler*\n#7 User likes espresso.", message.ReplaceLineEndings("\n"));
        Assert.Contains("*İş / Eğitim*\n#9 User works as a backend developer.", message.ReplaceLineEndings("\n"));
        Assert.DoesNotContain("work_education", message);
    }

    [Fact]
    public async Task ExecuteAsync_SendsEmptyMessage_WhenNoItemsExist()
    {
        var responseSender = new FakeTelegramResponseSender();
        var command = new MemoryCommand(new FakeMemoryItemService(), responseSender);

        await command.ExecuteAsync(CreateMemoryUpdate(), null!, CancellationToken.None);

        var message = Assert.Single(responseSender.Messages);
        Assert.Equal("Aktif memory kaydı bulunamadı.", message);
    }

    private static Update CreateMemoryUpdate()
    {
        return new Update
        {
            Message = new Message
            {
                Text = "/memory",
                Chat = new Chat { Id = 42 }
            }
        };
    }

    private sealed class FakeMemoryItemService : IMemoryItemService
    {
        public IReadOnlyList<MemoryItemSummary> ActiveItems { get; init; } = [];

        public Task<IReadOnlyList<MemoryItemSummary>> GetActiveItemsAsync(long chatId, CancellationToken cancellationToken)
        {
            return Task.FromResult(ActiveItems);
        }

        public Task<IReadOnlyList<MemoryItemSummary>> GetCoreItemsAsync(long chatId, CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }

        public Task<IReadOnlyList<MemoryItemSearchResult>> FindNeighborsAsync(int telegramUserId, Vector vector, CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }

        public Task<IReadOnlyList<MemoryItemSearchResult>> SearchAsync(long chatId, Vector queryVector, CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }

        public Task<bool> HasAnyItemsAsync(int telegramUserId, CancellationToken cancellationToken)
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
