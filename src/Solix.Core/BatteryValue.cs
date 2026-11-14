namespace Solix.Core;

/// <summary>One logged cycle: battery output (+ discharge, − charge) and the grid reading (+ import, − export).</summary>
public sealed record BatterySample(DateTimeOffset At, int OutputW, int DemandW);

public sealed record BatteryValueSummary(
    double DisplacedKwh,
    double ShiftSavedP,
    double SolarChargedKwh,
    double SolarSavedP,
    double WastedKwh,
    double WastedP,
    double NetSavedP,
    double CoveredHours);

public static class BatteryValue
{
    // Gaps longer than this (laptop asleep, app stopped) aren't counted rather than guessed.
    private static readonly TimeSpan MaxGap = TimeSpan.FromMinutes(3);

    /// <summary>
    /// What the battery was worth from logged samples, assuming it's normally filled at the off-peak rate:
    /// each kWh it supplied to the house saved (rate when used − off-peak rate). Energy charged from spare
    /// solar didn't need buying off-peak, so it saved (off-peak − export rate). Discharge while the meter
    /// showed export went to the grid and is counted as wasted.
    /// </summary>
    public static BatteryValueSummary Calculate(
        IReadOnlyList<BatterySample> samples,
        TariffRates rates,
        Func<DateTimeOffset, RateBand> band)
    {
        double displaced = 0, shiftSaved = 0, solarCharged = 0, wasted = 0, wastedP = 0, covered = 0;

        for (var i = 0; i < samples.Count - 1; i++)
        {
            var s = samples[i];
            var gap = samples[i + 1].At - s.At;
            if (gap <= TimeSpan.Zero || gap > MaxGap) continue;

            var hours = gap.TotalHours;
            covered += hours;
            var price = DayCost.Price(band(DayCost.HalfHourStart(s.At)), rates);
            var exportingW = Math.Max(0, -s.DemandW);

            if (s.OutputW > 0)
            {
                var wastedW = Math.Min(s.OutputW, exportingW);
                var usedKwh = (s.OutputW - wastedW) * hours / 1000;
                var wastedKwh = wastedW * hours / 1000;
                displaced += usedKwh;

                // Can go negative: discharging during a Power Up uses stored energy when the grid was free.
                shiftSaved += usedKwh * (price - rates.OffPeakP);
                wasted += wastedKwh;
                wastedP += wastedKwh * Math.Max(0, price - rates.ExportP);
            }
            else if (s.OutputW < 0)
            {
                // Only the part not covered by grid import came from spare solar.
                var chargeW = -s.OutputW;
                var fromGridW = Math.Min(chargeW, Math.Max(0, s.DemandW));
                solarCharged += (chargeW - fromGridW) * hours / 1000;
            }
        }

        var solarSaved = solarCharged * Math.Max(0, rates.OffPeakP - rates.ExportP);
        return new BatteryValueSummary(displaced, shiftSaved, solarCharged, solarSaved, wasted, wastedP, shiftSaved + solarSaved, covered);
    }
}
