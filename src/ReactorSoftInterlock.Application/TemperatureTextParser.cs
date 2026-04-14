using System.Globalization;
using System.Text.RegularExpressions;

namespace ReactorSoftInterlock.Application;

public sealed class TemperatureTextParser
{
    private static readonly Regex TemperaturePattern = new(
        @"(?<!\d)(?<value>-?\d{1,3}(?:[\.,]\d{1,2})?)\s*(?:°\s*)?[Cc](?![A-Za-z])?",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex BareNumberPattern = new(
        @"(?<!\d)(?<value>-?\d{1,3}(?:[\.,]\d{1,2})?)(?!\d)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public double? ParseHighestTemperatureC(string? ocrText)
    {
        if (string.IsNullOrWhiteSpace(ocrText))
        {
            return null;
        }

        var normalized = ocrText.Replace('，', ',').Replace('．', '.');
        var values = TemperaturePattern.Matches(normalized)
            .Select(ParseMatch)
            .Where(static value => value is >= -50 and <= 300)
            .ToList();

        if (values.Count == 0)
        {
            values = BareNumberPattern.Matches(normalized)
                .Select(ParseMatch)
                .Where(static value => value is >= -50 and <= 300)
                .ToList();
        }

        return values.Count == 0 ? null : values.Max();
    }

    private static double ParseMatch(Match match)
    {
        var value = match.Groups["value"].Value.Replace(',', '.');
        return double.Parse(value, CultureInfo.InvariantCulture);
    }
}
