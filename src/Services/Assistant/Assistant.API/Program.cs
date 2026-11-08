using Assistant.API.Alerts;
using Assistant.API.Apis;
using Assistant.API.Services;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.AddDefaultAuthentication();
builder.AddFeatureFlags();

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<Toolbox>();
builder.Services.AddSingleton<AnswerLoop>();
builder.Services.AddSingleton<AskQueue>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<AskQueue>());
builder.Services.AddAlert<AskQueueAlert>();

builder.Services.AddProblemDetails();

var app = builder.Build();

app.MapDefaultEndpoints();

app.UseDefaultAuthentication();

app.MapGet("/api/v1/status", () => new { Service = "assistant-api", Status = "ok" });
app.MapAskApi();

app.Run();
