using Solix.Core;

namespace Solix.Tests;

[TestClass]
public sealed class SpareChargeTests
{
    private static DateTimeOffset Local(DateTime day, int hour, int minute = 0)
    {
        var local = day.Date.AddHours(hour).AddMinutes(minute);
        return new DateTimeOffset(local, TimeZoneInfo.Local.GetUtcOffset(local));
    }

    // One sample a minute from 17:00 to 23:30 on the given day, at a steady house load.
    private static IEnumerable<BatterySample> Evening(DateTime day, int houseW) =>
        Enumerable.Range(0, 391).Select(m => new BatterySample(Local(day, 17).AddMinutes(m), houseW, 0));

    private static ControllerOptions Options() => new()
    {
        BatteryCapacityWh = 5000,
        CheapRate = new CheapRateOptions { Enabled = true, Start = new(23, 30), End = new(5, 30) },
        Spare = new SpareOptions { Enabled = true, ExportTargetW = 5, FloorPercent = 10, MarginWh = 500 },
    };

    [TestMethod]
    public void Need_is_the_busiest_recent_evening_capped_at_the_discharge_limit()
    {
        var today = new DateTime(2026, 11, 16);
        var history = Evening(today.AddDays(-1), 300).Concat(Evening(today.AddDays(-2), 2000)).ToList();

        var need = SpareCharge.NeededWh(history, Local(today, 17), new TimeOnly(23, 30), capW: 800, days: 7, carChargingW: 6000);

        Assert.AreEqual(800 * 6.5, need!.Value, 20, "the 2 kW evening counts as 800 W, the most the battery could give");
    }

    [TestMethod]
    public void Car_charging_is_left_out()
    {
        var today = new DateTime(2026, 11, 16);
        var history = Evening(today.AddDays(-1), 300).Select((s, i) => i < 60 ? s with { OutputW = -1300, DemandW = 9000 } : s).ToList();

        var need = SpareCharge.NeededWh(history, Local(today, 17), new TimeOnly(23, 30), capW: 800, days: 7, carChargingW: 6000);

        Assert.AreEqual(300 * 5.5, need!.Value, 20, "the hour the car charged needed nothing from the battery");
    }

    [TestMethod]
    public void Cooking_counts_even_when_it_is_big()
    {
        var today = new DateTime(2026, 11, 16);
        var history = Evening(today.AddDays(-1), 300).Select((s, i) => i < 60 ? s with { OutputW = 800, DemandW = 3500 } : s).ToList();

        var need = SpareCharge.NeededWh(history, Local(today, 17), new TimeOnly(23, 30), capW: 800, days: 7, carChargingW: 6000);

        Assert.AreEqual(800 * 1 + 300 * 5.5, need!.Value, 20, "a 4.3 kW oven hour is covered up to the 800 W cap");
    }

    [TestMethod]
    public void Need_only_counts_what_is_left_of_the_evening()
    {
        var today = new DateTime(2026, 11, 16);
        var history = Evening(today.AddDays(-1), 400).ToList();

        var need = SpareCharge.NeededWh(history, Local(today, 21, 30), new TimeOnly(23, 30), capW: 800, days: 7, carChargingW: 6000);

        Assert.AreEqual(400 * 2.0, need!.Value, 10);
    }

    [TestMethod]
    public void Patchy_days_are_skipped_and_no_history_means_unknown()
    {
        var today = new DateTime(2026, 11, 16);
        var patchy = Evening(today.AddDays(-1), 300).Take(60).ToList();

        Assert.IsNull(SpareCharge.NeededWh(patchy, Local(today, 17), new TimeOnly(23, 30), 800, 7, 6000));
        Assert.IsNull(SpareCharge.NeededWh([], Local(today, 17), new TimeOnly(23, 30), 800, 7, 6000));
    }

    [TestMethod]
    [DataRow(80, 2000.0, true, DisplayName = "81% at 5pm against a 2 kWh evening: plenty")]
    [DataRow(50, 2000.0, false, DisplayName = "1.5 kWh above the floor isn't enough with the margin")]
    [DataRow(90, null, false, DisplayName = "no history: never assumes plenty")]
    public void Plenty_needs_the_expected_use_plus_margin(int soc, double? neededWh, bool plenty)
    {
        var estimate = SpareCharge.Estimate(Options(), Local(DateTime.Today, 17), soc, neededWh);

        Assert.AreEqual(plenty, estimate!.Plenty);
    }

    [TestMethod]
    public void Does_not_apply_in_the_cheap_rate_window()
    {
        Assert.IsNull(SpareCharge.Estimate(Options(), Local(DateTime.Today, 2), 90, 100));
    }

    [TestMethod]
    [DataRow(true, 10, 305, "following load, plenty spare", DisplayName = "plenty: aims for a 5 W export")]
    [DataRow(false, 10, 285, "following load", DisplayName = "not plenty: leaves the usual 15 W import")]
    public void Aims_for_a_small_export_when_there_is_plenty(bool plenty, int demandW, int expected, string reason)
    {
        var options = Options();
        options.MaxW = 800; options.Gain = 1; options.MaxStepUpW = 300; options.ImportBufferW = 15; options.StaleAfterSeconds = 120;
        var now = Local(DateTime.Today, 17);
        var spare = new SpareEstimate(plenty ? 3500 : 1000, 2000, 500);

        var decision = ControlLaw.Decide(options, now, new GridReading(now.AddSeconds(-10), demandW), new BatteryState(290, 290, 80, 3), spare: spare);

        Assert.AreEqual(expected, decision.TargetW);
        Assert.AreEqual(reason, decision.Reason);
    }

