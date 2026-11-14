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
    public int ImportBufferW { get; set; } = 15;
    public int MaxW { get; set; } = 300;
    public int MaxStepUpW { get; set; } = 300;

    /// <summary>Share of the gap closed per cycle when increasing. Below 1 stops lagged readings causing overshoot.</summary>
    public double Gain { get; set; } = 0.6;
    public int SocFloorPercent { get; set; } = 5;
    public CheapRateOptions CheapRate { get; set; } = new();
    public SolarChargeOptions SolarCharge { get; set; } = new();
    public EvSlotOptions EvSlot { get; set; } = new();
    public OctopusEventOptions Events { get; set; } = new();
    public EveningExportOptions EveningExport { get; set; } = new();

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

    /// <summary>Charge rate during a slot. Leave unset to use the dashboard's max charge cap.</summary>
    public int? ChargeW { get; set; }

    public int UpToSocPercent { get; set; } = 100;
    public int PollMinutes { get; set; } = 1;

    /// <summary>House demand at or above this counts as the car charging: a planned slot only counts as cheap once it's seen.</summary>
    public int CarChargingW { get; set; } = 3500;

    /// <summary>Lower threshold that keeps a slot counting once the car has been seen in it, for a car tapering near full.</summary>
    public int CarStillChargingW { get; set; } = 1500;
}

/// <summary>Acts on joined Octopus Power Ups, Happy Hours and Power Downs.</summary>
public sealed class OctopusEventOptions
{
    public bool Enabled { get; set; } = true;
    public int PollMinutes { get; set; } = 15;
}

/// <summary>
/// Exports what's left in the battery before the cheap-rate window, spread so it reaches the floor as the window opens.
/// Only worth turning on with an export tariff that pays more than the overnight rate.
/// </summary>
public sealed class EveningExportOptions
{
    public bool Enabled { get; set; }
    public TimeOnly Start { get; set; } = new(21, 0);

    /// <summary>Exports stop at this SOC; normal load following carries on down to SocFloorPercent.</summary>
    public int FloorPercent { get; set; } = 10;

    /// <summary>Usable battery capacity, used to turn SOC into energy.</summary>
    public int CapacityWh { get; set; } = 5000;

    /// <summary>Exports smaller than this aren't worth the cycling.</summary>
    public int MinExportW { get; set; } = 100;
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
