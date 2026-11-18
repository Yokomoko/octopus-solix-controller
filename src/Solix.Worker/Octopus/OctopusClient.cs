using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using Solix.Core;

namespace Solix.Worker.Octopus;

public sealed class OctopusOptions
{
    public string ApiKey { get; set; } = "";
    public string AccountNumber { get; set; } = "";

    /// <summary>Home Mini device ID. Looked up from the account when left blank.</summary>
    public string? DeviceId { get; set; }

    /// <summary>Intelligent Octopus (car or charger) device ID. Looked up from the account when left blank.</summary>
    public string? EvDeviceId { get; set; }
}

public sealed record EvSlot(DateTimeOffset Start, DateTimeOffset End, string? Type);

public sealed record OctopusEvent(DateTimeOffset Start, DateTimeOffset End, OctopusEventKind Kind, string Type);

public sealed class OctopusRateLimitedException(string message) : Exception(message);

/// <summary>Reads live Home Mini telemetry from the Octopus Kraken GraphQL API.</summary>
public sealed class OctopusClient(HttpClient http, IOptions<OctopusOptions> options, SampleLog log, ILogger<OctopusClient> logger)
{
    private readonly OctopusOptions _options = options.Value;
    private string? _token;
    private DateTimeOffset _tokenExpires;
    private string? _deviceId;
    private string? _evDeviceId;

    /// <summary>Home Mini reads in the last hour, from this run and earlier ones. Octopus allows 125.</summary>
    public IReadOnlyList<DateTimeOffset> MeterCallsInLastHour() => log.MeterCallsSince(DateTimeOffset.UtcNow.AddHours(-1));

    public async Task<GridReading?> GetLatestAsync(CancellationToken ct)
    {
        var deviceId = await GetDeviceIdAsync(ct);
        log.RecordMeterCall(DateTimeOffset.UtcNow);
        var data = await QueryAsync(
            "query($id:String!){ smartMeterTelemetry(deviceId:$id){ readAt demand } }",
            new JsonObject { ["id"] = deviceId }, ct);

        var rows = data["smartMeterTelemetry"]?.AsArray();
        var latest = rows?
            .Where(r => r?["readAt"] is not null && r["demand"] is not null)
            .Select(r => new GridReading(
                DateTimeOffset.Parse(r!["readAt"]!.GetValue<string>(), CultureInfo.InvariantCulture),
                (int)Math.Round(ToDouble(r["demand"]!))))
            .MaxBy(r => r.ReadAt);

        return latest;
    }

    /// <summary>Smart-charge slots Octopus has planned for the car. Throws on failure so callers never mistake it for "no slots".</summary>
    public async Task<IReadOnlyList<EvSlot>> GetEvSlotsAsync(CancellationToken ct)
    {
        var deviceId = await GetEvDeviceIdAsync(ct);
        var data = await QueryAsync(
            "query($id:String!){ flexPlannedDispatches(deviceId:$id){ start end type } }",
            new JsonObject { ["id"] = deviceId }, ct);

        if (data["flexPlannedDispatches"] is not JsonArray rows)
            throw new InvalidOperationException("Octopus returned no dispatch list.");

        return rows
            .Where(r => r?["start"] is not null && r["end"] is not null)
            .Select(r => new EvSlot(
                DateTimeOffset.Parse(r!["start"]!.GetValue<string>(), CultureInfo.InvariantCulture),
                DateTimeOffset.Parse(r["end"]!.GetValue<string>(), CultureInfo.InvariantCulture),
                r["type"]?.ToString()))
            // Boost charges are billed at the normal rate, so they never count as a cheap slot.
            .Where(s => s.Type?.Contains("BOOST", StringComparison.OrdinalIgnoreCase) != true)
            .ToList();
    }

