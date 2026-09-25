using Assistant.Api.Features.Chat.Services;

namespace Assistant.Api.Tests.ChatFeatures;

public class ChatTurnSearchFusionTests
{
    [Fact]
    public void FuseByReciprocalRank_RanksTurnFoundByBothSearchesFirst()
    {
        var result = ChatTurnService.FuseByReciprocalRank(fullTextIds: [1, 2], semanticIds: [3, 2]);

        Assert.Equal(2, result[0].Id);
        Assert.Equal("both", result[0].Source);
        Assert.Equal(2, result[0].FullTextRank);
        Assert.Equal(2, result[0].SemanticRank);
    }

    [Fact]
    public void FuseByReciprocalRank_UsesStandardRrfScore()
    {
        var result = ChatTurnService.FuseByReciprocalRank(fullTextIds: [7], semanticIds: [9, 7]);

        var both = Assert.Single(result, x => x.Id == 7);
        Assert.Equal(1.0 / 61 + 1.0 / 62, both.Score, precision: 10);
    }

    [Fact]
    public void FuseByReciprocalRank_PreservesOrderWithinSingleList()
    {
        var result = ChatTurnService.FuseByReciprocalRank(fullTextIds: [5, 3, 8], semanticIds: []);

        Assert.Equal([5, 3, 8], result.Select(x => x.Id));
        Assert.All(result, x => Assert.Equal("fulltext", x.Source));
        Assert.All(result, x => Assert.Null(x.SemanticRank));
    }

    [Fact]
    public void FuseByReciprocalRank_WorksWithSemanticOnly()
    {
        var result = ChatTurnService.FuseByReciprocalRank(fullTextIds: [], semanticIds: [4, 6]);

        Assert.Equal([4, 6], result.Select(x => x.Id));
        Assert.All(result, x => Assert.Equal("semantic", x.Source));
    }

    [Fact]
    public void FuseByReciprocalRank_ReturnsEmpty_WhenBothListsAreEmpty()
    {
        var result = ChatTurnService.FuseByReciprocalRank(fullTextIds: [], semanticIds: []);

        Assert.Empty(result);
    }

    [Fact]
    public void FuseByReciprocalRank_BreaksTiesByNewerTurn()
    {
        // Both are rank 1 in a single list, so their scores are equal.
        var result = ChatTurnService.FuseByReciprocalRank(fullTextIds: [10], semanticIds: [20]);

        Assert.Equal([20, 10], result.Select(x => x.Id));
    }
}
