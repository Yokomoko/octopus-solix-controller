namespace Solix.Core;

/// <summary>Latest Home Mini reading. DemandW is positive when importing, negative when exporting.</summary>
public sealed record GridReading(DateTimeOffset ReadAt, int DemandW);

/// <summary>Battery state read over Modbus. AcOutputW is positive when supplying the house.</summary>
public sealed record BatteryState(int AcOutputW, int BatteryPowerW, int SocPercent, int Mode);

/// <summary>Octopus events the account has joined: Power Ups and Happy Hours are free, Power Downs reward using less.</summary>
public enum OctopusEventKind
{
    PowerUp,
    PowerDown,
}

/// <summary>Setpoint to write, positive to discharge and negative to charge.</summary>
public sealed record Decision(int TargetW, string Reason);
