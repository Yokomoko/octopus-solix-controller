namespace Solix.Core;

/// <summary>Spots an appliance switching on and off faster than the meter reports, like an oven thermostat.</summary>
public static class CyclingLoad
{
    public const int SwingW = 1000;
    public const int MinSpikes = 2;
    public const double MaxOnShare = 0.3;
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(8);

    /// <summary>True if at least two spikes have come and gone recently.</summary>
    public static bool IsCycling(IReadOnlyList<(DateTimeOffset At, int LoadW)> recent)
    {
        if (recent.Count < 3) return false;
        var baseW = recent.Min(x => x.LoadW);
        var spikes = 0;
        for (var i = 1; i < recent.Count; i++)
        {
            if (recent[i - 1].LoadW - baseW >= SwingW && recent[i].LoadW - baseW < SwingW) spikes++;
        }
        return spikes >= MinSpikes;
    }

    /// <summary>Share of the time the spikes were on, from 0 to 1.</summary>
    public static double OnShare(IReadOnlyList<(DateTimeOffset At, int LoadW)> recent)
    {
        if (recent.Count < 2) return 0;
        var baseW = recent.Min(x => x.LoadW);
        double on = 0, total = 0;
        for (var i = 0; i < recent.Count - 1; i++)
        {
            var seconds = (recent[i + 1].At - recent[i].At).TotalSeconds;
            total += seconds;
            if (recent[i].LoadW - baseW >= SwingW) on += seconds;
        }
        return total > 0 ? on / total : 0;
    }

    /// <summary>The load underneath short spikes, worth covering instead of chasing them; null if not cycling or the spikes are on too much of the time to leave uncovered.</summary>
    public static int? BaseW(IReadOnlyList<(DateTimeOffset At, int LoadW)> recent) =>
        IsCycling(recent) && OnShare(recent) < MaxOnShare ? recent.Min(x => x.LoadW) : null;
}
