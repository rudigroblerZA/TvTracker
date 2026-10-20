using Catalog.API.Apis;
using Catalog.API.Services;
using Catalog.Data;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

// Catalog.API owns the catalogdb schema; the sync worker waits for this service to be healthy.
builder.AddNpgsqlDbContext<CatalogContext>("catalogdb");
builder.Services.AddDatabaseInitializer<CatalogContext>();

builder.AddRedisDistributedCache("redis");
builder.Services.AddHybridCache();

builder.Services.AddSingleton(TimeProvider.System);

// Search asks the sync worker to import provider matches first; the worker owns provider calls and writes.
// No retries: when the worker is down, search should answer from the catalog straight away.
#pragma warning disable EXTEXP0001 // RemoveAllResilienceHandlers is experimental; it undoes the ServiceDefaults handler for this client.
builder.Services.AddHttpClient<CatalogImportClient>(http => http.BaseAddress = new Uri("https+http://catalog-sync/"))
    .RemoveAllResilienceHandlers();
#pragma warning restore EXTEXP0001
builder.Services.AddProblemDetails();
builder.Services.AddOpenApi();

var app = builder.Build();

app.MapDefaultEndpoints();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.MapGet("/api/v1/status", () => new { Service = "catalog-api", Status = "ok" });
app.MapCatalogApi();

app.Run();
