using ResMan.Core;
using ResMan.Data;
using ResMan.Service;

// Runs as a console app during development (`dotnet run`) and as the "ResMan" Windows Service when installed.
var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddWindowsService(o => o.ServiceName = "ResMan");

var db = new ResManDb(ResManPaths.DbPath);
builder.Services.AddSingleton(db);
builder.Services.AddSingleton<TelemetryStore>();
builder.Services.AddSingleton<HealthStore>();
builder.Services.AddSingleton<RuleStore>();
builder.Services.AddSingleton<ChangeStore>();
builder.Services.AddSingleton<DriveHealthStore>();
builder.Services.AddSingleton<FixStore>();
builder.Services.AddSingleton<ReplayStore>();
builder.Services.AddSingleton<ReplayBuffer>();
builder.Services.AddSingleton<ServiceState>();
builder.Services.AddSingleton<TelemetrySampler>();
builder.Services.AddSingleton<ChangeCollector>();
builder.Services.AddSingleton<DriveHealthCollector>();

builder.Services.AddHostedService<TelemetryCollector>();
builder.Services.AddHostedService<HealthEventCollector>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<ChangeCollector>());
builder.Services.AddHostedService(sp => sp.GetRequiredService<DriveHealthCollector>());
builder.Services.AddHostedService<NetworkMonitor>();
builder.Services.AddHostedService<RulesEngine>(); // stub: dry-run only, deprioritized for v1
builder.Services.AddHostedService<PipeServer>();

var host = builder.Build();
var state = host.Services.GetRequiredService<ServiceState>();
host.Services.GetRequiredService<ILogger<ServiceState>>()
    .LogInformation("ResMan service starting. Database: {Db}. Elevated: {Elevated}", db.Path, state.Elevated);
host.Run();