    /// <summary>
    /// Current import tariff, plus the export rate if there's an export agreement. The rates don't carry
    /// times, so peak is the highest unit rate and off-peak the lowest.
    /// </summary>
    public async Task<TariffRates> GetTariffAsync(CancellationToken ct)
    {
        var data = await QueryAsync("""
            query($acc:String!){ account(accountNumber:$acc){ electricityAgreements(active:true){ meterPoint{ direction } tariff {
              ... on StandardTariff { displayName standingCharge unitRate }
              ... on DayNightTariff { displayName standingCharge dayRate nightRate }
              ... on ThreeRateTariff { displayName standingCharge dayRate nightRate offPeakRate }
              ... on HalfHourlyTariff { displayName standingCharge unitRates { value } }
              ... on PrepayTariff { displayName standingCharge unitRate }
            } } } }
            """, new JsonObject { ["acc"] = _options.AccountNumber }, ct);

        var agreements = data["account"]?["electricityAgreements"]?.AsArray().Where(a => a is not null).ToList() ?? [];
        static IEnumerable<double> Rates(JsonNode tariff) =>
            new[] { tariff["unitRate"], tariff["dayRate"], tariff["nightRate"], tariff["offPeakRate"] }
                .Concat(tariff["unitRates"]?.AsArray().Select(r => r?["value"]) ?? [])
                .Where(n => n is not null)
                .Select(n => ToDouble(n!));

        var import = agreements.FirstOrDefault(a => a!["meterPoint"]?["direction"]?.ToString() != "EXPORT")?["tariff"]
            ?? throw new InvalidOperationException("No active import tariff on the account.");
        var importRates = Rates(import).ToList();
        if (importRates.Count == 0) throw new InvalidOperationException("Import tariff has no unit rates.");

        var export = agreements.FirstOrDefault(a => a!["meterPoint"]?["direction"]?.ToString() == "EXPORT")?["tariff"];
        var exportRate = export is null ? 0 : Rates(export).DefaultIfEmpty(0).Max();

        return new TariffRates(
            import["displayName"]?.ToString() ?? "Octopus tariff",
            importRates.Max(),
            importRates.Min(),
            exportRate,
            import["standingCharge"] is { } s ? ToDouble(s) : 0);
    }

