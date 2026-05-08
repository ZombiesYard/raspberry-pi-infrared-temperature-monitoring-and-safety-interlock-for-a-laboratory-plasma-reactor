using System.Globalization;
using System.Text.RegularExpressions;

namespace ReactorSoftInterlock.Infrastructure.Relay;

public static partial class PcanChannelParser
{
    private const ushort UsbBusBaseHandle = 0x51;

    public static bool TryParse(string? value, out ushort handle)
    {
        var normalized = string.IsNullOrWhiteSpace(value) ? "USBBUS1" : Normalize(value);
        var match = UsbBusPattern().Match(normalized);
        if (match.Success && int.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var index) && index is >= 1 and <= 16)
        {
            handle = (ushort)(UsbBusBaseHandle + index - 1);
            return true;
        }

        handle = 0;
        return false;
    }

    public static ushort ParseOrThrow(string? value)
    {
        if (TryParse(value, out var handle))
        {
            return handle;
        }

        throw new FormatException("PCAN channel must look like UsbBus1, UsbBus2, or PCAN_USBBUS1.");
    }

    private static string Normalize(string value)
    {
        return value.Trim().ToUpperInvariant().Replace("_", string.Empty, StringComparison.Ordinal);
    }

    [GeneratedRegex(@"^(?:PCAN)?USBBUS(\d{1,2})$", RegexOptions.CultureInvariant)]
    private static partial Regex UsbBusPattern();
}
