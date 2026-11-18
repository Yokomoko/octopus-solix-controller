using Solix.Core;

namespace Solix.Tests;

[TestClass]
public sealed class PollPacingTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 18, 12, 0, 0, TimeSpan.Zero);
    private static readonly ControllerOptions Options = new() { PollSeconds = 45, FastPollSeconds = 25, MeterCallsPerHour = 110, FastPollReserve = 6 };

    private static List<DateTimeOffset> Calls(int count, int everySeconds = 20) =>
        Enumerable.Range(1, count).Select(i => Now.AddSeconds(-everySeconds * i)).ToList();

    [TestMethod]
    [DataRow(0.0, 50, 45, DisplayName = "steady: normal rate")]
    [DataRow(1.0, 50, 25, DisplayName = "big change: quickest read")]
    [DataRow(0.5, 50, 35, DisplayName = "middling change: in between")]
    [DataRow(1.0, 104, 45, DisplayName = "big change but into the reserve: normal rate")]
    public void Paces_reads(double urgency, int callsThisHour, int expectedSeconds) =>
        Assert.AreEqual(TimeSpan.FromSeconds(expectedSeconds), PollPacing.Next(Options, Calls(callsThisHour), Now, urgency));

    [TestMethod]
    public void Budget_spent_waits_for_the_oldest_call_to_age_out()
    {
        // 110 calls 30s apart: the oldest was 55 minutes ago, so it leaves the hour in 5 minutes.
        var next = PollPacing.Next(Options, Calls(110, 30), Now, urgency: 1);

        Assert.AreEqual(TimeSpan.FromMinutes(5), next);
    }

    [TestMethod]
    public void Calls_older_than_an_hour_do_not_count() =>
        Assert.AreEqual(TimeSpan.FromSeconds(25), PollPacing.Next(Options, Calls(200, 40), Now, urgency: 1));

    [TestMethod]
    [DataRow(200, 300, 150, 160, false, 0.0, DisplayName = "small wobble: steady")]
    [DataRow(200, 425, 150, 160, false, 0.5, DisplayName = "225 W load swing: halfway")]
    [DataRow(200, 600, 150, 160, false, 1.0, DisplayName = "400 W load jump: quickest")]
    [DataRow(200, 250, 150, 300, false, 0.5, DisplayName = "150 W setpoint move: halfway")]
    [DataRow(200, 250, 150, 160, true, 1.0, DisplayName = "appliance cycling: quickest")]
    public void Scales_with_how_much_changed(int loadBefore, int loadNow, int targetBefore, int targetNow, bool cycling, double expected)
    {
        var loads = new List<(DateTimeOffset, int)> { (Now.AddSeconds(-40), loadBefore), (Now, loadNow) };

        Assert.AreEqual(expected, PollPacing.Urgency(loads, targetBefore, targetNow, cycling), 1e-9);
    }
}
