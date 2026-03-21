using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Assistant.Api.Data;
using Assistant.Api.Extensions;
using Microsoft.EntityFrameworkCore;
using ExpenseModel = Assistant.Api.Features.Expense.Models.Expense;

namespace Assistant.Api.Features.Expense.Services;

public class ExpenseAnalysisService(
    IHttpClientFactory httpClientFactory,
    ApplicationDbContext dbContext,
    ILogger<ExpenseAnalysisService> logger
) : IExpenseAnalysisService
{
    private static readonly CultureInfo TurkishCulture = CultureInfo.GetCultureInfo("tr-TR");

    private static readonly Regex TransactionDateRegex = new(
        @"(?<date>\d{2} (?:Ocak|Şubat|Mart|Nisan|Mayıs|Haziran|Temmuz|Ağustos|Eylül|Ekim|Kasım|Aralık) 20\d{2})",
        RegexOptions.Compiled);

    private static readonly Regex AmountRegex = new(
        @"(?<amount>\d{1,3}(?:\.\d{3})*,\d{2})(?<currency>\s*TL)?(?<sign>\s*[+-])?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex InstallmentAmountRegex = new(
        @"(?<amount>\d{1,3}(?:\.\d{3})*,\d{2})\s*x\s*\d+\s*=\s*\d{1,3}(?:\.\d{3})*,\d{2}",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly string[] SectionStartMarkers =
    [
        "BONUS PROGRAM ORTAKLARI'NDA YAPTIĞINIZ HARCAMALAR",
        "BONUS PROGRAM ORTAKLARINDA YAPTIĞINIZ HARCAMALAR"
    ];

    private static readonly string[] SectionEndMarkers =
    [
        "EKSTRE ÖZETİ",
        "Harcamalarınızın Dağılımı",
        "EKSTRENİZ İLE İLGİLİ AÇIKLAMALAR"
    ];

    private static readonly string[] IgnoredDescriptionFragments =
    [
        "HARCANABİLIR BONUS",
        "HARCANABİLİR BONUS",
        "TOPLAM BORCUNUZ",
        "İŞLEM TARİHİ",
        "ISLEM TARIHI",
        "EKSTRE SAYFASI",
        "EKSTRE NO",
        "SON ÖDEME TARİHİ",
        "SON ODEME TARIHI",
        "ÖNCEKİ DÖNEMDEN DEVİR",
        "ONCEKI DONEMDEN DEVIR",
        "ÖDEMENİZ İÇİN TEŞEKKÜR EDERİZ",
        "ODEMENIZ ICIN TESEKKUR EDERIZ",
        "MÜŞTERİ NUMARASI",
        "MUSTERI NUMARASI",
        "HESAP KESİM TARİHİ",
        "HESAP KESIM TARIHI",
        "KART NUMARASI"
    ];

    private static readonly string[] SegmentStopMarkers =
    [
        " Bonus Card'la ATM",
        " Bonus Cardla ATM",
        " Toplam ",
        " TOPLAM ",
        " Harcanabilir Bonus ",
        " HARCANABİLİR BONUS ",
        " HARCANABILIR BONUS "
    ];

    public async Task<ExpenseAnalysisResponse> AnalyzeStatementAsync(Stream pdfStream, long chatId, int userId, CancellationToken cancellationToken)
    {
        _ = chatId;

        try
        {
            var pdfText = await ExtractTextFromPdfAsync(pdfStream, cancellationToken);
            if (string.IsNullOrWhiteSpace(pdfText))
            {
                logger.LogWarning("PDF text is empty.");
                return new ExpenseAnalysisResponse(false, "PDF metni okunamadı.");
            }

            var parsedStatement = ParseStatementMarkdown(pdfText);
            if (parsedStatement.Expenses.Count == 0)
            {
                logger.LogWarning("No expenses were parsed from the statement.");
                return new ExpenseAnalysisResponse(false, "Ekstrede işlenebilir harcama bulunamadı.");
            }

            var fingerprint = ComputeStatementFingerprint(parsedStatement);
            var existingExpenses = await dbContext.Expenses
                .Where(x => x.TelegramUserId == userId && x.StatementFingerprint == fingerprint)
                .OrderBy(x => x.ExpenseDate)
                .ThenBy(x => x.Id)
                .ToListAsync(cancellationToken);

            if (existingExpenses.Count > 0)
            {
                return new ExpenseAnalysisResponse(
                    true,
                    BuildDuplicateMessage(parsedStatement),
                    existingExpenses,
                    parsedStatement);
            }

            var savedExpenses = await SaveParsedExpensesAsync(userId, parsedStatement, fingerprint, cancellationToken);

            return new ExpenseAnalysisResponse(
                true,
                BuildUserMessage(parsedStatement),
                savedExpenses,
                parsedStatement);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error analyzing statement.");
            return new ExpenseAnalysisResponse(false, "Ekstre analizi sırasında bir hata oluştu.");
        }
    }

    public static ParsedExpenseStatement ParseStatementMarkdown(string markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown))
        {
            return new ParsedExpenseStatement([], 0m);
        }

        var relevantSection = ExtractRelevantSection(markdown);
        var matches = TransactionDateRegex.Matches(relevantSection);
        var expenses = new List<StatementExpenseItem>();

        for (var index = 0; index < matches.Count; index++)
        {
            var segmentStart = matches[index].Index;
            var segmentEnd = index + 1 < matches.Count ? matches[index + 1].Index : relevantSection.Length;
            var segment = relevantSection[segmentStart..segmentEnd];

            if (TryParseExpense(segment, out var expense))
            {
                expenses.Add(expense);
            }
        }

        return new ParsedExpenseStatement(expenses, expenses.Sum(x => x.Price));
    }

    private async Task<string?> ExtractTextFromPdfAsync(Stream pdfStream, CancellationToken cancellationToken)
    {
        try
        {
            if (pdfStream.CanSeek)
            {
                pdfStream.Position = 0;
            }

            using var multipartContent = new MultipartFormDataContent();
            using var fileContent = new StreamContent(pdfStream);
            multipartContent.Add(fileContent, "file", "statement.pdf");

            using var httpClient = httpClientFactory.CreateClient(BotServiceRegistration.MarkitdownHttpClientName);
            using var response = await httpClient.PostAsync("/convert", multipartContent, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("Markitdown request failed with status code {StatusCode}.", response.StatusCode);
                return null;
            }

            var payload = await response.Content.ReadFromJsonAsync<MarkitdownConvertResponse>(cancellationToken: cancellationToken);
            if (string.IsNullOrWhiteSpace(payload?.Markdown))
            {
                logger.LogWarning("Markitdown response did not contain markdown content.");
                return null;
            }

            return payload.Markdown;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error extracting text from PDF via Markitdown.");
            return null;
        }
    }

    private async Task<List<ExpenseModel>> SaveParsedExpensesAsync(
        int userId,
        ParsedExpenseStatement parsedStatement,
        string statementFingerprint,
        CancellationToken cancellationToken)
    {
        var createdAt = DateTime.UtcNow;
        var expenseEntities = parsedStatement.Expenses
            .Select(expense => new ExpenseModel
            {
                TelegramUserId = userId,
                ExpenseDate = ToUtcDate(expense.Date),
                Amount = expense.Price,
                Currency = "TRY",
                Description = expense.Name,
                StatementFingerprint = statementFingerprint,
                CreatedAt = createdAt
            })
            .ToList();

        await dbContext.Expenses.AddRangeAsync(expenseEntities, cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "Saved statement expenses for user {UserId}. Transactions: {TransactionCount}, Total: {Total}, Fingerprint: {StatementFingerprint}",
            userId,
            parsedStatement.Expenses.Count,
            parsedStatement.Total,
            statementFingerprint);

        return expenseEntities;
    }

    private static string BuildUserMessage(ParsedExpenseStatement parsedStatement)
    {
        var start = parsedStatement.Expenses.Min(x => x.Date);
        var end = parsedStatement.Expenses.Max(x => x.Date);

        return
            $"{parsedStatement.Expenses.Count} işlem kaydedildi. " +
            $"Toplam harcama: {parsedStatement.Total.ToString("N2", TurkishCulture)} TRY " +
            $"({start:dd.MM.yyyy} - {end:dd.MM.yyyy}).";
    }

    private static string BuildDuplicateMessage(ParsedExpenseStatement parsedStatement)
    {
        var start = parsedStatement.Expenses.Min(x => x.Date);
        var end = parsedStatement.Expenses.Max(x => x.Date);

        return
            $"Bu ekstre zaten içeri aktarılmış. " +
            $"{parsedStatement.Expenses.Count} işlem, toplam {parsedStatement.Total.ToString("N2", TurkishCulture)} TRY " +
            $"({start:dd.MM.yyyy} - {end:dd.MM.yyyy}).";
    }

    private static string ComputeStatementFingerprint(ParsedExpenseStatement parsedStatement)
    {
        var payload = string.Join(
            "\n",
            parsedStatement.Expenses.Select(expense =>
                $"{expense.Date:yyyy-MM-dd}|{NormalizeForFingerprint(expense.Name)}|{expense.Price.ToString("0.00", CultureInfo.InvariantCulture)}|TRY"));

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload)));
    }

    private static string ExtractRelevantSection(string markdown)
    {
        var normalized = NormalizeStatementText(markdown);
        var sectionStart = FindFirstMarker(normalized, SectionStartMarkers);

        if (sectionStart < 0)
        {
            return normalized;
        }

        var relevantSection = normalized[sectionStart..];
        var sectionEnd = FindFirstMarker(relevantSection, SectionEndMarkers);

        return sectionEnd >= 0
            ? relevantSection[..sectionEnd]
            : relevantSection;
    }

    private static string NormalizeStatementText(string markdown)
    {
        var normalized = markdown
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Replace('\f', ' ')
            .Replace('\u00A0', ' ')
            .Replace('|', ' ');

        normalized = Regex.Replace(normalized, @"\b(?:bosluk|bboosslluukk)\b", " ", RegexOptions.IgnoreCase);
        normalized = Regex.Replace(normalized, @"-{2,}", " ");
        normalized = Regex.Replace(normalized, @"Ekstre No\s*:\s*\S+", " ", RegexOptions.IgnoreCase);
        normalized = Regex.Replace(normalized, @"Ekstre Sayfası\s*\d+\s*/\s*\d+", " ", RegexOptions.IgnoreCase);
        normalized = Regex.Replace(normalized, @"Ekstre Sayfasi\s*\d+\s*/\s*\d+", " ", RegexOptions.IgnoreCase);
        normalized = Regex.Replace(normalized, @"\s+", " ");

        return normalized.Trim();
    }

    private static bool TryParseExpense(string segment, out StatementExpenseItem expense)
    {
        expense = null!;

        var dateMatch = TransactionDateRegex.Match(segment);
        if (!dateMatch.Success)
        {
            return false;
        }

        var content = segment[(dateMatch.Index + dateMatch.Length)..].Trim();
        content = TrimSegment(content);
        if (string.IsNullOrWhiteSpace(content))
        {
            return false;
        }

        var nameBoundary = InstallmentAmountRegex.Match(content);
        if (!nameBoundary.Success)
        {
            nameBoundary = AmountRegex.Match(content);
        }

        if (!nameBoundary.Success)
        {
            return false;
        }

        var name = CleanDescription(content[..nameBoundary.Index]);
        if (ShouldIgnoreDescription(name))
        {
            return false;
        }

        var amount = ExtractAmount(content);
        if (amount is null || amount <= 0)
        {
            return false;
        }

        var date = DateOnly.ParseExact(dateMatch.Groups["date"].Value, "dd MMMM yyyy", TurkishCulture);
        expense = new StatementExpenseItem(date, name, amount.Value);

        return true;
    }

    private static decimal? ExtractAmount(string content)
    {
        var installmentMatch = InstallmentAmountRegex.Match(content);
        if (installmentMatch.Success)
        {
            return ParseAmount(installmentMatch.Groups["amount"].Value);
        }

        var amountMatches = AmountRegex.Matches(content);
        for (var index = amountMatches.Count - 1; index >= 0; index--)
        {
            var amount = ParseAmount(amountMatches[index].Groups["amount"].Value);
            if (amount is null)
            {
                continue;
            }

            if (amountMatches[index].Groups["sign"].Value.Contains('-', StringComparison.Ordinal))
            {
                amount *= -1;
            }

            return amount;
        }

        return null;
    }

    private static decimal? ParseAmount(string rawAmount)
    {
        var normalized = rawAmount.Replace(".", string.Empty, StringComparison.Ordinal)
            .Replace(",", ".", StringComparison.Ordinal);

        return decimal.TryParse(normalized, NumberStyles.Number, CultureInfo.InvariantCulture, out var amount)
            ? amount
            : null;
    }

    private static string CleanDescription(string rawDescription)
    {
        var cleaned = rawDescription
            .Replace(" TL", " ", StringComparison.OrdinalIgnoreCase)
            .Trim();

        cleaned = Regex.Replace(cleaned, @"\s+", " ");
        cleaned = cleaned.Trim(' ', '-', ':', '.', ',', ';');

        return cleaned;
    }

    private static DateTime ToUtcDate(DateOnly date)
    {
        return DateTime.SpecifyKind(date.ToDateTime(TimeOnly.MinValue), DateTimeKind.Utc);
    }

    private static string TrimSegment(string content)
    {
        var trimmed = content;

        foreach (var marker in SegmentStopMarkers)
        {
            var markerIndex = trimmed.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (markerIndex >= 0)
            {
                trimmed = trimmed[..markerIndex];
            }
        }

        return trimmed.Trim();
    }

    private static bool ShouldIgnoreDescription(string description)
    {
        if (string.IsNullOrWhiteSpace(description))
        {
            return true;
        }

        var normalizedDescription = NormalizeForComparison(description);

        if (normalizedDescription.Equals("TOPLAM", StringComparison.Ordinal))
        {
            return true;
        }

        foreach (var fragment in IgnoredDescriptionFragments)
        {
            if (normalizedDescription.Contains(NormalizeForComparison(fragment), StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static string NormalizeForComparison(string value)
    {
        return value
            .Replace('ı', 'i')
            .Replace('İ', 'I')
            .Replace('ş', 's')
            .Replace('Ş', 'S')
            .Replace('ğ', 'g')
            .Replace('Ğ', 'G')
            .Replace('ü', 'u')
            .Replace('Ü', 'U')
            .Replace('ö', 'o')
            .Replace('Ö', 'O')
            .Replace('ç', 'c')
            .Replace('Ç', 'C')
            .ToUpperInvariant();
    }

    private static string NormalizeForFingerprint(string value)
    {
        return Regex.Replace(NormalizeForComparison(value), @"\s+", " ").Trim();
    }

    private static int FindFirstMarker(string value, IEnumerable<string> markers)
    {
        var positions = markers
            .Select(marker => value.IndexOf(marker, StringComparison.OrdinalIgnoreCase))
            .Where(position => position >= 0)
            .ToArray();

        return positions.Length == 0 ? -1 : positions.Min();
    }
}

public sealed record MarkitdownConvertResponse(string? Filename, string? Markdown);
