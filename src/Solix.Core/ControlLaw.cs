namespace Solix.Core;

public static class ControlLaw
{
    /// <summary>
    /// Works out the setpoint. Priority: Power Down, Power Up, EV smart slot, then normal running
    /// (discharge to cover import, charge to soak up export).
    /// </summary>
    public static Decision Decide(
        ControllerOptions options,
        DateTimeOffset now,
        GridReading? grid,
        BatteryState? battery,
        bool inEvSlot = false,
        OctopusEventKind? octopusEvent = null,
        int? outputAtReadingW = null)
    {
        var cheap = options.CheapRate;
        if (cheap.Enabled && !cheap.HandBackToApp && cheap.Contains(TimeOnly.FromDateTime(now.LocalDateTime)))
        {
            if (battery is not null && battery.SocPercent >= options.CheapRate.UpToSocPercent)
                return new Decision(0, $"cheap-rate window, charged to {battery.SocPercent}%");
            return new Decision(-Math.Max(0, options.CheapRate.ChargeW), "cheap-rate window");
        }

        if (battery is null)
            return new Decision(0, "battery unreadable");

        var powerDown = octopusEvent == OctopusEventKind.PowerDown;

        if (octopusEvent == OctopusEventKind.PowerUp)
        {
            if (battery.SocPercent >= options.SolarCharge.SocCeilingPercent)
                return new Decision(0, $"Power Up, battery full ({battery.SocPercent}%)");
            return new Decision(-Math.Max(0, options.SolarCharge.MaxChargeW), "Power Up, charging on free electricity");
        }

        if (inEvSlot && options.EvSlot.Enabled && !powerDown)
        {
            if (battery.SocPercent >= options.EvSlot.UpToSocPercent)
                return new Decision(0, $"EV smart slot, battery charged to {battery.SocPercent}%");
            return new Decision(-Math.Max(0, options.EvSlot.ChargeW), "EV smart slot, charging at cheap rate");
        }

        if (grid is null)
            return new Decision(0, "no grid reading");

        var ageSeconds = (now - grid.ReadAt).TotalSeconds;
        if (ageSeconds > options.StaleAfterSeconds)
            return new Decision(0, $"grid reading stale ({ageSeconds:F0}s)");

        // Size from the AC register, sign from the battery register, which is documented as - when charging.
        var current = battery.BatteryPowerW < 0 ? -Math.Abs(battery.AcOutputW) : Math.Abs(battery.AcOutputW);

        // What the battery would need to supply for the grid to read zero; negative means spare solar.
        // The reading lags, so pair it with the output when it was taken, or the loop walks itself down.
        var balance = (outputAtReadingW ?? current) + grid.DemandW;

        if (powerDown)
            return PowerDown(options, battery, balance);

        if (balance > options.ImportBufferW)
            return Discharge(options, battery, current, balance);

        var solar = options.SolarCharge;
        if (solar.Enabled && balance <= -solar.StartExportW)
            return Charge(options, battery, current, balance);

        return new Decision(0, "balanced");
    }

    // Rewards are for import saved, so cover all of it at once: no buffer, no ramp, no charging.
    private static Decision PowerDown(ControllerOptions options, BatteryState battery, int balance)
    {
        if (battery.SocPercent <= options.SocFloorPercent)
            return new Decision(0, $"Power Down, SOC at floor ({battery.SocPercent}%)");

        return new Decision(Math.Clamp(balance, 0, options.MaxW), "Power Down, covering all import");
    }

    private static Decision Discharge(ControllerOptions options, BatteryState battery, int current, int balance)
    {
        if (battery.SocPercent <= options.SocFloorPercent)
            return new Decision(0, $"SOC at floor ({battery.SocPercent}%)");

        var wanted = Math.Min(balance - options.ImportBufferW, options.MaxW);

        // Increases are rate limited; decreases apply at once so export is cut quickly.
        var limit = Math.Max(current, 0) + options.MaxStepUpW;
        return wanted > limit
            ? new Decision(limit, "following load (step limited)")
            : new Decision(wanted, "following load");
    }

    private static Decision Charge(ControllerOptions options, BatteryState battery, int current, int balance)
    {
        var solar = options.SolarCharge;
        if (battery.SocPercent >= solar.SocCeilingPercent)
            return new Decision(0, $"exporting but battery full ({battery.SocPercent}%)");

        var wanted = Math.Max(balance + solar.TargetExportW, -solar.MaxChargeW);
        if (wanted >= 0)
            return new Decision(0, "balanced");

        var limit = Math.Min(current, 0) - options.MaxStepUpW;
        return wanted < limit
            ? new Decision(limit, "charging from solar (step limited)")
            : new Decision(wanted, "charging from solar");
    }
}
