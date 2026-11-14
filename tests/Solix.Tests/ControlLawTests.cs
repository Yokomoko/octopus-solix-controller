using Solix.Core;

namespace Solix.Tests;

[TestClass]
public sealed class ControlLawTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 11, 14, 0, 0, TimeSpan.Zero);

    private static ControllerOptions Options(int maxW = 800, int bufferW = 40, int stepW = 300) => new()
    {
        MaxW = maxW,
        ImportBufferW = bufferW,
        MaxStepUpW = stepW,
        Gain = 1,
        StaleAfterSeconds = 120,
        SocFloorPercent = 10,
    };

    private static GridReading Grid(int demandW, int ageSeconds = 20) => new(Now.AddSeconds(-ageSeconds), demandW);

    private static BatteryState Battery(int acW, int soc = 50) => new(acW, acW, soc, 3);

    [TestMethod]
    [DataRow(200, 250, 410, DisplayName = "importing raises output")]
    [DataRow(400, -150, 210, DisplayName = "exporting lowers output")]
    [DataRow(300, 900, 600, DisplayName = "big jump is step limited")]
    [DataRow(700, 500, 800, DisplayName = "clamped to max")]
    public void Follows_load(int acW, int demandW, int expected)
    {
        var decision = ControlLaw.Decide(Options(), Now, Grid(demandW), Battery(acW));

        Assert.AreEqual(expected, decision.TargetW);
    }

    [TestMethod]
    public void Export_with_solar_charge_off_stops_discharge_only()
    {
        var options = Options();
        options.SolarCharge.Enabled = false;

        var decision = ControlLaw.Decide(options, Now, Grid(-500), Battery(100));

        Assert.AreEqual(0, decision.TargetW);
    }

    [TestMethod]
    [DataRow(0, -300, -280, DisplayName = "idle battery starts charging from export")]
    [DataRow(0, -40, 0, DisplayName = "export under threshold is left alone")]
    [DataRow(-280, -20, -280, DisplayName = "steady charge holds")]
    [DataRow(-280, 200, -60, DisplayName = "cloud cuts charge at once")]
    [DataRow(-280, 260, 0, DisplayName = "import stops charge")]
    [DataRow(0, -900, -300, DisplayName = "charge increase is step limited")]
    public void Charges_from_solar_export(int batteryW, int demandW, int expected)
    {
        // The AC register is treated as unsigned; the sign comes from battery power.
        var battery = new BatteryState(Math.Abs(batteryW), batteryW, 50, 3);

        var decision = ControlLaw.Decide(Options(), Now, Grid(demandW), battery);

        Assert.AreEqual(expected, decision.TargetW);
    }

    [TestMethod]
    [DataRow(50, 3000, -800, DisplayName = "charges instead of covering EV import")]
    [DataRow(100, 3000, 0, DisplayName = "full battery holds, never discharges")]
    public void Ev_slot_charges_from_grid(int soc, int demandW, int expected)
    {
        var decision = ControlLaw.Decide(Options(), Now, Grid(demandW), Battery(300, soc), inEvSlot: true);

        Assert.AreEqual(expected, decision.TargetW);
    }

    [TestMethod]
    public void Ev_slot_uses_the_charge_cap_unless_set()
    {
        var options = Options();
        options.SolarCharge.MaxChargeW = 1200;

        Assert.AreEqual(-1200, ControlLaw.Decide(options, Now, Grid(7500), Battery(0), inEvSlot: true).TargetW);

        options.EvSlot.ChargeW = 600;
        Assert.AreEqual(-600, ControlLaw.Decide(options, Now, Grid(7500), Battery(0), inEvSlot: true).TargetW);
    }

    [TestMethod]
    public void Ev_slot_ignored_when_disabled()
    {
        var options = Options();
        options.EvSlot.Enabled = false;

        var decision = ControlLaw.Decide(options, Now, Grid(3000), Battery(300), inEvSlot: true);

        Assert.AreEqual(600, decision.TargetW);
    }

    [TestMethod]
    [DataRow(true, 7500, 0, DisplayName = "car charging with stale slots: holds")]
    [DataRow(true, 2500, 600, DisplayName = "stale slots, house-sized import: follows load")]
    [DataRow(false, 7500, 600, DisplayName = "fresh list with no slot: follows load")]
    public void Stale_slot_list_does_not_discharge_into_the_car(bool unknown, int demandW, int expected)
    {
        var decision = ControlLaw.Decide(Options(), Now, Grid(demandW), Battery(300), evSlotsUnknown: unknown);

        Assert.AreEqual(expected, decision.TargetW);
    }

    [TestMethod]
    public void Power_down_beats_stale_slot_hold()
    {
        var decision = ControlLaw.Decide(Options(), Now, Grid(7500), Battery(300), octopusEvent: OctopusEventKind.PowerDown, evSlotsUnknown: true);

        Assert.AreEqual(800, decision.TargetW);
    }

    [TestMethod]
    [DataRow(50, 900, 800, DisplayName = "covers big import at once, no ramp")]
    [DataRow(50, 30, 330, DisplayName = "no import buffer")]
    [DataRow(50, -400, 0, DisplayName = "exporting: stops, never charges")]
    [DataRow(10, 900, 0, DisplayName = "stops at SOC floor")]
    public void Power_down_covers_all_import(int soc, int demandW, int expected)
    {
        var decision = ControlLaw.Decide(Options(), Now, Grid(demandW), Battery(300, soc), octopusEvent: OctopusEventKind.PowerDown);

        Assert.AreEqual(expected, decision.TargetW);
    }

    [TestMethod]
    public void Power_down_beats_ev_slot()
    {
        var decision = ControlLaw.Decide(Options(), Now, Grid(3000), Battery(300), inEvSlot: true, octopusEvent: OctopusEventKind.PowerDown);

        Assert.AreEqual(800, decision.TargetW);
    }

    [TestMethod]
    public void Power_down_with_stale_grid_writes_zero()
    {
        var decision = ControlLaw.Decide(Options(), Now, Grid(900, ageSeconds: 200), Battery(300), octopusEvent: OctopusEventKind.PowerDown);

        Assert.AreEqual(0, decision.TargetW);
    }

    [TestMethod]
    [DataRow(50, -2300, DisplayName = "charges at the charge cap without needing the meter")]
    [DataRow(100, 0, DisplayName = "full battery holds")]
    public void Power_up_charges_at_cap(int soc, int expected)
    {
        var options = Options();
        options.SolarCharge.MaxChargeW = 2300;

        var decision = ControlLaw.Decide(options, Now, null, Battery(0, soc), inEvSlot: true, octopusEvent: OctopusEventKind.PowerUp);

        Assert.AreEqual(expected, decision.TargetW);
    }

    [TestMethod]
    public void Full_battery_does_not_charge()
    {
        var decision = ControlLaw.Decide(Options(), Now, Grid(-400), Battery(0, soc: 100));

        Assert.AreEqual(0, decision.TargetW);
    }

    [TestMethod]
    public void Cheap_rate_stops_at_soc_limit()
    {
        var options = Options();
        options.CheapRate = new CheapRateOptions { Enabled = true, HandBackToApp = false, ChargeW = 800, UpToSocPercent = 60 };
        var nightLocal = new DateTimeOffset(DateTime.Today.AddHours(2), TimeZoneInfo.Local.GetUtcOffset(DateTime.Today.AddHours(2)));

        Assert.AreEqual(-800, ControlLaw.Decide(options, nightLocal, null, Battery(0, soc: 59)).TargetW);
        Assert.AreEqual(0, ControlLaw.Decide(options, nightLocal, null, Battery(0, soc: 60)).TargetW);
    }

    [TestMethod]
    public void Damped_increase_closes_part_of_the_gap()
    {
        var options = Options();
        options.Gain = 0.6;

        // Wants 210 W from 0 W: closes 60% of the gap.
        var decision = ControlLaw.Decide(options, Now, Grid(250), Battery(0), outputAtReadingW: 0);

        Assert.AreEqual(126, decision.TargetW);
    }

    [TestMethod]
    public void Decrease_is_not_damped()
    {
        var options = Options();
        options.Gain = 0.6;

        var decision = ControlLaw.Decide(options, Now, Grid(-237), Battery(300));

        Assert.AreEqual(23, decision.TargetW);
    }

    [TestMethod]
    public void Charging_to_discharging_passes_through_zero()
    {
        var charging = new BatteryState(150, -150, 50, 3);

        // The lagged reading shows import, but the battery is still charging: stop first.
        var decision = ControlLaw.Decide(Options(), Now, Grid(400), charging, outputAtReadingW: 0);

        Assert.AreEqual(0, decision.TargetW);
    }

    [TestMethod]
    public void Discharging_to_charging_passes_through_zero()
    {
        var decision = ControlLaw.Decide(Options(), Now, Grid(-400), Battery(150), outputAtReadingW: 0);

        Assert.AreEqual(0, decision.TargetW);
    }

    [TestMethod]
    public void Uses_output_at_reading_time_not_now()
    {
        // Reading taken while the battery gave 100 W; it has since dropped to 60 W.
        var decision = ControlLaw.Decide(Options(), Now, Grid(4), Battery(60), outputAtReadingW: 100);

        Assert.AreEqual(64, decision.TargetW);
    }

    [TestMethod]
    public void Large_drop_is_not_rate_limited()
    {
        var decision = ControlLaw.Decide(Options(), Now, Grid(-700), Battery(800));

        Assert.AreEqual(60, decision.TargetW);
    }

    [TestMethod]
    public void Stale_reading_writes_zero()
    {
        var decision = ControlLaw.Decide(Options(), Now, Grid(500, ageSeconds: 121), Battery(300));

        Assert.AreEqual(0, decision.TargetW);
        StringAssert.Contains(decision.Reason, "stale");
    }

    [TestMethod]
    public void Missing_inputs_write_zero()
    {
        Assert.AreEqual(0, ControlLaw.Decide(Options(), Now, null, Battery(300)).TargetW);
        Assert.AreEqual(0, ControlLaw.Decide(Options(), Now, Grid(500), null).TargetW);
    }

    [TestMethod]
    public void Soc_at_floor_writes_zero()
    {
        var decision = ControlLaw.Decide(Options(), Now, Grid(500), Battery(300, soc: 10));

        Assert.AreEqual(0, decision.TargetW);
    }

    [TestMethod]
    public void Cheap_rate_window_charges_even_with_stale_data()
    {
        var options = Options();
        options.CheapRate = new CheapRateOptions { Enabled = true, HandBackToApp = false, ChargeW = 800 };
        var nightLocal = new DateTimeOffset(DateTime.Today.AddHours(2), TimeZoneInfo.Local.GetUtcOffset(DateTime.Today.AddHours(2)));

        var decision = ControlLaw.Decide(options, nightLocal, null, null);

        Assert.AreEqual(-800, decision.TargetW);
    }

    [TestMethod]
    [DataRow(true, true, 2, true, DisplayName = "app owns the window")]
    [DataRow(true, true, 12, false, DisplayName = "controller owns the day")]
    [DataRow(true, false, 2, false, DisplayName = "controller charges itself")]
    [DataRow(false, true, 2, false, DisplayName = "window disabled")]
    public void App_owns_cheap_rate_window_only_when_handing_back(bool enabled, bool handBack, int hour, bool expected)
    {
        var window = new CheapRateOptions { Enabled = enabled, HandBackToApp = handBack };
        var local = DateTime.Today.AddHours(hour);
        var now = new DateTimeOffset(local, TimeZoneInfo.Local.GetUtcOffset(local));

        Assert.AreEqual(expected, window.AppOwns(now));
    }

    [TestMethod]
    [DataRow(23, 45, true)]
    [DataRow(2, 0, true)]
    [DataRow(5, 30, false)]
    [DataRow(12, 0, false)]
    public void Window_spanning_midnight(int hour, int minute, bool expected)
    {
        var window = new CheapRateOptions { Start = new(23, 30), End = new(5, 30) };

        Assert.AreEqual(expected, window.Contains(new TimeOnly(hour, minute)));
    }
}
