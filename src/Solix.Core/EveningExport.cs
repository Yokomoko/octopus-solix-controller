namespace Solix.Core;

public static class EveningExport
{
    /// <summary>
    /// Watts to export on top of covering the house, or 0. Energy above the floor, less what the house needs
    /// until the cheap-rate window at its current load, spread evenly over the time left.
    /// </summary>
    public static int ExtraW(ControllerOptions options, TimeOnly now, int socPercent, int houseLoadW)
    {
        var export = options.EveningExport;
        var end = options.CheapRate.Start;
        if (!export.Enabled || !options.CheapRate.Enabled || socPercent <= export.FloorPercent) return 0;
        if (!InWindow(now, export.Start, end)) return 0;

        var hoursLeft = (end - now).TotalHours;
        if (hoursLeft <= 0) return 0;

        var spareWh = (socPercent - export.FloorPercent) / 100.0 * export.CapacityWh - Math.Max(0, houseLoadW) * hoursLeft;
        if (spareWh <= 0) return 0;

        var watts = (int)Math.Round(spareWh / hoursLeft);
        return watts < export.MinExportW ? 0 : watts;
    }

    private static bool InWindow(TimeOnly time, TimeOnly start, TimeOnly end) =>
        start <= end ? time >= start && time < end : time >= start || time < end;
}
