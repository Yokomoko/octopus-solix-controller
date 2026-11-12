using Microsoft.Extensions.Options;
using Microsoft.Win32;
using Solix.Core;
using Solix.Worker.Battery;
using Solix.Worker.Octopus;

namespace Solix.Worker;

/// <summary>Runs the control loop: Octopus every PollSeconds, mode guard every ModeGuardSeconds.</summary>
public sealed class ControlWorker(
    OctopusClient octopus,
    SolixModbus battery,
    SampleLog log,
    RuntimeControl runtime,
    IOptionsMonitor<ControllerOptions> options,
    IConfiguration config,
    ILogger<ControlWorker> logger) : BackgroundService
{
    private readonly SemaphoreSlim _wake = new(0);
    private readonly string _handBackFile = Path.Combine(SampleLog.DataDirectory(config), "hand-back-mode.txt");
    private GridReading? _grid;
    private DateTimeOffset _nextPoll = DateTimeOffset.MinValue;
    private ControlMode? _lastMode;
    private volatile bool _recompute;
    private bool _appInControl;
    private IReadOnlyList<EvSlot> _evSlots = [];
    private DateTimeOffset? _evFetchedAt;
    private DateTimeOffset _nextEvPoll = DateTimeOffset.MinValue;
    private string? _evError;
    private DateTimeOffset _rateLimitedUntil = DateTimeOffset.MinValue;
    private readonly List<(DateTimeOffset At, int OutputW)> _outputLog = [];
    private IReadOnlyList<OctopusEvent> _events = [];
    private DateTimeOffset? _eventsFetchedAt;
    private DateTimeOffset _nextEventsPoll = DateTimeOffset.MinValue;
    private string? _eventsError;

    // How long a good slot list is trusted after fetches start failing.
    private static readonly TimeSpan EvSlotsTrustedFor = TimeSpan.FromMinutes(15);

    // Events are booked well ahead, so a list is trusted for longer.
    private static readonly TimeSpan EventsTrustedFor = TimeSpan.FromHours(1);

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if (OperatingSystem.IsWindows()) SystemEvents.PowerModeChanged += OnPowerModeChanged;
        runtime.Changed += OnRuntimeChanged;

        try { battery.LogCapabilities(); }
        catch (Exception ex) { logger.LogWarning(ex, "Could not read battery capabilities"); }

        if (runtime.Current.Mode != ControlMode.Live && File.Exists(_handBackFile))
            HandBack("previous run ended without handing back");

        while (!ct.IsCancellationRequested)
        {
            var opts = runtime.Apply(options.CurrentValue);
            AnnounceMode(opts);

            if (DateTimeOffset.UtcNow >= _nextPoll)
            {
                await CycleAsync(opts, fetchOctopus: true, ct);
            }
            else if (_recompute)
            {
                _recompute = false;
                await CycleAsync(opts, fetchOctopus: false, ct);
            }
            else if (opts.Mode == ControlMode.Live && !_appInControl && !opts.CheapRate.AppOwns(DateTimeOffset.Now))
            {
                GuardMode();
            }

            await _wake.WaitAsync(TimeSpan.FromSeconds(Math.Max(1, opts.ModeGuardSeconds)), ct);
        }
    }

    private async Task CycleAsync(ControllerOptions opts, bool fetchOctopus, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var errors = new List<string>();

        if (fetchOctopus)
        {
            _nextPoll = now.AddSeconds(opts.PollSeconds);
            await FetchGridAsync(now, errors, ct);
        }

        BatteryState? state = null;
        try
        {
            state = battery.Read();
            LogOutput(DateTimeOffset.UtcNow, state);
        }
        catch (Exception ex)
        {
            battery.Reset();
            errors.Add($"modbus read: {ex.Message}");
            logger.LogWarning(ex, "Battery read failed");
        }

        // Slot checks have their own 5/minute limit, separate from the meter's 125/hour.
        if (fetchOctopus && opts.EvSlot.Enabled && now >= _nextEvPoll)
            await RefreshEvSlotsAsync(opts, now, ct);

        var inEvSlot = InEvSlot(opts, now);

        if (fetchOctopus && opts.Events.Enabled && now >= _nextEventsPoll)
            await RefreshEventsAsync(opts, now, ct);

        var octopusEvent = ActiveEvent(opts, now);

        // A Power Up or EV slot charges at a fixed rate, so it carries on without meter readings.
        var appReason = AppReason(opts, now, fixedCharge: (inEvSlot && octopusEvent != OctopusEventKind.PowerDown) || octopusEvent == OctopusEventKind.PowerUp);
        var decision = appReason is not null
            ? new Decision(0, $"{appReason}, app schedule in charge")
            : ControlLaw.Decide(opts, now, _grid, state, inEvSlot, octopusEvent, OutputAt(_grid?.ReadAt));
        var written = false;

        if (opts.Mode == ControlMode.Live && appReason is not null)
        {
            // Retried every cycle until it succeeds, so a Wi-Fi blip isn't fatal.
            if (!_appInControl) _appInControl = HandBack(appReason);
        }
        else if (opts.Mode == ControlMode.Live)
        {
            if (_appInControl)
            {
                logger.LogWarning("Taking control back from the app");
                _appInControl = false;
            }

            try
            {
                if (state is not null && state.Mode != SolixModbus.ThirdPartyMode)
                {
                    RememberMode(state.Mode);
                    battery.SetMode(SolixModbus.ThirdPartyMode);
                }
                battery.SetSetpoint(decision.TargetW);
                written = true;
            }
            catch (Exception ex)
            {
                battery.Reset();
                errors.Add($"modbus write: {ex.Message}");
                logger.LogWarning(ex, "Battery write failed");
            }
        }

        var error = errors.Count > 0 ? string.Join("; ", errors) : null;
        log.Write(now, opts.Mode, _grid, state, decision, written, error);
        runtime.Latest = new Snapshot(now, opts.Mode, opts.MaxW, _grid, state, decision, written, error);

        logger.LogInformation(
            "{Mode} grid {Demand} W (age {Age}s) | AC out {Ac} W, SOC {Soc}%, mode {BMode} | target {Target} W {Written} ({Reason})",
            opts.Mode,
            _grid?.DemandW.ToString() ?? "?",
            _grid is null ? "?" : ((int)(now - _grid.ReadAt).TotalSeconds).ToString(),
            state?.AcOutputW.ToString() ?? "?",
            state?.SocPercent.ToString() ?? "?",
            state?.Mode.ToString() ?? "?",
            decision.TargetW,
            written ? "written" : "not written",
            decision.Reason);
    }

    private void LogOutput(DateTimeOffset at, BatteryState state)
    {
        var signed = state.BatteryPowerW < 0 ? -Math.Abs(state.AcOutputW) : Math.Abs(state.AcOutputW);
        _outputLog.Add((at, signed));
        _outputLog.RemoveAll(x => at - x.At > TimeSpan.FromMinutes(10));
    }

    // Battery output when the grid reading was taken; null if the reading is older than the log.
    private int? OutputAt(DateTimeOffset? readAt)
    {
        if (readAt is null) return null;
        var before = _outputLog.LastOrDefault(x => x.At <= readAt.Value);
        return before == default ? null : before.OutputW;
    }

    private async Task FetchGridAsync(DateTimeOffset now, List<string> errors, CancellationToken ct)
    {
        try
        {
            _grid = await octopus.GetLatestAsync(ct) ?? _grid;
        }
        catch (OctopusRateLimitedException ex)
        {
            _nextPoll = now.AddMinutes(5);
            _rateLimitedUntil = _nextPoll;
            errors.Add("octopus rate limited");
            logger.LogWarning("Octopus rate limit hit, backing off 5 minutes: {Message}", ex.Message);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            errors.Add($"octopus: {ex.Message}");
            logger.LogWarning(ex, "Octopus read failed");
        }
    }

    // When the app's own schedule should run instead of the controller; null means the controller is in charge.
    private string? AppReason(ControllerOptions opts, DateTimeOffset now, bool fixedCharge)
    {
        if (opts.CheapRate.AppOwns(now))
            return $"cheap-rate window {opts.CheapRate.Start:HH\\:mm}-{opts.CheapRate.End:HH\\:mm}";

        if (fixedCharge) return null;

        if (now < _rateLimitedUntil) return "Octopus rate limited";

        if (_grid is null) return "no grid reading yet";
        var age = (now - _grid.ReadAt).TotalSeconds;
        return age > opts.StaleAfterSeconds ? $"grid reading stale ({age:F0}s)" : null;
    }

    private async Task RefreshEventsAsync(ControllerOptions opts, DateTimeOffset now, CancellationToken ct)
    {
        _nextEventsPoll = now.AddMinutes(Math.Max(1, opts.Events.PollMinutes));
        try
        {
            _events = await octopus.GetJoinedEventsAsync(ct);
            _eventsFetchedAt = now;
            _eventsError = null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _eventsError = ex.Message;
            logger.LogWarning("Octopus event check failed: {Message}", ex.Message);
        }
    }

    // As with EV slots, a failed fetch must never invent an event, so an old list stops being trusted.
    private OctopusEventKind? ActiveEvent(ControllerOptions opts, DateTimeOffset now)
    {
        var trusted = _eventsFetchedAt is { } at && now - at <= EventsTrustedFor;
        var active = opts.Events.Enabled && trusted
            // PowerDown sorts last, so it wins if events ever overlap.
            ? _events.Where(e => e.Start <= now && now < e.End).Select(e => (OctopusEventKind?)e.Kind).OrderBy(k => k).LastOrDefault()
            : null;
        runtime.Events = new EventStatus(
            _events.Where(e => e.End > now).OrderBy(e => e.Start).ToList(),
            _eventsFetchedAt,
            active,
            !opts.Events.Enabled ? "disabled" : _eventsError ?? (trusted || _eventsFetchedAt is null ? null : "event list out of date"));
        return active;
    }

    private async Task RefreshEvSlotsAsync(ControllerOptions opts, DateTimeOffset now, CancellationToken ct)
    {
        _nextEvPoll = now.AddMinutes(Math.Max(1, opts.EvSlot.PollMinutes));
        try
        {
            _evSlots = await octopus.GetEvSlotsAsync(ct);
            _evFetchedAt = now;
            _evError = null;
        }
        catch (OctopusRateLimitedException)
        {
            _nextEvPoll = now.AddMinutes(15);
            _evError = "rate limited";
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _evError = ex.Message;
            logger.LogWarning("EV slot check failed: {Message}", ex.Message);
        }
    }

    // A failed fetch must never count as a cheap slot, so an old list stops being trusted after a while.
    private bool InEvSlot(ControllerOptions opts, DateTimeOffset now)
    {
        var trusted = _evFetchedAt is { } at && now - at <= EvSlotsTrustedFor;
        var inSlot = opts.EvSlot.Enabled && trusted && _evSlots.Any(s => s.Start <= now && now < s.End);
        runtime.Ev = new EvStatus(
            _evSlots.Where(s => s.End > now).OrderBy(s => s.Start).ToList(),
            _evFetchedAt,
            inSlot,
            !opts.EvSlot.Enabled ? "disabled" : _evError ?? (trusted || _evFetchedAt is null ? null : "slot list out of date"));
        return inSlot;
    }

    private void GuardMode()
    {
        try
        {
            var state = battery.Read();
            LogOutput(DateTimeOffset.UtcNow, state);
            if (state.Mode == SolixModbus.ThirdPartyMode) return;
            logger.LogWarning("Battery left third-party mode (now {Mode}), restoring it", state.Mode);
            RememberMode(state.Mode);
            battery.SetMode(SolixModbus.ThirdPartyMode);
        }
        catch (Exception ex)
        {
            battery.Reset();
            logger.LogWarning(ex, "Mode guard failed");
        }
    }

    private void AnnounceMode(ControllerOptions opts)
    {
        if (_lastMode == opts.Mode) return;
        if (_lastMode == ControlMode.Live) HandBack("switched to Observe");
        _lastMode = opts.Mode;
        _appInControl = false;
        logger.LogWarning("Mode is {Mode}, cap {MaxW} W. {Detail}", opts.Mode, opts.MaxW,
            opts.Mode == ControlMode.Live ? "Writing to the battery." : "Logging only, nothing is written.");
    }

    // Dashboard changes recompute from the cached grid reading so they don't cost an Octopus call.
    private void OnRuntimeChanged()
    {
        _recompute = true;
        _wake.Release();
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Suspend) HandBack("laptop suspending");
        if (e.Mode == PowerModes.Resume) _nextPoll = DateTimeOffset.MinValue;
    }

    // Saved to disk so a crashed run can still be handed back on the next start.
    private void RememberMode(int mode)
    {
        if (mode == SolixModbus.ThirdPartyMode || File.Exists(_handBackFile)) return;
        File.WriteAllText(_handBackFile, mode.ToString());
        logger.LogInformation("Saved app mode {Mode} to hand back to later", mode);
    }

    /// <summary>Writes 0 W and puts the battery back in the mode it was in before going Live, so the app schedule resumes.</summary>
    private bool HandBack(string why)
    {
        if (_lastMode != ControlMode.Live && !File.Exists(_handBackFile)) return true;

        var mode = File.Exists(_handBackFile) && ushort.TryParse(File.ReadAllText(_handBackFile).Trim(), out var saved)
            ? saved
            : SolixModbus.CustomMode;
        try
        {
            battery.SetSetpoint(0);
            battery.SetMode(mode);
            File.Delete(_handBackFile);
            logger.LogWarning("Handed the battery back to the app in mode {Mode} ({Why})", mode, why);
            return true;
        }
        catch (Exception ex)
        {
            battery.Reset();
            logger.LogError(ex,
                "Could not hand the battery back ({Why}). It stays in third-party mode at its last setpoint; set the mode in the Anker app", why);
            return false;
        }
    }

    public override async Task StopAsync(CancellationToken ct)
    {
        if (OperatingSystem.IsWindows()) SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        runtime.Changed -= OnRuntimeChanged;
        await base.StopAsync(ct);
        HandBack("shutting down");
    }
}
