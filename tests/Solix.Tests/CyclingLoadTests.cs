using Solix.Core;

namespace Solix.Tests;

[TestClass]
public sealed class CyclingLoadTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 17, 13, 0, 0, TimeSpan.Zero);

    private static List<(DateTimeOffset, int)> Loads(params int[] loads) =>
        loads.Select((w, i) => (Start.AddSeconds(42 * i), w)).ToList();

    [TestMethod]
    public void Short_spikes_give_the_base() =>
        Assert.AreEqual(150, CyclingLoad.BaseW(Loads(200, 2200, 150, 180, 190, 2100, 170, 160, 190, 200)));

    [TestMethod]
    public void Spikes_on_most_of_the_time_are_chased()
    {
        var loads = Loads(30, 2300, 2200, 40, 2250, 2300, 2280, 50, 2200, 2190);

        Assert.IsTrue(CyclingLoad.IsCycling(loads));
        Assert.IsNull(CyclingLoad.BaseW(loads));
    }

    [TestMethod]
    public void On_share_is_weighted_by_time() =>
        Assert.AreEqual(0.25, CyclingLoad.OnShare(Loads(100, 100, 2100, 100, 100)), 0.001);

    [TestMethod]
    public void One_spike_is_not_cycling() =>
        Assert.IsNull(CyclingLoad.BaseW(Loads(200, 2200, 150, 180, 190)));

    [TestMethod]
    public void A_load_that_stays_on_is_not_cycling() =>
        Assert.IsFalse(CyclingLoad.IsCycling(Loads(200, 2200, 2200, 2250, 2200, 2300)));
}
