using Solix.Core;

namespace Solix.Tests;

[TestClass]
public sealed class EveningExportTests
{
    private static ControllerOptions Options(bool enabled = true) => new()
    {
        MaxW = 800,
        ImportBufferW = 40,
        MaxStepUpW = 300,
        Gain = 1,
        StaleAfterSeconds = 120,
        SocFloorPercent = 5,
        CheapRate = new CheapRateOptions { Enabled = true, Start = new(23, 30), End = new(5, 30) },
        EveningExport = new EveningExportOptions { Enabled = enabled, Start = new(21, 0), FloorPercent = 10, CapacityWh = 5000, MinExportW = 100 },
    };

    [TestMethod]
    [DataRow(22, 30, 50, 200, 1800, DisplayName = "spare above floor, less the house, over the hour left")]
    [DataRow(21, 0, 30, 300, 100, DisplayName = "small spare spread over the whole window")]
    [DataRow(21, 0, 30, 350, 0, DisplayName = "spread under the minimum isn't worth it")]
    [DataRow(21, 30, 40, 800, 0, DisplayName = "busy house needs it all")]
    [DataRow(22, 0, 10, 100, 0, DisplayName = "at the export floor")]
    [DataRow(20, 59, 80, 200, 0, DisplayName = "before the window")]
    [DataRow(23, 30, 80, 200, 0, DisplayName = "cheap rate has started")]
    public void Exports_only_what_the_house_will_not_need(int hour, int minute, int soc, int houseW, int expected)
    {
        Assert.AreEqual(expected, EveningExport.ExtraW(Options(), new TimeOnly(hour, minute), soc, houseW));
    }

    [TestMethod]
    public void Off_by_default()
    {
        Assert.IsFalse(new ControllerOptions().EveningExport.Enabled);
        Assert.AreEqual(0, EveningExport.ExtraW(Options(enabled: false), new TimeOnly(22, 30), 80, 200));
    }

    [TestMethod]
    public void Discharges_past_the_house_load_in_the_window()
    {
        var now = Local(22, 30);
        var grid = new GridReading(now.AddSeconds(-20), 50);

        var decision = ControlLaw.Decide(Options(), now, grid, new BatteryState(200, 200, 50, 3));

        Assert.AreEqual(500, decision.TargetW, "800 W wanted, step limited from 200 W");
        StringAssert.Contains(decision.Reason, "exporting spare");
    }

    [TestMethod]
    public void Below_export_floor_just_follows_the_load()
    {
        var now = Local(22, 30);
        var grid = new GridReading(now.AddSeconds(-20), 50);

        var decision = ControlLaw.Decide(Options(), now, grid, new BatteryState(200, 200, 8, 3));

        Assert.AreEqual(210, decision.TargetW);
        Assert.AreEqual("following load", decision.Reason);
    }

    private static DateTimeOffset Local(int hour, int minute)
    {
        var local = DateTime.Today.AddHours(hour).AddMinutes(minute);
        return new DateTimeOffset(local, TimeZoneInfo.Local.GetUtcOffset(local));
    }
}
