using Assistant.Api.Features.Chat.Services;

namespace Assistant.Api.Tests.ChatFeatures;

public class MathToolFunctionsTests
{
    private readonly MathToolFunctions _tool = new();

    [Fact]
    public void Calculate_RespectsOperatorPrecedence()
    {
        var result = _tool.Calculate("2 + 3 * 4");

        Assert.Equal("Result: 14", result);
    }

    [Fact]
    public void Calculate_EvaluatesParenthesesAndPowers()
    {
        var result = _tool.Calculate("(2 + 3)^2");

        Assert.Equal("Result: 25", result);
    }

    [Fact]
    public void Calculate_AppliesUnaryMinusAfterPower()
    {
        var result = _tool.Calculate("-2^2");

        Assert.Equal("Result: -4", result);
    }

    [Fact]
    public void Calculate_EvaluatesPercentOf()
    {
        var result = _tool.Calculate("percentOf(18, 250)");

        Assert.Equal("Result: 45", result);
    }

    [Fact]
    public void Calculate_ParsesDecimalsUsingInvariantCulture()
    {
        var result = _tool.Calculate("1.5 + 2.25");

        Assert.Equal("Result: 3.75", result);
    }

    [Fact]
    public void Calculate_ReturnsErrorForInvalidInput()
    {
        var result = _tool.Calculate("2 +");

        Assert.StartsWith("Error:", result);
    }

    [Fact]
    public void Calculate_ReturnsErrorForDivisionByZero()
    {
        var result = _tool.Calculate("10 / (5 - 5)");

        Assert.Equal("Error: Division by zero is not allowed.", result);
    }

    [Fact]
    public void Calculate_ReturnsErrorForUnsupportedCharacters()
    {
        var result = _tool.Calculate("System.Console.WriteLine(1)");

        Assert.StartsWith("Error:", result);
    }
}
