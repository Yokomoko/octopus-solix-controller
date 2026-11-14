using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using Solix.Core;

namespace Solix.Worker;

public sealed record Snapshot(
    DateTimeOffset At,
    ControlMode Mode,
    int MaxW,
    GridReading? Grid,
    BatteryState? Battery,
    Decision Decision,
    bool Written,
    string? Error);

/// <summary>Latest view of Intelligent Octopus smart-charge slots for the dashboard.</summary>
public sealed record EvStatus(IReadOnlyList<Octopus.EvSlot> Slots, DateTimeOffset? CheckedAt, bool InSlot, string? Error);

/// <summary>Latest view of joined Octopus events for the dashboard.</summary>
public sealed record EventStatus(IReadOnlyList<Octopus.OctopusEvent> Upcoming, DateTimeOffset? CheckedAt, OctopusEventKind? Active, string? Error);

/// <summary>Settings changed from the dashboard.</summary>
public sealed record RuntimeSettings(ControlMode Mode, int MaxW, bool SolarCharge, int StartExportW, int MaxChargeW);

/// <summary>
/// Dashboard settings plus the latest cycle for display. Settings are saved to the data folder so a
/// restart carries on as it was left; without a saved file, config is used.
/// </summary>
public sealed class RuntimeControl
{
    public const int HardMaxW = 800;

    // Charging isn't bound by the 800 W plug-in output cap.
    public const int HardMaxChargeW = 2300;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
        WriteIndented = true,
    };

    private readonly Lock _gate = new();
    private readonly string _file;
    private readonly ILogger<RuntimeControl> _logger;
    private RuntimeSettings _settings;
    private Snapshot? _latest;
    private EvStatus? _ev;
    private EventStatus? _events;
    private UsageStatus? _usage;

    public RuntimeControl(IOptions<ControllerOptions> options, IConfiguration config, ILogger<RuntimeControl> logger)
    {
        _logger = logger;
        _file = Path.Combine(SampleLog.DataDirectory(config), "dashboard-settings.json");
        var o = options.Value;
        var fromConfig = new RuntimeSettings(o.Mode, o.MaxW, o.SolarCharge.Enabled, o.SolarCharge.StartExportW, o.SolarCharge.MaxChargeW);
        _settings = Clamp(Load() ?? fromConfig);
    }

    public event Action? Changed;

    public RuntimeSettings Current
    {
        get { lock (_gate) return _settings; }
    }

    public Snapshot? Latest
    {
        get { lock (_gate) return _latest; }
        set { lock (_gate) _latest = value; }
    }

    public EvStatus? Ev
    {
        get { lock (_gate) return _ev; }
        set { lock (_gate) _ev = value; }
    }

    public EventStatus? Events
    {
        get { lock (_gate) return _events; }
        set { lock (_gate) _events = value; }
    }

    public UsageStatus? Usage
    {
        get { lock (_gate) return _usage; }
        set { lock (_gate) _usage = value; }
    }

    public ControllerOptions Apply(ControllerOptions configured)
    {
        var s = Current;
        var copy = configured.With(s.Mode, s.MaxW);
        var solar = configured.SolarCharge;
        copy.SolarCharge = new SolarChargeOptions
        {
            Enabled = s.SolarCharge,
            StartExportW = s.StartExportW,
            TargetExportW = solar.TargetExportW,
            MaxChargeW = s.MaxChargeW,
            SocCeilingPercent = solar.SocCeilingPercent,
        };
        return copy;
    }

    public void Set(RuntimeSettings settings)
    {
        RuntimeSettings saved;
        lock (_gate) saved = _settings = Clamp(settings);
        Save(saved);
        Changed?.Invoke();
    }

    private RuntimeSettings? Load()
    {
        try
        {
            if (!File.Exists(_file)) return null;
            var loaded = JsonSerializer.Deserialize<RuntimeSettings>(File.ReadAllText(_file), Json);
            _logger.LogInformation("Restored dashboard settings: {Settings}", loaded);
            return loaded;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Couldn't read saved dashboard settings, using config");
            return null;
        }
    }

    private void Save(RuntimeSettings settings)
    {
        try
        {
            File.WriteAllText(_file, JsonSerializer.Serialize(settings, Json));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Couldn't save dashboard settings; they'll reset on restart");
        }
    }

    private static RuntimeSettings Clamp(RuntimeSettings s) =>
        s with
        {
            MaxW = Math.Clamp(s.MaxW, 0, HardMaxW),
            StartExportW = Math.Clamp(s.StartExportW, 0, 500),
            MaxChargeW = Math.Clamp(s.MaxChargeW, 0, HardMaxChargeW),
        };
}
