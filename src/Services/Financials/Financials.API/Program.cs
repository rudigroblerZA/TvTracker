using Financials.API.Apis;
using Financials.API.Data;
using Financials.API.Services;
using TvTracker;
using TvTracker.Persistence;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.AddDefaultAuthentication();

builder.AddNpgsqlDbContext<FinancialsContext>("financialsdb", configureSettings: s => s.ConnectionString = ConnectionPool.Limit(s.ConnectionString));
builder.Services.AddDatabaseInitializer<FinancialsContext>();

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddOptions<FinancialOptions>().Bind(builder.Configuration.GetSection("Financials"))
    .Validate(o => o.PaymentFeePercent is >= 0 and < 100 && o.PaymentFeeFixed >= 0, "Financials fee settings must be a percent from 0 to under 100 and a fixed fee of 0 or more.")
    .ValidateOnStart();

// Income is the other services' to report (IncomeSources). One quick try each, without the default retries: a
// service that's down should cost the statement a few seconds and leave a gap the page names, not hold it for the
// half minute the retries would take.
foreach (var (client, address) in new[] { (IncomeSources.Identity, "https+http://identity-api/"), (IncomeSources.Catalog, "https+http://catalog-api/") })
{
#pragma warning disable EXTEXP0001 // RemoveAllResilienceHandlers is experimental; Catalog.API's ops clients use it the same way.
    builder.Services.AddHttpClient(client, http =>
        {
            http.BaseAddress = new Uri(address);
            http.Timeout = TimeSpan.FromSeconds(5);
        })
        .RemoveAllResilienceHandlers();
#pragma warning restore EXTEXP0001
}
builder.Services.AddSingleton<IncomeSources>();

builder.Services.AddProblemDetails();
builder.Services.AddOpenApi();

var app = builder.Build();

app.MapDefaultEndpoints();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseDefaultAuthentication();

app.MapGet("/api/v1/status", () => new { Service = "financials-api", Status = "ok" });
app.MapFinancialsApi();

app.Run();
