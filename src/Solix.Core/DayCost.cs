namespace Solix.Core;

/// <summary>One half-hour of meter data: energy imported and exported in that half-hour.</summary>
public sealed record HalfHour(DateTimeOffset Start, double ImportWh, double ExportWh);

/// <summary>Tariff prices in pence, including VAT as Octopus reports them.</summary>
public sealed record TariffRates(string Name, double PeakP, double OffPeakP, double ExportP, double StandingP);

/// <summary>A time range that is billed differently, such as an EV smart slot or a Power Up.</summary>
public sealed record TimeRange(DateTimeOffset Start, DateTimeOffset End)
{
    public bool Overlaps(DateTimeOffset start, DateTimeOffset end) => Start < end && start < End;
}

public sealed record DaySummary(
    double ImportPeakKwh,
    double ImportOffPeakKwh,
    double ImportFreeKwh,
    double ExportKwh,
    double ImportCostP,
    double ExportEarnedP,
    double StandingP,
    double TotalP);

public enum RateBand
{
    Peak,
    OffPeak,
    Free,
}

public static class DayCost
{
    /// <summary>Which rate applies to the half-hour starting at <paramref name="start"/>.</summary>
    public static RateBand Band(
        DateTimeOffset start,
        CheapRateOptions offPeakWindow,
        IReadOnlyList<TimeRange> evSlots,
        IReadOnlyList<TimeRange> freeSessions)
    {
        var end = start.AddMinutes(30);
        if (freeSessions.Any(s => s.Overlaps(start, end))) return RateBand.Free;
        if (offPeakWindow.Contains(TimeOnly.FromDateTime(start.LocalDateTime)) || evSlots.Any(s => s.Overlaps(start, end)))
            return RateBand.OffPeak;
        return RateBand.Peak;
    }

    public static double Price(RateBand band, TariffRates rates) => band switch
    {
        RateBand.Free => 0,
        RateBand.OffPeak => rates.OffPeakP,
        _ => rates.PeakP,
    };

    /// <summary>Start of the billing half-hour containing <paramref name="at"/>.</summary>
    public static DateTimeOffset HalfHourStart(DateTimeOffset at) =>
        new(at.Year, at.Month, at.Day, at.Hour, at.Minute < 30 ? 0 : 30, 0, at.Offset);

    /// <summary>
    /// Prices a day's half-hours. A half-hour is free if it overlaps a Power Up, off-peak if it falls in the
    /// cheap-rate window or overlaps an EV smart slot, and peak otherwise.
    /// </summary>
    public static DaySummary Calculate(
        IEnumerable<HalfHour> halfHours,
        TariffRates rates,
        CheapRateOptions offPeakWindow,
        IReadOnlyList<TimeRange> evSlots,
        IReadOnlyList<TimeRange> freeSessions)
    {
        double peak = 0, offPeak = 0, free = 0, export = 0;

        foreach (var h in halfHours)
        {
            var kwh = Math.Max(0, h.ImportWh) / 1000;
            export += Math.Max(0, h.ExportWh) / 1000;

            switch (Band(h.Start, offPeakWindow, evSlots, freeSessions))
            {
                case RateBand.Free: free += kwh; break;
                case RateBand.OffPeak: offPeak += kwh; break;
                default: peak += kwh; break;
            }
        }

        var importCost = peak * rates.PeakP + offPeak * rates.OffPeakP;
        var exportEarned = export * rates.ExportP;
        return new DaySummary(peak, offPeak, free, export, importCost, exportEarned, rates.StandingP,
            importCost - exportEarned + rates.StandingP);
    }
}
