using Solix.Core;

namespace Solix.Tests;

[TestClass]
public sealed class CyclingLoadTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 17, 13, 0, 0, TimeSpan.Zero);

    private static List<(DateTimeOffset, int)> Loads(params int[] loads) =>
        loads.Select((w, i) => (Start.AddSeconds(42 * i), w)).ToList();

    [TestMethod]
    public void Two_spikes_that_came_and_went_give_the_base() =>
        Assert.AreEqual(150, CyclingLoad.BaseW(Loads(200, 2200, 150, 180, 2100, 2150, 190, 2200)));

    [TestMethod]
    public void One_spike_is_not_cycling() =>
        Assert.IsNull(CyclingLoad.BaseW(Loads(200, 2200, 150, 180, 190)));

    [TestMethod]
    public void A_load_that_stays_on_is_not_cycling() =>
        Assert.IsNull(CyclingLoad.BaseW(Loads(200, 2200, 2200, 2250, 2200, 2300)));
}
