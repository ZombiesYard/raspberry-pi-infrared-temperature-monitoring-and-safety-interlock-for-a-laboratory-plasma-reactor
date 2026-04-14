namespace ReactorSoftInterlock.Application.Ports;

public interface ITemperatureReader
{
    Task<TemperatureReading> ReadAsync(CancellationToken cancellationToken);
}
