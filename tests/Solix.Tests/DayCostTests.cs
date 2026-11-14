using Solix.Core;

namespace Solix.Tests;

[TestClass]
public sealed class DayCostTests
{
    private static readonly TariffRates Rates = new("Test", PeakP: 28.48, OffPeakP: 3.80, ExportP: 15, StandingP: 45.49);
    private static readonly CheapRateOptions Window = new() { Start = new(23, 30), End = new(5, 30) };

    private static DateTimeOffset Local(int hour, int minute = 0)
    {
        var local = DateTime.Today.AddHours(hour).AddMinutes(minute);
        return new DateTimeOffset(local, TimeZoneInfo.Local.GetUtcOffset(local));
    }

    [TestMethod]
    public void Prices_peak_off_peak_and_standing()
    {
        var halfHours = new[]
        {
            new HalfHour(Local(2), 1000, 0),
            new HalfHour(Local(12), 500, 200),
        };

        var day = DayCost.Calculate(halfHours, Rates, Window, [], []);

        Assert.AreEqual(1.0, day.ImportOffPeakKwh, 1e-9);
        Assert.AreEqual(0.5, day.ImportPeakKwh, 1e-9);
        Assert.AreEqual(0.2, day.ExportKwh, 1e-9);
        Assert.AreEqual(3.80 + 0.5 * 28.48 - 0.2 * 15 + 45.49, day.TotalP, 1e-9);
    }

    [TestMethod]
    public void Ev_slot_makes_daytime_half_hour_off_peak()
    {
        var slot = new TimeRange(Local(9), Local(9, 30));

        var day = DayCost.Calculate([new HalfHour(Local(9), 1000, 0), new HalfHour(Local(9, 30), 1000, 0)], Rates, Window, [slot], []);

        Assert.AreEqual(1.0, day.ImportOffPeakKwh, 1e-9);
        Assert.AreEqual(1.0, day.ImportPeakKwh, 1e-9);
    }

    [TestMethod]
    public void Power_up_is_free()
    {
        var session = new TimeRange(Local(11), Local(12));

        var day = DayCost.Calculate([new HalfHour(Local(11), 2000, 0), new HalfHour(Local(11, 30), 2000, 0)], Rates, Window, [], [session]);

        Assert.AreEqual(4.0, day.ImportFreeKwh, 1e-9);
        Assert.AreEqual(0, day.ImportCostP, 1e-9);
    }
}
