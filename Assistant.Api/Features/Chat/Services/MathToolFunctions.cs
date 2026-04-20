using System.ComponentModel;
using System.Globalization;

namespace Assistant.Api.Features.Chat.Services;

public class MathToolFunctions
{
    private const int MaxExpressionLength = 500;

    [Description("Evaluates a safe math expression using only numbers, +, -, *, /, ^, parentheses, and allowlisted functions: abs, sqrt, round, floor, ceil, min, max, percentOf, percentChange.")]
    public string Calculate(
        [Description("A math expression such as '2 + 3 * 4', '(2 + 3)^2', 'percentOf(18, 250)', or 'round(10 / 3, 2)'. Use '.' for decimals and ',' only between function arguments.")] string expression)
    {
        if (string.IsNullOrWhiteSpace(expression))
        {
            return "Error: expression is required.";
        }

        if (expression.Length > MaxExpressionLength)
        {
            return $"Error: expression is too long. Keep it under {MaxExpressionLength} characters.";
        }

        if (!ContainsOnlyAllowedCharacters(expression))
        {
            return "Error: expression contains unsupported characters. Use numbers, '.', ',', operators (+ - * / ^), parentheses, spaces, and supported function names.";
        }

        try
        {
            var result = new ExpressionParser(expression).Parse();
            return $"Result: {FormatResult(result)}";
        }
        catch (MathExpressionException ex)
        {
            return $"Error: {ex.Message}";
        }
        catch (OverflowException)
        {
            return "Error: calculation result is too large.";
        }
    }

    private static bool ContainsOnlyAllowedCharacters(string expression)
    {
        foreach (var character in expression)
        {
            if (char.IsAsciiLetterOrDigit(character) || char.IsWhiteSpace(character))
            {
                continue;
            }

            if (character is '.' or ',' or '+' or '-' or '*' or '/' or '^' or '(' or ')')
            {
                continue;
            }

            return false;
        }

        return true;
    }

    private static string FormatResult(decimal result)
    {
        return result.ToString("G29", CultureInfo.InvariantCulture);
    }

    private sealed class ExpressionParser(string expression)
    {
        private const int MaxIntegerExponentMagnitude = 1000;
        private int _position;

        public decimal Parse()
        {
            var result = ParseExpression();
            SkipWhitespace();

            if (!IsAtEnd)
            {
                throw Error($"Unexpected input '{Current}'.");
            }

            return result;
        }

        private decimal ParseExpression()
        {
            return ParseAdditive();
        }

        private decimal ParseAdditive()
        {
            var value = ParseMultiplicative();

            while (true)
            {
                SkipWhitespace();

                if (Match('+'))
                {
                    value += ParseMultiplicative();
                    continue;
                }

                if (Match('-'))
                {
                    value -= ParseMultiplicative();
                    continue;
                }

                return value;
            }
        }

        private decimal ParseMultiplicative()
        {
            var value = ParseUnary();

            while (true)
            {
                SkipWhitespace();

                if (Match('*'))
                {
                    value *= ParseUnary();
                    continue;
                }

                if (Match('/'))
                {
                    var divisor = ParseUnary();
                    if (divisor == 0)
                    {
                        throw Error("Division by zero is not allowed.");
                    }

                    value /= divisor;
                    continue;
                }

                return value;
            }
        }

        private decimal ParsePower()
        {
            var value = ParsePrimary();
            SkipWhitespace();

            if (!Match('^'))
            {
                return value;
            }

            var exponent = ParseUnary();
            return Power(value, exponent);
        }

        private decimal ParseUnary()
        {
            SkipWhitespace();

            if (Match('+'))
            {
                return ParseUnary();
            }

            if (Match('-'))
            {
                return -ParseUnary();
            }

            return ParsePower();
        }

        private decimal ParsePrimary()
        {
            SkipWhitespace();

            if (Match('('))
            {
                var value = ParseExpression();
                SkipWhitespace();

                if (!Match(')'))
                {
                    throw Error("Missing closing parenthesis.");
                }

                return value;
            }

            if (!IsAtEnd && char.IsDigit(Current))
            {
                return ParseNumber();
            }

            if (!IsAtEnd && char.IsAsciiLetter(Current))
            {
                return ParseFunctionCall();
            }

            throw IsAtEnd ? Error("Expression ended unexpectedly.") : Error($"Unexpected input '{Current}'.");
        }

        private decimal ParseNumber()
        {
            var start = _position;
            var hasDecimalPoint = false;

            while (!IsAtEnd && (char.IsDigit(Current) || Current == '.'))
            {
                if (Current == '.')
                {
                    if (hasDecimalPoint)
                    {
                        throw Error("Invalid number format.");
                    }

                    hasDecimalPoint = true;
                }

                _position++;
            }

            var numberText = expression[start.._position];
            if (!decimal.TryParse(numberText, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var value))
            {
                throw Error("Invalid number format. Use '.' as the decimal separator.");
            }

            return value;
        }

        private decimal ParseFunctionCall()
        {
            var functionName = ParseIdentifier();
            SkipWhitespace();

            if (!Match('('))
            {
                throw Error($"Unsupported input '{functionName}'. Function calls must use parentheses.");
            }

            var arguments = ParseArguments();
            return EvaluateFunction(functionName, arguments);
        }

        private string ParseIdentifier()
        {
            var start = _position;

            while (!IsAtEnd && char.IsAsciiLetter(Current))
            {
                _position++;
            }

            return expression[start.._position];
        }

