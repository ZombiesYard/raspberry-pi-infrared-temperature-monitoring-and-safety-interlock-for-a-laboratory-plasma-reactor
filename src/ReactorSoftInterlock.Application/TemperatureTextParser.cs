using System.Globalization;
using System.Text.RegularExpressions;

namespace ReactorSoftInterlock.Application;

public sealed class TemperatureTextParser
{
    public const double MinimumPlausibleTemperatureC = -50d;
    public const double MaximumPlausibleTemperatureC = 300d;

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
        var temperatureMatches = TemperaturePattern.Matches(normalized);
        var values = temperatureMatches
            .Select(ParseMatch)
            .Where(IsPlausibleReading)
            .ToList();

        if (temperatureMatches.Count > 0)
        {
            return values.Count == 0 ? null : values.Max();
        }

        values = BareNumberPattern.Matches(normalized)
            .Select(ParseMatch)
            .Where(IsPlausibleReading)
            .ToList();
        return values.Count == 0 ? null : values.Max();
    }

    private static double ParseMatch(Match match)
    {
        var value = match.Groups["value"].Value.Replace(',', '.');
        return double.Parse(value, CultureInfo.InvariantCulture);
    }

    public static bool IsPlausibleReading(double value)
    {
        // HikmicroAnalyzer can briefly display exactly 0.0 during shutter calibration.
        // Treat that sentinel as no reading so it cannot advance automatic recovery.
        return value is >= MinimumPlausibleTemperatureC and <= MaximumPlausibleTemperatureC && value != 0d;
    }
}
