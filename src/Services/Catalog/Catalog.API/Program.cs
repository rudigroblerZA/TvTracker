using Catalog.API.Apis;
using Catalog.API.Services;
using Catalog.Data;
using Catalog.Embeddings;
using Microsoft.EntityFrameworkCore;
using Npgsql;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

// Catalog endpoints are public, but a signed-in caller's token identifies them for search rate limiting.
builder.AddDefaultAuthentication();

// Catalog.API owns the catalogdb schema; the sync worker waits for this service to be healthy.
builder.AddCatalogDbContext();
builder.Services.AddDatabaseInitializer<CatalogContext>(async (db, _, ct) =>
{
    // A migration may have just created the pgvector extension; connections opened earlier didn't know its types.
    var connection = (NpgsqlConnection)db.Database.GetDbConnection();
    await connection.OpenAsync(ct);
    await connection.ReloadTypesAsync(ct);
});

builder.AddRedisDistributedCache("redis");
builder.Services.AddHybridCache();

builder.Services.AddSingleton(TimeProvider.System);

// Search asks the sync worker to import provider matches first; the worker owns provider calls and writes.
// No retries: when the worker is down, search should answer from the catalog straight away.
#pragma warning disable EXTEXP0001 // RemoveAllResilienceHandlers is experimental; it undoes the ServiceDefaults handler for this client.
builder.Services.AddHttpClient<CatalogImportClient>(http => http.BaseAddress = new Uri("https+http://catalog-sync/"))
    .RemoveAllResilienceHandlers();
#pragma warning restore EXTEXP0001
builder.Services.AddSearchRateLimiting(builder.Configuration);

// Search also lists shows similar in meaning to the term, by comparing its vector with the sync worker's show vectors.
builder.Services.AddLocalEmbeddings(builder.Configuration);
builder.Services.AddSingleton<SemanticSearch>();
builder.Services.AddProblemDetails();
builder.Services.AddOpenApi();

var app = builder.Build();

app.MapDefaultEndpoints();

app.UseAuthentication();
app.UseRateLimiter();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.MapGet("/api/v1/status", () => new { Service = "catalog-api", Status = "ok" });
app.MapCatalogApi();

app.Run();
