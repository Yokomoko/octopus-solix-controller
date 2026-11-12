namespace Solix.Core;

public enum ControlMode
{
    /// <summary>Reads everything and logs what it would write, but never writes to the battery.</summary>
    Observe,

    /// <summary>Writes the third-party mode and setpoints to the battery.</summary>
    Live,
}

public sealed class ControllerOptions
{
    public ControlMode Mode { get; set; } = ControlMode.Observe;
    // smartMeterTelemetry allows 125 calls an hour; 40s uses 90, leaving room for the Octopus app.
    public int PollSeconds { get; set; } = 40;
    public int ModeGuardSeconds { get; set; } = 10;
    public int StaleAfterSeconds { get; set; } = 120;
    public int ImportBufferW { get; set; } = 40;
    public int MaxW { get; set; } = 300;
    public int MaxStepUpW { get; set; } = 300;
    public int SocFloorPercent { get; set; } = 10;
    public CheapRateOptions CheapRate { get; set; } = new();
    public SolarChargeOptions SolarCharge { get; set; } = new();
    public EvSlotOptions EvSlot { get; set; } = new();
    public OctopusEventOptions Events { get; set; } = new();

    public ControllerOptions With(ControlMode mode, int maxW)
    {
        var copy = (ControllerOptions)MemberwiseClone();
        copy.Mode = mode;
        copy.MaxW = maxW;
        return copy;
    }
}

public sealed class CheapRateOptions
{
    public bool Enabled { get; set; }
    public TimeOnly Start { get; set; } = new(23, 30);
    public TimeOnly End { get; set; } = new(5, 30);

    /// <summary>Charge power in watts during the window, as a positive number. 0 holds the battery idle.</summary>
    public int ChargeW { get; set; }

    /// <summary>Stops charging in the window at this SOC, leaving room for solar the next day.</summary>
    public int UpToSocPercent { get; set; } = 100;

    /// <summary>Hands the battery back to the app's own schedule for the window instead of charging at ChargeW.</summary>
    public bool HandBackToApp { get; set; } = true;

    public bool AppOwns(DateTimeOffset now) =>
        Enabled && HandBackToApp && Contains(TimeOnly.FromDateTime(now.LocalDateTime));

    public bool Contains(TimeOnly time) =>
        Start <= End ? time >= Start && time < End : time >= Start || time < End;
}

/// <summary>Charges from the grid during Intelligent Octopus smart-charge slots, when the house gets the off-peak rate.</summary>
public sealed class EvSlotOptions
{
    public bool Enabled { get; set; } = true;
    public int ChargeW { get; set; } = 800;
    public int UpToSocPercent { get; set; } = 100;
    public int PollMinutes { get; set; } = 1;
}

/// <summary>Acts on joined Octopus Power Ups, Happy Hours and Power Downs.</summary>
public sealed class OctopusEventOptions
{
    public bool Enabled { get; set; } = true;
    public int PollMinutes { get; set; } = 15;
}

/// <summary>Soaks up solar export by charging the battery instead of sending it to the grid.</summary>
public sealed class SolarChargeOptions
{
    public bool Enabled { get; set; } = true;

    /// <summary>Spare power, in watts, before charging starts.</summary>
    public int StartExportW { get; set; } = 50;

    /// <summary>Export left over while charging, so a cloud doesn't pull charge from the grid.</summary>
    public int TargetExportW { get; set; } = 20;

    public int MaxChargeW { get; set; } = 800;
    public int SocCeilingPercent { get; set; } = 100;
}
