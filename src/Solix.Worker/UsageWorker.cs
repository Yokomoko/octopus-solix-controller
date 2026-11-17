using Microsoft.Extensions.Options;
using Solix.Core;
using Solix.Worker.Octopus;

namespace Solix.Worker;

/// <summary>Today's import, export and cost, priced from the account's own tariff.</summary>
public sealed record UsageStatus(TariffRates? Rates, DaySummary? Today, BatteryValueSummary? Battery, DateTimeOffset? AsOf, string? Error);

/// <summary>
/// Refreshes today's usage and cost every 10 minutes from the Home Mini's half-hourly data. Separate from
/// the control loop so a slow or failed fetch never delays control.
/// </summary>
public sealed class UsageWorker(
    OctopusClient octopus,
    SampleLog log,
    RuntimeControl runtime,
    IOptionsMonitor<ControllerOptions> options,
    ILogger<UsageWorker> logger) : BackgroundService
{
    private static readonly TimeSpan Refresh = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan TariffRefresh = TimeSpan.FromHours(6);
    private static readonly TimeSpan PendingFor = TimeSpan.FromHours(2);

    private TariffRates? _rates;
    private DateTimeOffset _ratesFetchedAt = DateTimeOffset.MinValue;

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        // Let the control loop make its first Octopus calls before this adds more.
        await Task.Delay(TimeSpan.FromSeconds(20), ct);

        while (!ct.IsCancellationRequested)
        {
            await RefreshAsync(ct);
            await Task.Delay(Refresh, ct);
        }
    }

    private async Task RefreshAsync(CancellationToken ct)
    {
        var now = DateTimeOffset.Now;
        try
        {
            if (_rates is null || now - _ratesFetchedAt > TariffRefresh)
            {
                _rates = await octopus.GetTariffAsync(ct);
                _ratesFetchedAt = now;
                logger.LogInformation("Tariff: {Name}, peak {Peak:F2}p, off-peak {OffPeak:F2}p, export {Export:F2}p, standing {Standing:F2}p",
                    _rates.Name, _rates.PeakP, _rates.OffPeakP, _rates.ExportP, _rates.StandingP);
            }

            var midnight = new DateTimeOffset(now.Date, now.Offset);
            var halfHours = await octopus.GetHalfHoursAsync(midnight, now, ct);

            // Missing EV slots or Power Ups would only make the estimate pessimistic, so they're best effort.
            var evSlots = await BestEffort(() => octopus.GetCompletedDispatchesAsync(ct), "completed EV slots");
            // The control loop already keeps the planned slot list, so reuse it rather than spend another call.
            IReadOnlyList<TimeRange> planned = runtime.Ev?.Slots.Select(s => new TimeRange(s.Start, s.End)).ToList() ?? [];
            var events = await BestEffort(async () =>
                (IReadOnlyList<TimeRange>)(await octopus.GetJoinedEventsAsync(ct))
                    .Where(e => e.Kind == OctopusEventKind.PowerUp)
                    .Select(e => new TimeRange(e.Start, e.End)).ToList(), "Power Ups");

            var opts = options.CurrentValue;
            var window = opts.CheapRate;
            IReadOnlyList<TimeRange> allEv = [.. evSlots, .. planned];
            var samples = log.BatterySamplesSince(midnight);
            // Octopus can confirm a slot after the car has charged in it, so give it a while before calling it peak.
            var pending = opts.EvSlot.Enabled ? DayCost.CarHalfHours(samples, opts.EvSlot.CarChargingW, now, PendingFor) : null;
            var carWh = CarCharging.WhByHalfHour(samples, opts.Spare.CarW, opts.EvSlot.CarStillChargingW);
            var today = DayCost.Calculate(halfHours, _rates, window, allEv, events, pending, carWh);
            var batteryValue = BatteryValue.Calculate(samples, _rates, start => DayCost.Band(start, window, allEv, events, pending));

            runtime.Usage = new UsageStatus(_rates, today, batteryValue, now, null);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning("Usage refresh failed: {Message}", ex.Message);
            var previous = runtime.Usage;
            runtime.Usage = new UsageStatus(_rates, previous?.Today, previous?.Battery, previous?.AsOf, ex.Message);
        }
    }

    private async Task<IReadOnlyList<TimeRange>> BestEffort(Func<Task<IReadOnlyList<TimeRange>>> fetch, string what)
    {
        try { return await fetch(); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning("Couldn't fetch {What} for usage: {Message}", what, ex.Message);
            return [];
        }
    }
}
