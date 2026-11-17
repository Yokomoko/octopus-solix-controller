namespace Solix.Core;

/// <summary>Spots an appliance switching on and off faster than the meter reports, like an oven thermostat.</summary>
public static class CyclingLoad
{
    public const int SwingW = 1000;
    public const int MinSpikes = 2;
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(8);

    /// <summary>The house load underneath the spikes if at least two have come and gone recently, otherwise null.</summary>
    public static int? BaseW(IReadOnlyList<(DateTimeOffset At, int LoadW)> recent)
    {
        if (recent.Count < 3) return null;
        var baseW = recent.Min(x => x.LoadW);
        var spikes = 0;
        for (var i = 1; i < recent.Count; i++)
        {
            if (recent[i - 1].LoadW - baseW >= SwingW && recent[i].LoadW - baseW < SwingW) spikes++;
        }
        return spikes >= MinSpikes ? baseW : null;
    }
}
