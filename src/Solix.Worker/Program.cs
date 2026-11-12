using System.Text.Json.Serialization;
using Solix.Core;
using Solix.Worker;
using Solix.Worker.Battery;
using Solix.Worker.Octopus;

var builder = WebApplication.CreateBuilder(args);
builder.Configuration.AddUserSecrets<Program>(optional: true);
builder.Services.AddSystemd();
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));

builder.Services.Configure<ControllerOptions>(builder.Configuration.GetSection("Controller"));
builder.Services.Configure<OctopusOptions>(builder.Configuration.GetSection("Octopus"));
builder.Services.Configure<BatteryOptions>(builder.Configuration.GetSection("Battery"));

builder.Services.AddHttpClient<OctopusClient>(c =>
{
    c.Timeout = TimeSpan.FromSeconds(20);

    // The backend API returns 403 to requests without a User-Agent.
    c.DefaultRequestHeaders.UserAgent.ParseAdd("SolixLoadFollower/1.0");
});
builder.Services.AddSingleton<SolixModbus>();
builder.Services.AddSingleton<SampleLog>();
builder.Services.AddSingleton<RuntimeControl>();
builder.Services.AddHostedService<ControlWorker>();

var app = builder.Build();
app.UseDefaultFiles();
app.UseStaticFiles();

app.MapGet("/api/status", (RuntimeControl runtime, Microsoft.Extensions.Options.IOptionsMonitor<ControllerOptions> options) =>
{
    var s = runtime.Current;
    return new
    {
        s.Mode, s.MaxW, s.SolarCharge, s.StartExportW, s.MaxChargeW,
        pollSeconds = options.CurrentValue.PollSeconds,
        hardMaxW = RuntimeControl.HardMaxW,
        now = DateTimeOffset.UtcNow,
        latest = runtime.Latest,
        ev = runtime.Ev,
        events = runtime.Events,
    };
});

app.MapGet("/api/history", (SampleLog log, int? minutes) =>
    log.Since(DateTimeOffset.UtcNow.AddMinutes(-Math.Clamp(minutes ?? 180, 5, 60 * 24 * 7))));

app.MapPost("/api/control", (RuntimeControl runtime, RuntimeSettings request) =>
{
    runtime.Set(request);
    return Results.Ok(runtime.Current);
});

app.Run();
