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

    [TestMethod]
    public void Unconfirmed_car_charge_is_pending_at_off_peak_with_the_peak_risk_shown()
    {
        var pending = new HashSet<DateTimeOffset> { Local(12) };

        var day = DayCost.Calculate([new HalfHour(Local(12), 2000, 0), new HalfHour(Local(13), 500, 0)], Rates, Window, [], [], pending);

        Assert.AreEqual(2.0, day.ImportPendingKwh, 1e-9);
        Assert.AreEqual(0.5, day.ImportPeakKwh, 1e-9);
        Assert.AreEqual(2.0 * 3.80 + 0.5 * 28.48, day.ImportCostP, 1e-9);
        Assert.AreEqual(2.0 * (28.48 - 3.80), day.PendingExtraP, 1e-9);
    }

    [TestMethod]
    public void Confirmed_slot_beats_pending()
    {
        var pending = new HashSet<DateTimeOffset> { Local(12) };

        var band = DayCost.Band(Local(12), Window, [new TimeRange(Local(12), Local(12, 30))], [], pending);

        Assert.AreEqual(RateBand.OffPeak, band);
    }

    [TestMethod]
    public void Car_half_hours_are_pending_for_two_hours_then_count_as_peak()
    {
        var now = Local(15);
        BatterySample[] samples =
        [
            new(Local(12, 10), -800, 7000),  // car, 2.5 h ago: too old
            new(Local(14, 5), 800, 6500),    // car, recent
            new(Local(14, 40), 800, 2000),   // kettle-sized
        ];

        var pending = DayCost.CarHalfHours(samples, 3500, now, TimeSpan.FromHours(2));

        CollectionAssert.AreEquivalent(new[] { Local(14) }, pending.ToArray());
    }

    [TestMethod]
    public void Car_share_is_priced_at_the_rate_of_its_half_hour_and_capped_at_the_meter()
    {
        var car = new Dictionary<DateTimeOffset, double> { [Local(2)] = 3500, [Local(12)] = 5000 };

        var day = DayCost.Calculate([new HalfHour(Local(2), 3800, 0), new HalfHour(Local(12), 4000, 0)], Rates, Window, [], [], carWh: car);

        Assert.AreEqual(3.5 + 4.0, day.CarKwh, 1e-9, "the midday estimate is capped at the 4 kWh the meter saw");
        Assert.AreEqual(3.5 * 3.80 + 4.0 * 28.48, day.CarCostP, 1e-9);
    }

    [TestMethod]
    public void Car_sessions_start_big_carry_on_while_tapering_and_leave_out_the_house()
    {
        var t = Local(2);
        // House at 300 W, then the car: 7.5 kW for 20 min, tapering to 2 kW for 10 min, then off. One sample a minute.
        var loads = Enumerable.Repeat(300, 30).Concat(Enumerable.Repeat(7800, 20)).Concat(Enumerable.Repeat(2300, 10)).Concat(Enumerable.Repeat(300, 30));
        var samples = loads.Select((w, i) => new BatterySample(t.AddMinutes(i), 0, w)).ToList();

        var wh = CarCharging.WhByHalfHour(samples, startW: 6000, stillW: 1500);

        Assert.AreEqual(7500 * 20 / 60.0 + 2000 * 10 / 60.0, wh.Values.Sum(), 1);
        Assert.IsFalse(wh.ContainsKey(Local(3)), "nothing after the car stopped");
    }

    [TestMethod]
    public void Cooking_on_its_own_is_not_the_car()
    {
        var t = Local(18);
        var samples = Enumerable.Range(0, 60).Select(i => new BatterySample(t.AddMinutes(i), 0, i < 30 ? 4500 : 300)).ToList();

        Assert.IsEmpty(CarCharging.WhByHalfHour(samples, 6000, 1500));
    }
}
