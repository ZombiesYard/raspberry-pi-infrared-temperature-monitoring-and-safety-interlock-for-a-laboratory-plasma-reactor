namespace ReactorSoftInterlock.Infrastructure.Relay;

public static class HexCommandParser
{
    public static byte[] Parse(string hex)
    {
        if (string.IsNullOrWhiteSpace(hex))
        {
            return [];
        }

        var cleaned = new string(hex.Where(static c => !char.IsWhiteSpace(c) && c != '-' && c != ':').ToArray());
        if (cleaned.Length % 2 != 0)
        {
            throw new FormatException("HEX command must contain an even number of digits.");
        }

        var bytes = new byte[cleaned.Length / 2];
        for (var i = 0; i < bytes.Length; i++)
        {
            var slice = cleaned.Substring(i * 2, 2);
            bytes[i] = Convert.ToByte(slice, 16);
        }

        return bytes;
    }
}
