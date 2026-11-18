using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Solix.Core;
using Solix.Worker;
using Solix.Worker.Battery;
using Solix.Worker.Octopus;

var builder = WebApplication.CreateBuilder(args);
builder.Configuration.AddUserSecrets<Program>(optional: true);
builder.Services.AddSystemd();

// AddSystemd only registers the notifier under systemd; elsewhere this one reports IsEnabled false.
builder.Services.TryAddSingleton<Microsoft.Extensions.Hosting.Systemd.ISystemdNotifier, Microsoft.Extensions.Hosting.Systemd.SystemdNotifier>();
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));

builder.Services.Configure<ControllerOptions>(builder.Configuration.GetSection("Controller"));
builder.Services.Configure<OctopusOptions>(builder.Configuration.GetSection("Octopus"));
builder.Services.Configure<BatteryOptions>(builder.Configuration.GetSection("Battery"));

// One shared client so there's one cached token: Octopus allows only 3 token requests a minute.
builder.Services.AddSingleton(sp =>
{
    var http = new HttpClient(new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(15) })
    {
        Timeout = TimeSpan.FromSeconds(20),
    };

    // The backend API returns 403 to requests without a User-Agent.
    http.DefaultRequestHeaders.UserAgent.ParseAdd("SolixLoadFollower/1.0");
    return new OctopusClient(
        http,
        sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<OctopusOptions>>(),
        sp.GetRequiredService<SampleLog>(),
        sp.GetRequiredService<ILogger<OctopusClient>>());
});
builder.Services.AddSingleton<SolixModbus>();
builder.Services.AddSingleton<SampleLog>();
builder.Services.AddSingleton<RuntimeControl>();
builder.Services.AddHostedService<ControlWorker>();
builder.Services.AddHostedService<UsageWorker>();

var app = builder.Build();
app.UseDefaultFiles();
app.UseStaticFiles();

app.MapGet("/api/status", (RuntimeControl runtime, Microsoft.Extensions.Options.IOptionsMonitor<ControllerOptions> options) =>
{
    var s = runtime.Current;
    var applied = runtime.Apply(options.CurrentValue);
    return new
    {
        s.Mode, s.MaxW, s.SolarCharge, s.StartExportW, s.MaxChargeW,
        importBufferW = applied.ImportBufferW,
        plentyExportW = applied.Spare.ExportTargetW,
        solarExportW = applied.SolarCharge.TargetExportW,
        spareMarginWh = applied.Spare.MarginWh,
        pollSeconds = options.CurrentValue.PollSeconds,
        hardMaxW = runtime.HardMaxW,
        now = DateTimeOffset.UtcNow,
        latest = runtime.Latest,
        ev = runtime.Ev,
        events = runtime.Events,
        usage = runtime.Usage,
        spare = runtime.Spare,
        day = runtime.Day,
        cheapRateEnd = options.CurrentValue.CheapRate.End.ToString("HH:mm"),
        canShutDown = OperatingSystem.IsLinux(),
    };
});

app.MapGet("/api/history", (SampleLog log, int? minutes) =>
    log.Since(DateTimeOffset.UtcNow.AddMinutes(-Math.Clamp(minutes ?? 180, 5, 60 * 24 * 7))));

app.MapPost("/api/control", (RuntimeControl runtime, RuntimeSettings request) =>
{
    runtime.Set(request);
    return Results.Ok(runtime.Current);
});

// Powers the Pi off. systemd stops this service first, which hands the battery back to the app.
app.MapPost("/api/shutdown", (ILogger<Program> logger) =>
{
    if (!OperatingSystem.IsLinux()) return Results.BadRequest(new { error = "Shutting down is only available on the Pi" });

    logger.LogWarning("Shutdown requested from the dashboard");
    using var sudo = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("sudo", ["-n", "/usr/bin/systemctl", "poweroff"])
    {
        RedirectStandardError = true,
    })!;
    var error = sudo.StandardError.ReadToEnd();
    sudo.WaitForExit(10_000);
    if (sudo.ExitCode != 0)
    {
        logger.LogError("Shutdown failed: {Error}", error);
        return Results.Json(new { error = $"Couldn't shut down: {error.Trim()}" }, statusCode: 500);
    }
    return Results.Ok();
});

app.Run();
