using Solix.Core;

namespace Solix.Tests;

[TestClass]
public sealed class BatteryValueTests
{
    private static readonly TariffRates Rates = new("Test", PeakP: 28.48, OffPeakP: 3.80, ExportP: 0, StandingP: 0);
    private static readonly DateTimeOffset T0 = new(2026, 11, 12, 14, 0, 0, TimeSpan.Zero);

    // An hour of samples two minutes apart.
    private static List<BatterySample> Hour(int outputW, int demandW) =>
        Enumerable.Range(0, 31).Select(i => new BatterySample(T0.AddMinutes(2 * i), outputW, demandW)).ToList();

    [TestMethod]
    public void Peak_use_saves_peak_minus_off_peak()
    {
        var v = BatteryValue.Calculate(Hour(1000, 40), Rates, _ => RateBand.Peak);

        Assert.AreEqual(1.0, v.DisplacedKwh, 1e-9);
        Assert.AreEqual(28.48 - 3.80, v.ShiftSavedP, 1e-9);
        Assert.AreEqual(28.48 - 3.80, v.NetSavedP, 1e-9);
    }

    [TestMethod]
    public void Off_peak_use_saves_nothing()
    {
        var v = BatteryValue.Calculate(Hour(1000, 40), Rates, _ => RateBand.OffPeak);

        Assert.AreEqual(0, v.ShiftSavedP, 1e-9);
    }

    [TestMethod]
    public void Discharge_while_exporting_is_wasted()
    {
        // 100 W out while the meter shows 85 W export: 85 W went to the grid, 15 W covered the house.
        var v = BatteryValue.Calculate(Hour(100, -85), Rates, _ => RateBand.Peak);

        Assert.AreEqual(0.085, v.WastedKwh, 1e-9);
        Assert.AreEqual(0.015, v.DisplacedKwh, 1e-9);
        Assert.AreEqual(0.085 * 28.48, v.WastedP, 1e-9);
    }

    [TestMethod]
    public void Solar_charging_saves_the_off_peak_cost()
    {
        // Charging 500 W while still importing 200 W: only 300 W came from spare solar.
        var v = BatteryValue.Calculate(Hour(-500, 200), Rates, _ => RateBand.Peak);

        Assert.AreEqual(0.3, v.SolarChargedKwh, 1e-9);
        Assert.AreEqual(0.3 * 3.80, v.SolarSavedP, 1e-9);
    }

    [TestMethod]
    public void Gaps_are_not_counted()
    {
        var samples = new List<BatterySample> { new(T0, 300, 100), new(T0.AddHours(2), 300, 100) };

        var v = BatteryValue.Calculate(samples, Rates, _ => RateBand.Peak);

        Assert.AreEqual(0, v.CoveredHours, 1e-9);
        Assert.AreEqual(0, v.NetSavedP, 1e-9);
    }
}
