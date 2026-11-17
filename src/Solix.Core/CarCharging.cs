namespace Solix.Core;

public static class CarCharging
{
    private static readonly TimeSpan MaxGap = TimeSpan.FromMinutes(3);

    /// <summary>
    /// Estimated car energy per billing half-hour from logged samples. A session starts when the house (battery excluded)
    /// draws <paramref name="startW"/> and carries on while it stays above <paramref name="stillW"/>, so tapering counts.
    /// The car's share is the house load less the day's usual non-car load.
    /// </summary>
    public static IReadOnlyDictionary<DateTimeOffset, double> WhByHalfHour(IReadOnlyList<BatterySample> samples, int startW, int stillW)
    {
        var loads = samples.Select(s => s.OutputW + s.DemandW).ToList();
        var baseW = Median(loads.Where(l => l >= 0 && l < stillW).ToList());

        var result = new Dictionary<DateTimeOffset, double>();
        var charging = false;
        for (var i = 0; i < samples.Count - 1; i++)
        {
            var load = loads[i];
            charging = charging ? load >= stillW : load >= startW;
            if (!charging) continue;

            var gap = samples[i + 1].At - samples[i].At;
            if (gap <= TimeSpan.Zero || gap > MaxGap) continue;

            var key = DayCost.HalfHourStart(samples[i].At);
            result[key] = result.GetValueOrDefault(key) + Math.Max(0, load - baseW) * gap.TotalHours;
        }
        return result;
    }

    private static double Median(List<int> values)
    {
        if (values.Count == 0) return 0;
        values.Sort();
        return values[values.Count / 2];
    }
}
