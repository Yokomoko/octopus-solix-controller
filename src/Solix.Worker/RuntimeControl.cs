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

/// <summary>Settings changed from the dashboard. The advanced ones fall back to config when not set.</summary>
public sealed record RuntimeSettings(
    ControlMode Mode, int MaxW, bool SolarCharge, int StartExportW, int MaxChargeW,
    int? ImportBufferW = null, int? PlentyExportW = null, int? SolarExportW = null, int? SpareMarginWh = null);

/// <summary>
/// Dashboard settings plus the latest cycle for display. Settings are saved to the data folder so a
/// restart carries on as it was left; without a saved file, config is used.
/// </summary>
public sealed class RuntimeControl
{
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
    private SpareEstimate? _spare;
    private DayProfile? _day;
    private readonly int _hardMaxW;

    public RuntimeControl(IOptions<ControllerOptions> options, IConfiguration config, ILogger<RuntimeControl> logger)
    {
        _logger = logger;
        _file = Path.Combine(SampleLog.DataDirectory(config), "dashboard-settings.json");
        var o = options.Value;
        _hardMaxW = Math.Max(0, o.HardMaxW);
        var fromConfig = new RuntimeSettings(o.Mode, o.MaxW, o.SolarCharge.Enabled, o.SolarCharge.StartExportW, o.SolarCharge.MaxChargeW);
        _settings = Clamp(Load() ?? fromConfig);
    }

    public event Action? Changed;

    /// <summary>Upper limit for the discharge cap, from config; read at start-up.</summary>
    public int HardMaxW => _hardMaxW;

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

    public SpareEstimate? Spare
    {
        get { lock (_gate) return _spare; }
        set { lock (_gate) _spare = value; }
    }

    public DayProfile? Day
    {
        get { lock (_gate) return _day; }
        set { lock (_gate) _day = value; }
    }

    public ControllerOptions Apply(ControllerOptions configured)
    {
        var s = Current;
        var copy = configured.With(s.Mode, s.MaxW);
        copy.ImportBufferW = s.ImportBufferW ?? configured.ImportBufferW;
        var spare = configured.Spare;
        copy.Spare = new SpareOptions
        {
            Enabled = spare.Enabled,
            ExportTargetW = s.PlentyExportW ?? spare.ExportTargetW,
            FloorPercent = spare.FloorPercent,
            MarginWh = s.SpareMarginWh ?? spare.MarginWh,
            HistoryDays = spare.HistoryDays,
            CarW = spare.CarW,
        };
        var solar = configured.SolarCharge;
        copy.SolarCharge = new SolarChargeOptions
        {
            Enabled = s.SolarCharge,
            StartExportW = s.StartExportW,
            TargetExportW = s.SolarExportW ?? solar.TargetExportW,
            MaxChargeW = s.MaxChargeW,
            MinChargeW = solar.MinChargeW,
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

    private RuntimeSettings Clamp(RuntimeSettings s) =>
        s with
        {
            MaxW = Math.Clamp(s.MaxW, 0, _hardMaxW),
            StartExportW = Math.Clamp(s.StartExportW, 0, 500),
            MaxChargeW = Math.Clamp(s.MaxChargeW, 0, HardMaxChargeW),
            ImportBufferW = s.ImportBufferW is { } b ? Math.Clamp(b, 0, 100) : null,
            PlentyExportW = s.PlentyExportW is { } p ? Math.Clamp(p, 0, 100) : null,
            SolarExportW = s.SolarExportW is { } x ? Math.Clamp(x, 0, 100) : null,
            SpareMarginWh = s.SpareMarginWh is { } m ? Math.Clamp(m, 0, 2000) : null,
        };
}
