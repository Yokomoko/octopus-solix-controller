using Microsoft.Data.Sqlite;
using Solix.Core;

namespace Solix.Worker;

public sealed record HistoryPoint(DateTimeOffset At, int? DemandW, int? AcOutputW, int TargetW, bool Written, int? Soc);

/// <summary>Appends one row per control cycle to a SQLite file and serves recent rows to the dashboard.</summary>
public sealed class SampleLog : IDisposable
{
    private readonly Lock _gate = new();
    private readonly SqliteConnection _db;

    public static string DataDirectory(IConfiguration config)
    {
        var dir = config["DataDirectory"] is { Length: > 0 } d ? d : Path.Combine(AppContext.BaseDirectory, "data");
        Directory.CreateDirectory(dir);
        return dir;
    }

    public SampleLog(IConfiguration config)
    {
        var dir = DataDirectory(config);
        _db = new SqliteConnection($"Data Source={Path.Combine(dir, "samples.db")}");
        _db.Open();
        using var cmd = _db.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS samples (
                at TEXT NOT NULL,
                mode TEXT NOT NULL,
                grid_read_at TEXT,
                grid_age_s REAL,
                demand_w INTEGER,
                ac_output_w INTEGER,
                battery_w INTEGER,
                soc INTEGER,
                battery_mode INTEGER,
                target_w INTEGER NOT NULL,
                written INTEGER NOT NULL,
                reason TEXT NOT NULL,
                error TEXT
            );
            CREATE INDEX IF NOT EXISTS ix_samples_at ON samples(at);
            """;
        cmd.ExecuteNonQuery();
    }

    public void Write(DateTimeOffset at, ControlMode mode, GridReading? grid, BatteryState? battery, Decision decision, bool written, string? error)
    {
        lock (_gate)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = """
                INSERT INTO samples VALUES ($at,$mode,$readAt,$age,$demand,$ac,$bat,$soc,$bmode,$target,$written,$reason,$error)
                """;
            cmd.Parameters.AddWithValue("$at", at.UtcDateTime.ToString("O"));
            cmd.Parameters.AddWithValue("$mode", mode.ToString());
            cmd.Parameters.AddWithValue("$readAt", (object?)grid?.ReadAt.UtcDateTime.ToString("O") ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$age", grid is null ? DBNull.Value : (at - grid.ReadAt).TotalSeconds);
            cmd.Parameters.AddWithValue("$demand", (object?)grid?.DemandW ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$ac", (object?)battery?.AcOutputW ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$bat", (object?)battery?.BatteryPowerW ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$soc", (object?)battery?.SocPercent ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$bmode", (object?)battery?.Mode ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$target", decision.TargetW);
            cmd.Parameters.AddWithValue("$written", written ? 1 : 0);
            cmd.Parameters.AddWithValue("$reason", decision.Reason);
            cmd.Parameters.AddWithValue("$error", (object?)error ?? DBNull.Value);
            cmd.ExecuteNonQuery();
        }
    }

    public IReadOnlyList<HistoryPoint> Since(DateTimeOffset from)
    {
        lock (_gate)
        {
            using var cmd = _db.CreateCommand();
            // The AC register is unsigned; the battery register gives the sign, so charging plots below zero.
            cmd.CommandText = """
                SELECT at, demand_w,
                       CASE WHEN battery_w < 0 THEN -ABS(ac_output_w) ELSE ABS(ac_output_w) END,
                       target_w, written, soc
                FROM samples WHERE at >= $from ORDER BY at
                """;
            cmd.Parameters.AddWithValue("$from", from.UtcDateTime.ToString("O"));
            using var reader = cmd.ExecuteReader();
            var rows = new List<HistoryPoint>();
            while (reader.Read())
            {
                rows.Add(new HistoryPoint(
                    DateTimeOffset.Parse(reader.GetString(0)),
                    reader.IsDBNull(1) ? null : reader.GetInt32(1),
                    reader.IsDBNull(2) ? null : reader.GetInt32(2),
                    reader.GetInt32(3),
                    reader.GetInt32(4) == 1,
                    reader.IsDBNull(5) ? null : reader.GetInt32(5)));
            }
            return rows;
        }
    }

    /// <summary>Logged battery output and grid readings since <paramref name="from"/>, for working out what the battery was worth.</summary>
    public IReadOnlyList<BatterySample> BatterySamplesSince(DateTimeOffset from)
    {
        lock (_gate)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = """
                SELECT at, demand_w, ac_output_w, battery_w FROM samples
                WHERE at >= $from AND demand_w IS NOT NULL AND ac_output_w IS NOT NULL AND battery_w IS NOT NULL
                ORDER BY at
                """;
            cmd.Parameters.AddWithValue("$from", from.UtcDateTime.ToString("O"));
            using var reader = cmd.ExecuteReader();
            var rows = new List<BatterySample>();
            while (reader.Read())
            {
                var ac = Math.Abs(reader.GetInt32(2));
                var signed = reader.GetInt32(3) < 0 ? -ac : ac;
                rows.Add(new BatterySample(DateTimeOffset.Parse(reader.GetString(0)), signed, reader.GetInt32(1)));
            }
            return rows;
        }
    }

    /// <summary>Logged battery levels since <paramref name="from"/>, oldest first.</summary>
    public IReadOnlyList<(DateTimeOffset At, int Soc)> SocSince(DateTimeOffset from)
    {
        lock (_gate)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "SELECT at, soc FROM samples WHERE at >= $from AND soc IS NOT NULL ORDER BY at";
            cmd.Parameters.AddWithValue("$from", from.UtcDateTime.ToString("O"));
            using var reader = cmd.ExecuteReader();
            var rows = new List<(DateTimeOffset, int)>();
            while (reader.Read()) rows.Add((DateTimeOffset.Parse(reader.GetString(0)), reader.GetInt32(1)));
            return rows;
        }
    }

    public void Dispose() => _db.Dispose();
}
