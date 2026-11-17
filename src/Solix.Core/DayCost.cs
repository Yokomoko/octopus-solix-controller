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
    double ImportPendingKwh,
    double PendingExtraP,
    double CarKwh,
    double CarCostP,
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

    /// <summary>The car charged outside a known slot; Octopus usually confirms it as a slot afterwards.</summary>
    Pending,
}

public static class DayCost
{
    /// <summary>Which rate applies to the half-hour starting at <paramref name="start"/>.</summary>
    public static RateBand Band(
        DateTimeOffset start,
        CheapRateOptions offPeakWindow,
        IReadOnlyList<TimeRange> evSlots,
        IReadOnlyList<TimeRange> freeSessions,
        IReadOnlySet<DateTimeOffset>? pending = null)
    {
        var end = start.AddMinutes(30);
        if (freeSessions.Any(s => s.Overlaps(start, end))) return RateBand.Free;
        if (offPeakWindow.Contains(TimeOnly.FromDateTime(start.LocalDateTime)) || evSlots.Any(s => s.Overlaps(start, end)))
            return RateBand.OffPeak;
        return pending?.Contains(start) == true ? RateBand.Pending : RateBand.Peak;
    }

    /// <summary>Pending is priced off-peak, since that's how it nearly always ends up billed.</summary>
    public static double Price(RateBand band, TariffRates rates) => band switch
    {
        RateBand.Free => 0,
        RateBand.OffPeak or RateBand.Pending => rates.OffPeakP,
        _ => rates.PeakP,
    };

    /// <summary>
    /// Half-hours where the house drew car-sized power and that ended less than <paramref name="confirmWithin"/> ago.
    /// Older ones still without a slot are treated as a real peak charge.
    /// </summary>
    public static IReadOnlySet<DateTimeOffset> CarHalfHours(
        IEnumerable<BatterySample> samples, int carChargingW, DateTimeOffset now, TimeSpan confirmWithin) =>
        samples
            .Where(s => s.OutputW + s.DemandW >= carChargingW)
            .Select(s => HalfHourStart(s.At))
            .Where(start => now - start.AddMinutes(30) < confirmWithin)
            .ToHashSet();

    /// <summary>Start of the billing half-hour containing <paramref name="at"/>.</summary>
    public static DateTimeOffset HalfHourStart(DateTimeOffset at) =>
        new(at.Year, at.Month, at.Day, at.Hour, at.Minute < 30 ? 0 : 30, 0, at.Offset);

    /// <summary>
    /// Prices a day's half-hours. A half-hour is free if it overlaps a Power Up, off-peak if it falls in the
    /// cheap-rate window or overlaps an EV smart slot, pending if the car charged with no slot yet, and peak otherwise.
    /// </summary>
    public static DaySummary Calculate(
        IEnumerable<HalfHour> halfHours,
        TariffRates rates,
        CheapRateOptions offPeakWindow,
        IReadOnlyList<TimeRange> evSlots,
        IReadOnlyList<TimeRange> freeSessions,
        IReadOnlySet<DateTimeOffset>? pending = null,
        IReadOnlyDictionary<DateTimeOffset, double>? carWh = null)
    {
        double peak = 0, offPeak = 0, free = 0, pend = 0, export = 0, car = 0, carCost = 0;

        foreach (var h in halfHours)
        {
            var kwh = Math.Max(0, h.ImportWh) / 1000;
            export += Math.Max(0, h.ExportWh) / 1000;

            var band = Band(h.Start, offPeakWindow, evSlots, freeSessions, pending);

            // The car can't have used more than the meter recorded for the half-hour.
            var carKwh = Math.Min(kwh, (carWh?.GetValueOrDefault(h.Start) ?? 0) / 1000);
            car += carKwh;
            carCost += carKwh * Price(band, rates);

            switch (band)
            {
                case RateBand.Free: free += kwh; break;
                case RateBand.OffPeak: offPeak += kwh; break;
                case RateBand.Pending: pend += kwh; break;
                default: peak += kwh; break;
            }
        }

        var importCost = peak * rates.PeakP + (offPeak + pend) * rates.OffPeakP;
        var exportEarned = export * rates.ExportP;
        return new DaySummary(peak, offPeak, free, pend, pend * (rates.PeakP - rates.OffPeakP), car, carCost, export, importCost, exportEarned,
            rates.StandingP, importCost - exportEarned + rates.StandingP);
    }
}
