namespace ReactorSoftInterlock.Application.G2000;

public sealed class G2000AutomaticStartBlockedException : InvalidOperationException
{
    public G2000AutomaticStartBlockedException()
        : base("Cannot start the automatic sequence while a G2000 fault or trip is active.")
    {
    }
}