        private List<decimal> ParseArguments()
        {
            var arguments = new List<decimal>();
            SkipWhitespace();

            if (Match(')'))
            {
                return arguments;
            }

            while (true)
            {
                arguments.Add(ParseExpression());
                SkipWhitespace();

                if (Match(')'))
                {
                    return arguments;
                }

                if (!Match(','))
                {
                    throw Error("Expected ',' between function arguments or ')' after the final argument.");
                }
            }
        }

        private decimal EvaluateFunction(string functionName, IReadOnlyList<decimal> arguments)
        {
            return functionName.ToLowerInvariant() switch
            {
                "abs" => RequireArgumentCount(functionName, arguments, 1)[0] < 0
                    ? -arguments[0]
                    : arguments[0],
                "sqrt" => Sqrt(RequireArgumentCount(functionName, arguments, 1)[0]),
                "round" => Round(functionName, arguments),
                "floor" => Math.Floor(RequireArgumentCount(functionName, arguments, 1)[0]),
                "ceil" => Math.Ceiling(RequireArgumentCount(functionName, arguments, 1)[0]),
                "min" => Min(functionName, arguments),
                "max" => Max(functionName, arguments),
                "percentof" => PercentOf(functionName, arguments),
                "percentchange" => PercentChange(functionName, arguments),
                _ => throw Error($"Unsupported function '{functionName}'. Supported functions are abs, sqrt, round, floor, ceil, min, max, percentOf, and percentChange.")
            };
        }

        private IReadOnlyList<decimal> RequireArgumentCount(
            string functionName,
            IReadOnlyList<decimal> arguments,
            int expectedCount)
        {
            if (arguments.Count != expectedCount)
            {
                throw Error($"Function '{functionName}' expects {expectedCount} argument(s).");
            }

            return arguments;
        }

        private decimal Round(string functionName, IReadOnlyList<decimal> arguments)
        {
            if (arguments.Count is not 1 and not 2)
            {
                throw Error($"Function '{functionName}' expects 1 or 2 argument(s).");
            }

            if (arguments.Count == 1)
            {
                return Math.Round(arguments[0], 0, MidpointRounding.AwayFromZero);
            }

            var digitsValue = arguments[1];
            if (!IsWholeNumber(digitsValue) || digitsValue is < 0 or > 28)
            {
                throw Error("round digits must be a whole number between 0 and 28.");
            }

            return Math.Round(arguments[0], (int)digitsValue, MidpointRounding.AwayFromZero);
        }

        private decimal Min(string functionName, IReadOnlyList<decimal> arguments)
        {
            if (arguments.Count == 0)
            {
                throw Error($"Function '{functionName}' expects at least 1 argument.");
            }

            return arguments.Min();
        }

        private decimal Max(string functionName, IReadOnlyList<decimal> arguments)
        {
            if (arguments.Count == 0)
            {
                throw Error($"Function '{functionName}' expects at least 1 argument.");
            }

            return arguments.Max();
        }

        private decimal PercentOf(string functionName, IReadOnlyList<decimal> arguments)
        {
            RequireArgumentCount(functionName, arguments, 2);
            return arguments[1] * arguments[0] / 100m;
        }

        private decimal PercentChange(string functionName, IReadOnlyList<decimal> arguments)
        {
            RequireArgumentCount(functionName, arguments, 2);

            var fromValue = arguments[0];
            if (fromValue == 0)
            {
                throw Error("percentChange cannot use 0 as the starting value.");
            }

            return (arguments[1] - fromValue) / Math.Abs(fromValue) * 100m;
        }

        private decimal Power(decimal baseValue, decimal exponent)
        {
            if (IsWholeNumber(exponent) && Math.Abs(exponent) <= MaxIntegerExponentMagnitude)
            {
                return IntegerPower(baseValue, (int)exponent);
            }

            var result = Math.Pow((double)baseValue, (double)exponent);
            if (double.IsNaN(result) || double.IsInfinity(result))
            {
                throw Error("Power calculation did not produce a finite real number.");
            }

            return Convert.ToDecimal(result, CultureInfo.InvariantCulture);
        }

        private decimal IntegerPower(decimal baseValue, int exponent)
        {
            if (exponent == 0)
            {
                return 1;
            }

            var isNegativeExponent = exponent < 0;
            var remainingExponent = Math.Abs(exponent);
            var result = 1m;
            var factor = baseValue;

            while (remainingExponent > 0)
            {
                if ((remainingExponent & 1) == 1)
                {
                    result *= factor;
                }

                remainingExponent >>= 1;
                if (remainingExponent > 0)
                {
                    factor *= factor;
                }
            }

            if (!isNegativeExponent)
            {
                return result;
            }

            if (result == 0)
            {
                throw Error("Division by zero is not allowed.");
            }

            return 1m / result;
        }

        private decimal Sqrt(decimal value)
        {
            if (value < 0)
            {
                throw Error("sqrt requires a non-negative value.");
            }

            var result = Math.Sqrt((double)value);
            if (double.IsNaN(result) || double.IsInfinity(result))
            {
                throw Error("sqrt calculation did not produce a finite real number.");
            }

            return Convert.ToDecimal(result, CultureInfo.InvariantCulture);
        }

        private static bool IsWholeNumber(decimal value)
        {
            return decimal.Truncate(value) == value;
        }

        private void SkipWhitespace()
        {
            while (!IsAtEnd && char.IsWhiteSpace(Current))
            {
                _position++;
            }
        }

        private bool Match(char expected)
        {
            if (IsAtEnd || Current != expected)
            {
                return false;
            }

            _position++;
            return true;
        }

        private MathExpressionException Error(string message)
        {
            return new MathExpressionException(message);
        }

        private bool IsAtEnd => _position >= expression.Length;

        private char Current => expression[_position];
    }

    private sealed class MathExpressionException(string message) : Exception(message);
}