    [TestMethod]
    public void Forecast_gives_typical_and_busiest_burndown_and_finishing_level()
    {
        var today = new DateTime(2026, 11, 16);
        var history = Evening(today.AddDays(-1), 200)
            .Concat(Evening(today.AddDays(-2), 400))
            .Concat(Evening(today.AddDays(-3), 600))
            .ToList();

        var forecast = SpareCharge.Forecast(history, Local(today, 21, 30), new TimeOnly(23, 30), capW: 800, days: 7, carChargingW: 6000, stepMinutes: 30)!;

        Assert.AreEqual(3, forecast.Days);
        Assert.HasCount(5, forecast.Steps, "now, then every half hour to 23:30");
        Assert.AreEqual(0, forecast.Steps[0].TypicalWh, 1e-9);
        Assert.AreEqual(400 * 2.0, forecast.TypicalWh, 10, "median of the three evenings");
        Assert.AreEqual(600 * 2.0, forecast.BusiestWh, 10);

        var estimate = SpareCharge.Estimate(Options(), Local(today, 21, 30), 60, forecast.BusiestWh, forecast)!;
        Assert.AreEqual(60 - 800 / 5000.0 * 100, estimate.TypicalFinishPercent!.Value, 0.5);
        Assert.AreEqual(60 - 1200 / 5000.0 * 100, estimate.WorstFinishPercent!.Value, 0.5);
    }

    [TestMethod]
    public void Remaining_need_is_the_most_any_day_still_used_from_each_point()
    {
        var today = new DateTime(2026, 11, 16);
        var history = Evening(today.AddDays(-1), 200).Concat(Evening(today.AddDays(-2), 600)).ToList();

        var forecast = SpareCharge.Forecast(history, Local(today, 21, 30), new TimeOnly(23, 30), 800, 7, 6000, stepMinutes: 30)!;

        Assert.AreEqual(600 * 2.0, forecast.Steps[0].BusiestRemainingWh, 10);
        Assert.AreEqual(600 * 1.0, forecast.Steps[2].BusiestRemainingWh, 10, "an hour in, the busy day still had an hour to go");
        Assert.AreEqual(0, forecast.Steps[^1].BusiestRemainingWh, 1e-9);
    }

    [TestMethod]
    public void Usual_day_follows_logged_levels_from_this_morning_and_plenty_falls_through_the_day()
    {
        var today = new DateTime(2026, 11, 16);
        var yesterday = today.AddDays(-1);
        // Yesterday: house at 300 W from 05:30 to 06:30, and the battery went from 100% to 90% (solar or not, it's what happened).
        var history = Enumerable.Range(0, 61).Select(m => new BatterySample(Local(yesterday, 5, 30).AddMinutes(m), 300, 0)).ToList();
        var socs = Enumerable.Range(0, 61).Select(m => (Local(yesterday, 5, 30).AddMinutes(m), 100 - m / 6)).ToList();
        var start = Local(today, 5, 30);
        var need = SpareCharge.Forecast(history, start, new TimeOnly(6, 30), 800, 7, 6000, stepMinutes: 30)!;

        var day = SpareCharge.Day(Options(), start, 80, need, socs, 7)!;

        Assert.AreEqual(80, day.Steps[0].TypicalSoc!.Value, 1e-9);
        Assert.AreEqual(70, day.Steps[^1].TypicalSoc!.Value, 1, "today starts at 80% and a usual hour takes 10%");
        Assert.AreEqual(10 + (500 + 300) / 5000.0 * 100, day.Steps[0].PlentySoc, 0.5);
        Assert.AreEqual(10 + 500 / 5000.0 * 100, day.Steps[^1].PlentySoc, 0.5);
    }

    [TestMethod]
    public void A_gappy_morning_does_not_drop_a_busy_evening_from_the_plenty_line()
    {
        var today = new DateTime(2026, 11, 16);
        // Two days from 17:00: one quiet and complete, one busy but missing its first two hours.
        var quiet = Evening(today.AddDays(-1), 200);
        var busyLateOnly = Evening(today.AddDays(-2), 700).Skip(120);
        var history = quiet.Concat(busyLateOnly).OrderBy(s => s.At).ToList();

        var fromFive = SpareCharge.Forecast(history, Local(today, 17), new TimeOnly(23, 30), 800, 7, 6000, stepMinutes: 30, allowPartial: true)!;
        var fromSeven = SpareCharge.Forecast(history, Local(today, 19), new TimeOnly(23, 30), 800, 7, 6000, stepMinutes: 30)!;

        var atSeven = fromFive.Steps.Single(s => s.At == Local(today, 19));
        Assert.AreEqual(fromSeven.Steps[0].BusiestRemainingWh, atSeven.BusiestRemainingWh, 15, "19:00 means the same in both views");
        Assert.AreEqual(700 * 4.5, atSeven.BusiestRemainingWh, 15, "the busy evening counts once its log is complete");
    }
}
