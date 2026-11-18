namespace Solix.Core;

/// <summary>When to read the meter next: sooner the more the house is changing, never past the hourly budget.</summary>
public static class PollPacing
{
    // Changes below the first figure wait the normal time; at the second they get the quickest read.
    public const int LoadChangeFromW = 150, LoadChangeToW = 300;
    public const int TargetChangeFromW = 100, TargetChangeToW = 200;

    /// <summary>How busy the house is, from 0 (steady) to 1 (a big jump, or an appliance cycling).</summary>
    public static double Urgency(IReadOnlyList<(DateTimeOffset At, int LoadW)> loads, int? previousTargetW, int targetW, bool cycling)
    {
        if (cycling) return 1;
        var load = loads.Count >= 2 ? Scale(Math.Abs(loads[^1].LoadW - loads[^2].LoadW), LoadChangeFromW, LoadChangeToW) : 0;
        var target = previousTargetW is { } before ? Scale(Math.Abs(targetW - before), TargetChangeFromW, TargetChangeToW) : 0;
        return Math.Max(load, target);
    }

    /// <summary>Time until the next read. Quicker reads stop short of the budget so normal reads always have room.</summary>
    public static TimeSpan Next(ControllerOptions options, IReadOnlyList<DateTimeOffset> calls, DateTimeOffset now, double urgency)
    {
        var inLastHour = calls.Where(t => now - t < TimeSpan.FromHours(1)).OrderBy(t => t).ToList();
        var budget = Math.Max(1, options.MeterCallsPerHour);
        var seconds = (double)options.PollSeconds;
        if (urgency > 0 && inLastHour.Count < budget - options.FastPollReserve)
            seconds -= (options.PollSeconds - options.FastPollSeconds) * Math.Clamp(urgency, 0, 1);
        var wanted = TimeSpan.FromSeconds(Math.Round(seconds));
        if (inLastHour.Count < budget) return wanted;

        // Budget spent: wait until enough calls have aged out of the hour.
        var free = inLastHour[inLastHour.Count - budget] + TimeSpan.FromHours(1) - now;
        return free > wanted ? free : wanted;
    }

    private static double Scale(int change, int from, int to) => Math.Clamp((change - from) / (double)(to - from), 0, 1);
}
