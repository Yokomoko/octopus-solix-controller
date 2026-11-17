namespace Solix.Core;

public static class ControlLaw
{
    public const string CarUnconfirmed = "car charging outside a known slot, holding until Octopus confirms";
    public const string Cycling = "covering base load, appliance cycling";

    // Import beyond the cap by at least this much can't be a stale reading's overshoot.
    private const int JumpMarginW = 200;

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
        int? outputAtReadingW = null,
        SpareEstimate? spare = null,
        int? cyclingBaseW = null)
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
            return new Decision(-Math.Max(0, options.EvSlot.ChargeW ?? options.SolarCharge.MaxChargeW), "EV smart slot, charging at cheap rate");
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

        // On Intelligent Go the car nearly always charges in a slot, even one Octopus only reports afterwards.
        if (options.EvSlot.Enabled && options.EvSlot.HoldUnconfirmedCar && balance >= options.EvSlot.CarChargingW)
            return new Decision(0, CarUnconfirmed);

        // Spare charge before the cheap window goes out to the grid: aim the meter at an export instead of a small import.
        var exportW = EveningExport.ExtraW(options, TimeOnly.FromDateTime(now.LocalDateTime), battery.SocPercent, balance);
        if (exportW > 0)
        {
            var decision = Discharge(options, battery, current, balance, -exportW);
            return decision with { Reason = decision.Reason.Replace("following load", $"exporting spare ({exportW} W)") };
        }

        // With charge to spare, a tiny export is cheaper than a tiny import.
        var plenty = spare?.Plenty == true;
        var gridTargetW = plenty ? -options.Spare.ExportTargetW : options.ImportBufferW;
        if (balance > gridTargetW)
        {
            // Chasing spikes the meter reports late exports the battery once they stop; worth it only with charge to spare.
            if (!plenty && cyclingBaseW is { } baseW && balance > baseW)
            {
                if (baseW <= gridTargetW) return new Decision(0, Cycling);
                var covered = Discharge(options, battery, current, baseW, gridTargetW);
                return covered with { Reason = covered.Reason.Replace("following load", Cycling) };
            }
            var decision = Discharge(options, battery, current, balance, gridTargetW);
            return plenty ? decision with { Reason = decision.Reason.Replace("following load", "following load, plenty spare") } : decision;
        }

        var solar = options.SolarCharge;
        if (solar.Enabled && balance <= -solar.StartExportW)
            return Charge(options, battery, current, balance, plenty);

        return new Decision(0, "balanced");
    }

    // Rewards are for import saved, so cover all of it at once: no buffer, no ramp, no charging.
    private static Decision PowerDown(ControllerOptions options, BatteryState battery, int balance)
    {
        if (battery.SocPercent <= options.SocFloorPercent)
            return new Decision(0, $"Power Down, SOC at floor ({battery.SocPercent}%)");

        return new Decision(Math.Clamp(balance, 0, options.MaxW), "Power Down, covering all import");
    }

    private static Decision Discharge(ControllerOptions options, BatteryState battery, int current, int balance, int gridTargetW)
    {
        if (battery.SocPercent <= options.SocFloorPercent)
            return new Decision(0, $"SOC at floor ({battery.SocPercent}%)");

        // One lagged reading shouldn't swing it straight from charging to discharging.
        if (current < 0)
            return new Decision(0, "stopping charge before discharging");

        var wanted = Math.Min(balance - gridTargetW, options.MaxW);
        if (wanted <= current)
            return new Decision(wanted, "following load");

        // Far more import than the cap can cover leaves nothing to overshoot into, so go straight to the cap.
        if (balance - gridTargetW >= options.MaxW + JumpMarginW)
            return new Decision(options.MaxW, "following load");

        // Increases close only part of the gap and are rate limited; decreases above apply at once.
        var damped = current + (int)Math.Round((wanted - current) * Math.Clamp(options.Gain, 0.1, 1));
        var limit = current + options.MaxStepUpW;
        return damped > limit
            ? new Decision(limit, "following load (step limited)")
            : new Decision(damped, "following load");
    }

    private static Decision Charge(ControllerOptions options, BatteryState battery, int current, int balance, bool plenty)
    {
        var solar = options.SolarCharge;
        if (battery.SocPercent >= solar.SocCeilingPercent)
            return new Decision(0, $"exporting but battery full ({battery.SocPercent}%)");

        if (current > 0)
            return new Decision(0, "stopping discharge before charging");

        var wanted = Math.Max(balance + solar.TargetExportW, -solar.MaxChargeW);
        if (wanted >= 0)
            return new Decision(0, "balanced");

        // Topping a small spare up to the battery's minimum buys grid at peak. That pays back only if the battery would
        // run out before the cheap rate; with plenty spare it just means less overnight charging at the cheap rate.
        var floor = -Math.Min(solar.MinChargeW, solar.MaxChargeW);
        var minimumSpare = plenty ? solar.MinChargeW : solar.MinChargeW / 2;
        if (wanted > floor && -balance < minimumSpare)
            return new Decision(0, "balanced");
        if (wanted >= current)
            return new Decision(Math.Min(wanted, floor), "charging from solar");

        // Same as discharging: charge increases are damped, reductions apply at once.
        var damped = Math.Min(current + (int)Math.Round((wanted - current) * Math.Clamp(options.Gain, 0.1, 1)), floor);
        var limit = Math.Min(current - options.MaxStepUpW, floor);
        return damped < limit
            ? new Decision(limit, "charging from solar (step limited)")
            : new Decision(damped, "charging from solar");
    }
}
