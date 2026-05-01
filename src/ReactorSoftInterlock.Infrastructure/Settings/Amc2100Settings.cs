namespace ReactorSoftInterlock.Infrastructure.Settings;

public sealed class Amc2100Settings
{
    public bool Enabled { get; set; }

    public string PortName { get; set; } = "COM4";

    public int BaudRate { get; set; } = 19200;

    public int SlaveAddress { get; set; } = 1;

    public bool ForceDigitalControlMode { get; set; } = true;

    public double FallbackRestoreSetpointMlMin { get; set; }

    public int ActualFlowHighRegister { get; set; } = 0;

    public int ActualFlowLowRegister { get; set; } = 1;

    public int SetpointHighRegister { get; set; } = 2;

    public int SetpointLowRegister { get; set; } = 3;

    public int DeviceAddressRegister { get; set; } = 8;

    public int BaudRateRegister { get; set; } = 9;

    public int ControlModeRegister { get; set; } = 11;

    public int DigitalControlModeValue { get; set; } = 1;

    public int AnalogControlModeValue { get; set; } = 2;

    public void Normalize()
    {
        if (string.IsNullOrWhiteSpace(PortName))
        {
            PortName = "COM4";
        }

        if (BaudRate <= 0)
        {
            BaudRate = 19200;
        }

        if (SlaveAddress < 1)
        {
            SlaveAddress = 1;
        }

        if (SlaveAddress > 247)
        {
            SlaveAddress = 247;
        }

        if (FallbackRestoreSetpointMlMin < 0)
        {
            FallbackRestoreSetpointMlMin = 0;
        }
    }
}
