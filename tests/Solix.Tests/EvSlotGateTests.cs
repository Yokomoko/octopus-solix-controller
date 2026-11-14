using Solix.Core;

namespace Solix.Tests;

[TestClass]
public sealed class EvSlotGateTests
{
    private static readonly DateTimeOffset At0800 = new(2026, 11, 13, 8, 0, 0, TimeSpan.Zero);

    [TestMethod]
    [DataRow(true, 7200, true, DisplayName = "planned slot with the car charging is cheap")]
    [DataRow(true, 200, false, DisplayName = "planned slot with the car idle is peak")]
    [DataRow(true, null, false, DisplayName = "planned slot without a reading is not trusted")]
    [DataRow(false, 7200, false, DisplayName = "car charging with no planned slot is not cheap")]
    public void Cheap_only_when_planned_and_car_charging(bool planned, int? demandW, bool expected)
    {
        Assert.AreEqual(expected, new EvSlotGate().IsCheap(At0800.AddMinutes(5), planned, demandW, 3500));
    }

    [TestMethod]
    public void Stays_cheap_to_the_end_of_the_half_hour_once_the_car_charged()
    {
        var gate = new EvSlotGate();
        Assert.IsTrue(gate.IsCheap(At0800.AddMinutes(5), true, 7200, 3500));

        // Unplugged and the slot dropped at 08:15: still cheap until 08:30.
        Assert.IsTrue(gate.IsCheap(At0800.AddMinutes(15), false, 200, 3500));
        Assert.IsTrue(gate.IsCheap(At0800.AddMinutes(29), false, 200, 3500));
        Assert.IsFalse(gate.IsCheap(At0800.AddMinutes(30), false, 200, 3500));
    }

    [TestMethod]
    public void Next_half_hour_needs_the_car_again()
    {
        var gate = new EvSlotGate();
        Assert.IsTrue(gate.IsCheap(At0800.AddMinutes(10), true, 7200, 3500));

        // Slot still planned after 08:30 but the car has stopped drawing.
        Assert.IsFalse(gate.IsCheap(At0800.AddMinutes(31), true, 200, 3500));
    }

    [TestMethod]
    public void Tapering_car_keeps_the_slot_with_the_lower_threshold()
    {
        var gate = new EvSlotGate();
        Assert.IsTrue(gate.IsCheap(At0800.AddMinutes(10), true, 7200, 3500, 1500));

        // Next half-hour, same slot, car tapering at 2.4 kW.
        Assert.IsTrue(gate.IsCheap(At0800.AddMinutes(31), true, 2400, 3500, 1500));
        Assert.IsFalse(gate.IsCheap(At0800.AddMinutes(61), true, 300, 3500, 1500));
    }

    [TestMethod]
    public void Lower_threshold_does_not_carry_into_the_next_slot()
    {
        var gate = new EvSlotGate();
        Assert.IsTrue(gate.IsCheap(At0800.AddMinutes(10), true, 7200, 3500, 1500));
        Assert.IsFalse(gate.IsCheap(At0800.AddMinutes(45), false, 200, 3500, 1500));

        // A new slot with a kettle on and the car idle.
        Assert.IsFalse(gate.IsCheap(At0800.AddMinutes(95), true, 3200, 3500, 1500));
    }

    [TestMethod]
    public void House_demand_excludes_the_battery()
    {
        var options = new ControllerOptions();
        var grid = new GridReading(At0800, 1450);
        var charging = new BatteryState(1300, -1300, 50, 3);

        // This morning: 1,450 W import while the battery charged at 1,300 W means the house drew 150 W.
        Assert.AreEqual(150, EvSlotGate.HouseDemandW(options, At0800.AddSeconds(10), grid, charging, null));
        Assert.AreEqual(150, EvSlotGate.HouseDemandW(options, At0800.AddSeconds(10), grid, null, -1300));
        Assert.IsNull(EvSlotGate.HouseDemandW(options, At0800.AddMinutes(5), grid, charging, null));
    }
}
