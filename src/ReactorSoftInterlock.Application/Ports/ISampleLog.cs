using ReactorSoftInterlock.Domain;

namespace ReactorSoftInterlock.Application.Ports;

public interface ISampleLog
{
    Task AppendAsync(TemperatureSample sample, CancellationToken cancellationToken);

    Task<IReadOnlyList<TemperatureSample>> ReadRecentAsync(int maxRows, CancellationToken cancellationToken);

    Task ExportAsync(string destinationPath, CancellationToken cancellationToken);
}
