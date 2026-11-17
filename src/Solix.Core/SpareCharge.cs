namespace Solix.Core;

/// <summary>
/// Battery need at one point: cumulative from the start (typical and busiest recent day), and the most any recent day
/// still needed from here to the end, which is what decides plenty.
/// </summary>
public sealed record NeedStep(DateTimeOffset At, double TypicalWh, double BusiestWh, double BusiestRemainingWh = 0);

/// <summary>One point of the day's battery profile: the usual level by now, and the level needed to count as plenty.</summary>
public sealed record DayStep(DateTimeOffset At, double? TypicalSoc, double PlentySoc);

/// <summary>Today's battery profile from the end of the cheap rate, for comparing the day so far against a usual one.</summary>
public sealed record DayProfile(DateTimeOffset Start, int StartSoc, IReadOnlyList<DayStep> Steps);

/// <summary>How much the battery is likely to supply from now until the cheap rate, built from recent days.</summary>
public sealed record NeedForecast(IReadOnlyList<NeedStep> Steps, int Days)
{
    public double TypicalWh => Steps.Count == 0 ? 0 : Steps[^1].TypicalWh;
    public double BusiestWh => Steps.Count == 0 ? 0 : Steps[^1].BusiestWh;
}

public sealed record SpareEstimate(
    double AvailableWh, double? NeededWh, double MarginWh, int SocPercent = 0, int CapacityWh = 0, NeedForecast? Forecast = null, int FloorPercent = 0)
{
    /// <summary>Charge left over after the expected need and the margin; null without enough history.</summary>
    public double? SpareWh => NeededWh is { } need ? AvailableWh - need - MarginWh : null;
    public bool Plenty => SpareWh > 0;

    /// <summary>Where the battery should end up at the cheap rate on a typical day, and on the busiest recent one.</summary>
    public double? TypicalFinishPercent => Forecast is { } f && CapacityWh > 0 ? SocPercent - f.TypicalWh / CapacityWh * 100 : null;
    public double? WorstFinishPercent => Forecast is { } f && CapacityWh > 0 ? SocPercent - f.BusiestWh / CapacityWh * 100 : null;
}

public static class SpareCharge
{
    private static readonly TimeSpan MaxGap = TimeSpan.FromMinutes(3);

    /// <summary>The busiest recent day's need from now until <paramref name="until"/>; see <see cref="Forecast"/>.</summary>
    public static double? NeededWh(IReadOnlyList<BatterySample> history, DateTimeOffset now, TimeOnly until, int capW, int days, int carChargingW) =>
        Forecast(history, now, until, capW, days, carChargingW)?.BusiestWh;

    /// <summary>
    /// What the battery supplied on each of the last <paramref name="days"/> days between this time of day and
    /// <paramref name="until"/>, in steps, as a running total. House load counts only up to the discharge cap and the car
    /// is left out, since the battery never covers it. Each point uses the days logged from there to the end. Null if no
    /// day is logged for the whole stretch, unless <paramref name="allowPartial"/>, when only the remaining need is meaningful.
    /// </summary>
    public static NeedForecast? Forecast(
        IReadOnlyList<BatterySample> history, DateTimeOffset now, TimeOnly until, int capW, int days, int carChargingW,
        int stepMinutes = 15, bool allowPartial = false)
    {
        var local = now.ToLocalTime();
        var from = TimeOnly.FromDateTime(local.DateTime);
        if (until <= from) return new NeedForecast([new NeedStep(now, 0, 0, 0)], 0);

        var span = until.ToTimeSpan() - from.ToTimeSpan();
        var step = TimeSpan.FromMinutes(Math.Max(1, stepMinutes));
        var bins = (int)Math.Ceiling(span / step);

        // Per day: energy and logged seconds in each step, kept for every day so each point can use whichever days
        // have a full log from there to the end.
        var energy = new List<double[]>();
        var logged = new List<double[]>();
        for (var d = 1; d <= days; d++)
        {
            var date = local.Date.AddDays(-d);
            var start = AtLocal(date + from.ToTimeSpan());
            var end = AtLocal(date + until.ToTimeSpan());

            var perBin = new double[bins];
            var coverBin = new double[bins];
            for (var i = 0; i < history.Count - 1; i++)
            {
                var s = history[i];
                if (s.At < start || s.At >= end) continue;
                var gap = history[i + 1].At - s.At;
                if (gap <= TimeSpan.Zero || gap > MaxGap) continue;

                var load = s.OutputW + s.DemandW;
                var house = load >= carChargingW ? 0 : Math.Clamp(load, 0, capW);
                var bin = Math.Min(bins - 1, (int)((s.At - start) / step));
                perBin[bin] += house * gap.TotalHours;
                coverBin[bin] += gap.TotalSeconds;
            }
            energy.Add(perBin);
            logged.Add(coverBin);
        }

        // Days logged well enough from step b to the end, with what they used over that stretch.
        List<double> RemainingFrom(int b)
        {
            var needed = 0.8 * (span - step * b).TotalSeconds;
            return Enumerable.Range(0, energy.Count)
                .Where(d => logged[d].Skip(b).Sum() >= needed)
                .Select(d => energy[d].Skip(b).Sum())
                .ToList();
        }

        // The typical and busiest running totals come from days logged for the whole stretch.
        var full = Enumerable.Range(0, energy.Count).Where(d => logged[d].Sum() >= 0.8 * span.TotalSeconds).ToList();
        if (full.Count == 0 && !allowPartial) return null;
        var curves = full.Select(d =>
        {
            var running = new double[bins + 1];
            for (var b = 0; b < bins; b++) running[b + 1] = running[b] + energy[d][b];
            return running;
        }).ToList();

        var steps = new List<NeedStep>(bins + 1);
        for (var b = 0; b <= bins; b++)
        {
            var at = b == bins ? now + span : now + step * b;
            var values = curves.Select(c => c[b]).OrderBy(v => v).DefaultIfEmpty(0).ToList();
            List<double> remaining = b == bins ? [0] : RemainingFrom(b);
            steps.Add(new NeedStep(at, values[values.Count / 2], values[^1], remaining.Count == 0 ? 0 : remaining.Max()));
        }
        return new NeedForecast(steps, curves.Count);
    }

