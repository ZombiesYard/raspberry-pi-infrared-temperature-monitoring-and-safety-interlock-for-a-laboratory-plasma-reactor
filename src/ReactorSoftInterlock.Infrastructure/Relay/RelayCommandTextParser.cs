namespace ReactorSoftInterlock.Infrastructure.Relay;

public static class RelayCommandTextParser
{
    public static IReadOnlyList<string> Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return [];
        }

        return text
            .Split(['\r', '\n', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(static command => !string.IsNullOrWhiteSpace(command))
            .ToArray();
    }
}
