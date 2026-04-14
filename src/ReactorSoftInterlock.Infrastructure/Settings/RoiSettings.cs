namespace ReactorSoftInterlock.Infrastructure.Settings;

public sealed class RoiSettings
{
    public int X { get; set; }

    public int Y { get; set; }

    public int Width { get; set; }

    public int Height { get; set; }

    public bool IsConfigured => Width > 0 && Height > 0;

    public override string ToString() => IsConfigured ? $"{X},{Y},{Width},{Height}" : "unconfigured";
}
