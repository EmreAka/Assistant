using Assistant.Api.Features.Tefas.Commands;
using Assistant.Api.Features.Tefas.Models;
using Assistant.Api.Features.Tefas.Services;
using Assistant.Api.Services.Abstracts;
using Microsoft.Extensions.Logging.Abstractions;
using Telegram.Bot.Types;

namespace Assistant.Api.Tests.Tefas;

public class TefasCommandTests
{
    [Fact]
    public async Task ExecuteAsync_SendsUsage_WhenFundCodeMissing()
    {
        var responseSender = new FakeTelegramResponseSender();
        var analysisService = new FakeTefasAnalysisService();
        var command = new TefasCommand(analysisService, responseSender, NullLogger<TefasCommand>.Instance);

        await command.ExecuteAsync(
            new Update
            {
                Message = new Message
                {
                    Text = "/tefas   ",
                    Chat = new Chat { Id = 42 }
                }
            },
            null!,
            CancellationToken.None);

        Assert.Single(responseSender.Messages);
        Assert.Equal("Kullanim: /tefas AFT", responseSender.Messages[0]);
        Assert.Null(analysisService.LastRequestedCode);
    }

    [Fact]
    public async Task ExecuteAsync_NormalizesFundCode_WithLowercaseAndWhitespace()
    {
        var responseSender = new FakeTelegramResponseSender();
        var analysisService = new FakeTefasAnalysisService
        {
            Response = new TefasAnalysisResponse(true, "hazir mesaj")
        };
        var command = new TefasCommand(analysisService, responseSender, NullLogger<TefasCommand>.Instance);

        await command.ExecuteAsync(
            new Update
            {
                Message = new Message
                {
                    Text = "/tefas   aft   ",
                    Chat = new Chat { Id = 7 }
                }
            },
            null!,
            CancellationToken.None);

        Assert.Equal("AFT", analysisService.LastRequestedCode);
        Assert.Single(responseSender.Messages);
        Assert.Equal("hazir mesaj", responseSender.Messages[0]);
    }

    private sealed class FakeTefasAnalysisService : ITefasAnalysisService
    {
        public string? LastRequestedCode { get; private set; }
        public TefasAnalysisResponse Response { get; set; } = new(true, "ok");

        public Task<TefasAnalysisResponse> AnalyzeAsync(long chatId, string fundCode, CancellationToken cancellationToken)
        {
            LastRequestedCode = fundCode;
            return Task.FromResult(Response);
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
