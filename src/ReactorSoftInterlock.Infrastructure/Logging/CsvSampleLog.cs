using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using ReactorSoftInterlock.Application.Ports;
using ReactorSoftInterlock.Domain;

namespace ReactorSoftInterlock.Infrastructure.Logging;

public sealed class CsvSampleLog : ISampleLog
{
    public const string Header = "timestamp,temperature_c,raw_ocr_text,status,alarm_reason,relay_action,screenshot_roi";
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates = new(StringComparer.OrdinalIgnoreCase);
    private readonly string _path;
    private readonly SemaphoreSlim _gate;

    public CsvSampleLog(string path)
    {
        _path = path;
        _gate = Gates.GetOrAdd(Path.GetFullPath(path), static _ => new SemaphoreSlim(1, 1));
    }

    public async Task AppendAsync(TemperatureSample sample, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path) ?? ".");
            var shouldWriteHeader = !File.Exists(_path) || new FileInfo(_path).Length == 0;
            await using var stream = new FileStream(_path, FileMode.Append, FileAccess.Write, FileShare.Read);
            await using var writer = new StreamWriter(stream, Encoding.UTF8);
            if (shouldWriteHeader)
            {
                await writer.WriteLineAsync(Header.AsMemory(), cancellationToken).ConfigureAwait(false);
            }

            await writer.WriteLineAsync(ToCsvLine(sample).AsMemory(), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<TemperatureSample>> ReadRecentAsync(int maxRows, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!File.Exists(_path))
            {
                return [];
            }

            var lines = await File.ReadAllLinesAsync(_path, cancellationToken).ConfigureAwait(false);
            return lines.Skip(1)
                .TakeLast(maxRows)
                .Select(ParseLine)
                .Where(static sample => sample is not null)
                .Cast<TemperatureSample>()
                .ToList();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task ExportAsync(string destinationPath, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath) ?? ".");
            if (File.Exists(_path))
            {
                File.Copy(_path, destinationPath, overwrite: true);
            }
            else
            {
                await File.WriteAllTextAsync(destinationPath, Header + Environment.NewLine, Encoding.UTF8, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task ClearAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path) ?? ".");
            await File.WriteAllTextAsync(_path, Header + Environment.NewLine, Encoding.UTF8, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public static string ToCsvLine(TemperatureSample sample)
    {
        var temperature = sample.TemperatureC?.ToString("0.###", CultureInfo.InvariantCulture) ?? string.Empty;
        return string.Join(
            ',',
            Escape(sample.Timestamp.ToString("O", CultureInfo.InvariantCulture)),
            Escape(temperature),
            Escape(sample.RawOcrText),
            Escape(sample.Status.ToString()),
            Escape(sample.AlarmReason),
            Escape(sample.RelayAction.ToString()),
            Escape(sample.ScreenshotRoi));
    }

    private static TemperatureSample? ParseLine(string line)
    {
        var columns = SplitCsv(line);
        if (columns.Count != 7)
        {
            return null;
        }

        _ = DateTimeOffset.TryParse(columns[0], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var timestamp);
        var temperature = double.TryParse(columns[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            ? value
            : (double?)null;
        _ = Enum.TryParse<MonitorStatus>(columns[3], out var status);
        _ = Enum.TryParse<RelayAction>(columns[5], out var relayAction);
        return new TemperatureSample(timestamp, temperature, columns[2], status, columns[4], relayAction, columns[6]);
    }

    private static string Escape(string value)
    {
        var escaped = value.Replace("\"", "\"\"");
        return escaped.Any(static c => c is ',' or '"' or '\n' or '\r') ? $"\"{escaped}\"" : escaped;
    }

    private static List<string> SplitCsv(string line)
    {
        var result = new List<string>();
        var current = new StringBuilder();
        var inQuotes = false;

        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (c == '"' && inQuotes && i + 1 < line.Length && line[i + 1] == '"')
            {
                current.Append('"');
                i++;
            }
            else if (c == '"')
            {
                inQuotes = !inQuotes;
            }
            else if (c == ',' && !inQuotes)
            {
                result.Add(current.ToString());
                current.Clear();
            }
            else
            {
                current.Append(c);
            }
        }

        result.Add(current.ToString());
        return result;
    }
}