    /// <summary>Half-hourly import and export from the Home Mini. Export comes as a running total, so it's differenced here.</summary>
    public async Task<IReadOnlyList<HalfHour>> GetHalfHoursAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken ct)
    {
        var deviceId = await GetDeviceIdAsync(ct);

        // Start half an hour early so the first half-hour has a previous export total to difference against.
        log.RecordMeterCall(DateTimeOffset.UtcNow);
        var data = await QueryAsync(
            "query($id:String!,$s:DateTime!,$e:DateTime!){ smartMeterTelemetry(deviceId:$id, grouping:HALF_HOURLY, start:$s, end:$e){ readAt consumptionDelta export } }",
            new JsonObject { ["id"] = deviceId, ["s"] = from.AddMinutes(-30).ToString("o"), ["e"] = to.ToString("o") }, ct);

        var rows = (data["smartMeterTelemetry"]?.AsArray() ?? [])
            .Where(r => r?["readAt"] is not null)
            .Select(r => (
                At: DateTimeOffset.Parse(r!["readAt"]!.GetValue<string>(), CultureInfo.InvariantCulture),
                Import: r["consumptionDelta"] is { } c ? ToDouble(c) : 0,
                ExportTotal: r["export"] is { } e ? ToDouble(e) : (double?)null))
            .OrderBy(r => r.At)
            .ToList();

        var result = new List<HalfHour>();
        for (var i = 1; i < rows.Count; i++)
        {
            if (rows[i].At < from) continue;
            var exported = rows[i].ExportTotal is { } now && rows[i - 1].ExportTotal is { } before ? Math.Max(0, now - before) : 0;
            result.Add(new HalfHour(rows[i].At, rows[i].Import, exported));
        }
        return result;
    }

    /// <summary>EV smart-charge slots Octopus has already run, which are billed at the off-peak rate.</summary>
    public async Task<IReadOnlyList<TimeRange>> GetCompletedDispatchesAsync(CancellationToken ct)
    {
        var data = await QueryAsync(
            "query($acc:String!){ completedDispatches(accountNumber:$acc){ start end meta { source } } }",
            new JsonObject { ["acc"] = _options.AccountNumber }, ct);

        return (data["completedDispatches"]?.AsArray() ?? [])
            .Where(r => r?["start"] is not null && r["end"] is not null)
            .Where(r => r!["meta"]?["source"]?.ToString()?.Contains("bump", StringComparison.OrdinalIgnoreCase) != true)
            .Select(r => new TimeRange(
                DateTimeOffset.Parse(r!["start"]!.GetValue<string>(), CultureInfo.InvariantCulture),
                DateTimeOffset.Parse(r["end"]!.GetValue<string>(), CultureInfo.InvariantCulture)))
            .ToList();
    }

    /// <summary>Power Ups, Happy Hours and Power Downs the account has joined. Throws on failure so callers never invent an event.</summary>
    public async Task<IReadOnlyList<OctopusEvent>> GetJoinedEventsAsync(CancellationToken ct)
    {
        var data = await PostAsync(
            "query($acc:String!){ savingSessions { account(accountNumber:$acc){ joinedEvents { startAt endAt eventType } } } }",
            new JsonObject { ["acc"] = _options.AccountNumber },
            await GetTokenAsync(ct), ct, BackendApi);

        if (data["savingSessions"]?["account"]?["joinedEvents"] is not JsonArray rows)
            throw new InvalidOperationException("Octopus returned no joined events.");

        return rows
            .Select(r => (Row: r, Kind: r?["eventType"]?.ToString() switch
            {
                "TURN_DOWN" => OctopusEventKind.PowerDown,
                "TURN_UP" or "WEEKEND_HAPPY_HOUR" => (OctopusEventKind?)OctopusEventKind.PowerUp,
                _ => null,
            }))
            .Where(x => x.Kind is not null && x.Row?["startAt"] is not null && x.Row["endAt"] is not null)
            .Select(x => new OctopusEvent(
                DateTimeOffset.Parse(x.Row!["startAt"]!.GetValue<string>(), CultureInfo.InvariantCulture),
                DateTimeOffset.Parse(x.Row["endAt"]!.GetValue<string>(), CultureInfo.InvariantCulture),
                x.Kind!.Value,
                x.Row["eventType"]!.ToString()))
            .ToList();
    }

    private async Task<string> GetEvDeviceIdAsync(CancellationToken ct)
    {
        if (_evDeviceId is not null) return _evDeviceId;
        if (!string.IsNullOrWhiteSpace(_options.EvDeviceId)) return _evDeviceId = _options.EvDeviceId;

        var variables = new JsonObject { ["acc"] = _options.AccountNumber };
        JsonNode data;
        try
        {
            data = await QueryAsync("query($acc:String!){ devices(accountNumber:$acc){ id deviceType } }", variables, ct);
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("deviceType"))
        {
            data = await QueryAsync("query($acc:String!){ devices(accountNumber:$acc){ id } }", variables.DeepClone().AsObject(), ct);
        }

        var devices = data["devices"]?.AsArray().Where(d => d?["id"] is not null).ToList() ?? [];
        var ev = devices.FirstOrDefault(d => d!["deviceType"]?.ToString() is "ELECTRIC_VEHICLES" or "CHARGE_POINTS")
            ?? devices.FirstOrDefault();
        _evDeviceId = ev?["id"]?.GetValue<string>()
            ?? throw new InvalidOperationException("No Intelligent Octopus device found on the account. Set Octopus:EvDeviceId in config.");

        logger.LogInformation("Using Intelligent Octopus device {DeviceId} ({Type})", _evDeviceId, ev!["deviceType"]?.ToString() ?? "unknown type");
        return _evDeviceId;
    }

    private async Task<string> GetDeviceIdAsync(CancellationToken ct)
    {
        if (_deviceId is not null) return _deviceId;
        if (!string.IsNullOrWhiteSpace(_options.DeviceId)) return _deviceId = _options.DeviceId;

        var data = await QueryAsync(
            "query($acc:String!){ account(accountNumber:$acc){ electricityAgreements(active:true){ meterPoint{ meters(includeInactive:false){ smartDevices{ deviceId } } } } } }",
            new JsonObject { ["acc"] = _options.AccountNumber }, ct);

        _deviceId = data.ToJsonString()
            .Split("\"deviceId\":\"", StringSplitOptions.None)
            .Skip(1)
            .Select(s => s[..s.IndexOf('"')])
            .FirstOrDefault()
            ?? throw new InvalidOperationException("No Home Mini found on the account. Set Octopus:DeviceId in config.");

        logger.LogInformation("Using Home Mini device {DeviceId}", _deviceId);
        return _deviceId;
    }

    private async Task<string> GetTokenAsync(CancellationToken ct)
    {
        if (_token is not null && DateTimeOffset.UtcNow < _tokenExpires) return _token;
        if (string.IsNullOrWhiteSpace(_options.ApiKey))
            throw new InvalidOperationException("Octopus:ApiKey isn't set (dotnet user-secrets set Octopus:ApiKey sk_live_...)");

        var data = await PostAsync(
            "mutation($key:String!){ obtainKrakenToken(input:{APIKey:$key}){ token } }",
            new JsonObject { ["key"] = _options.ApiKey }, authorization: null, ct);

        _token = data["obtainKrakenToken"]?["token"]?.GetValue<string>()
            ?? throw new InvalidOperationException("Octopus did not return a token. Check Octopus:ApiKey.");
        _tokenExpires = ReadExpiry(_token).AddMinutes(-5);
        return _token;
    }

    private const string MainApi = "https://api.octopus.energy/v1/graphql/";

    // Power Ups, Happy Hours and Power Downs live on this API, which takes the same token without the "JWT" prefix.
    private const string BackendApi = "https://api.backend.octopus.energy/v1/graphql/";

    private async Task<JsonNode> QueryAsync(string query, JsonObject variables, CancellationToken ct) =>
        await PostAsync(query, variables, $"JWT {await GetTokenAsync(ct)}", ct);

    private async Task<JsonNode> PostAsync(string query, JsonObject variables, string? authorization, CancellationToken ct, string url = MainApi)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = JsonContent.Create(new JsonObject { ["query"] = query, ["variables"] = variables }),
        };
        if (authorization is not null) request.Headers.TryAddWithoutValidation("Authorization", authorization);

        using var response = await http.SendAsync(request, ct);
        if (response.Content.Headers.ContentType?.MediaType?.Contains("json") != true)
            throw new InvalidOperationException($"HTTP {(int)response.StatusCode} from Octopus ({new Uri(url).Host}), not a GraphQL response.");

        var body = await response.Content.ReadFromJsonAsync<JsonNode>(ct)
            ?? throw new InvalidOperationException($"Empty response from Octopus ({(int)response.StatusCode}).");

        if (body["errors"] is JsonArray { Count: > 0 } errors)
        {
            var text = errors.ToJsonString();
            if (text.Contains("KT-CT-1199")) throw new OctopusRateLimitedException(text);
            if (text.Contains("KT-CT-1124") || text.Contains("JWT")) _token = null;
            throw new InvalidOperationException($"Octopus GraphQL error: {text}");
        }

        return body["data"] ?? throw new InvalidOperationException("Octopus response had no data.");
    }

    private static double ToDouble(JsonNode node) => node.GetValueKind() switch
    {
        JsonValueKind.String => double.Parse(node.GetValue<string>(), CultureInfo.InvariantCulture),
        _ => node.GetValue<double>(),
    };

    private static DateTimeOffset ReadExpiry(string jwt)
    {
        try
        {
            var payload = jwt.Split('.')[1].Replace('-', '+').Replace('_', '/');
            payload = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=');
            var json = JsonNode.Parse(Convert.FromBase64String(payload));
            return DateTimeOffset.FromUnixTimeSeconds(json!["exp"]!.GetValue<long>());
        }
        catch (Exception)
        {
            return DateTimeOffset.UtcNow.AddMinutes(55);
        }
    }
}
