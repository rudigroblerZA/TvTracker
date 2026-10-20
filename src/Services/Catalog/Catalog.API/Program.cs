using Catalog.API.Apis;
using Catalog.Data;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

// Catalog.API owns the catalogdb schema; the sync worker waits for this service to be healthy.
builder.AddNpgsqlDbContext<CatalogContext>("catalogdb");
builder.Services.AddDatabaseInitializer<CatalogContext>();

builder.AddRedisDistributedCache("redis");
builder.Services.AddHybridCache();

builder.Services.AddSingleton(TimeProvider.System);
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
