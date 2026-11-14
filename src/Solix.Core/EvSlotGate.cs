namespace Solix.Core;

/// <summary>
/// Decides whether a planned EV slot is actually cheap. Octopus only bills a half-hour off-peak if the car
/// really charged in it, so a planned slot counts once the meter shows car-sized demand, then stays cheap
/// to the end of that half-hour even if the car stops or the slot ends early. Once the car has been seen in a
/// slot, a lower threshold keeps it counting while the car tapers near full.
/// </summary>
public sealed class EvSlotGate
{
    private DateTimeOffset _cheapUntil = DateTimeOffset.MinValue;
    private bool _carSeenThisSlot;

    public bool IsCheap(DateTimeOffset now, bool slotPlanned, int? houseDemandW, int carChargingW, int? carStillChargingW = null)
    {
        if (!slotPlanned) _carSeenThisSlot = false;
        if (now < _cheapUntil) return true;

        var threshold = _carSeenThisSlot ? carStillChargingW ?? carChargingW : carChargingW;
        if (!slotPlanned || houseDemandW is not { } demand || demand < threshold) return false;

        _carSeenThisSlot = true;
        _cheapUntil = DayCost.HalfHourStart(now).AddMinutes(30);
        return true;
    }

    /// <summary>What the house (car included) is drawing, excluding the battery; null without a fresh reading.</summary>
    public static int? HouseDemandW(ControllerOptions options, DateTimeOffset now, GridReading? grid, BatteryState? battery, int? outputAtReadingW)
    {
        if (grid is null || (now - grid.ReadAt).TotalSeconds > options.StaleAfterSeconds) return null;
        var output = outputAtReadingW
            ?? (battery is null ? 0 : battery.BatteryPowerW < 0 ? -Math.Abs(battery.AcOutputW) : Math.Abs(battery.AcOutputW));
        return output + grid.DemandW;
    }
}