    /// <summary>What's spare before the cheap-rate window, or null outside the day when it doesn't apply.</summary>
    public static SpareEstimate? Estimate(ControllerOptions options, DateTimeOffset now, int socPercent, double? neededWh, NeedForecast? forecast = null)
    {
        var spare = options.Spare;
        var time = TimeOnly.FromDateTime(now.LocalDateTime);
        if (!spare.Enabled || !options.CheapRate.Enabled || options.CheapRate.Contains(time)) return null;

        var availableWh = Math.Max(0, socPercent - spare.FloorPercent) / 100.0 * options.BatteryCapacityWh;
        return new SpareEstimate(availableWh, neededWh, spare.MarginWh, socPercent, options.BatteryCapacityWh, forecast, spare.FloorPercent);
    }

    /// <summary>
    /// The day's profile from <paramref name="start"/>: the usual battery level at each step (the level at the start,
    /// moved by the median change logged on recent days, so solar and car-slot charging count) and the plenty level
    /// (floor, margin and the most any recent day still needed from that point).
    /// </summary>
    public static DayProfile? Day(
        ControllerOptions options, DateTimeOffset start, int startSoc, NeedForecast? need,
        IReadOnlyList<(DateTimeOffset At, int Soc)> socs, int days)
    {
        if (need is null || need.Steps.Count < 2) return null;
        var floor = options.Spare.FloorPercent;
        var capacity = Math.Max(1, options.BatteryCapacityWh);

        var deltas = need.Steps.Select(_ => new List<double>()).ToList();
        var localStart = start.ToLocalTime();
        for (var d = 1; d <= days; d++)
        {
            var dayStart = AtLocal(localStart.Date.AddDays(-d) + localStart.TimeOfDay);
            if (SocAt(socs, dayStart) is not { } baseline) continue;
            for (var i = 0; i < need.Steps.Count; i++)
            {
                if (SocAt(socs, dayStart + (need.Steps[i].At - start)) is { } level) deltas[i].Add(level - baseline);
            }
        }

        var steps = need.Steps.Select((s, i) =>
        {
            var sorted = deltas[i].OrderBy(v => v).ToList();
            double? typical = sorted.Count == 0 ? null : Math.Clamp(startSoc + sorted[sorted.Count / 2], 0, 100);
            return new DayStep(s.At, typical, floor + (options.Spare.MarginWh + s.BusiestRemainingWh) / capacity * 100);
        }).ToList();
        return new DayProfile(start, startSoc, steps);
    }

    // The logged level at or just before a moment, if there's a reading within a few minutes of it.
    private static int? SocAt(IReadOnlyList<(DateTimeOffset At, int Soc)> socs, DateTimeOffset at)
    {
        int? found = null;
        foreach (var (time, soc) in socs)
        {
            if (time > at) break;
            if (at - time <= MaxGap) found = soc;
        }
        return found;
    }

    private static DateTimeOffset AtLocal(DateTime local) => new(local, TimeZoneInfo.Local.GetUtcOffset(local));
}
