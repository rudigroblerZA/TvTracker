using Financials.API.Apis;
using Financials.API.IntegrationEvents;
using Financials.API.Payments;
using Financials.API.Services;
using Financials.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TvTracker.IntegrationEvents;
using TvTracker.Persistence;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.AddDefaultAuthentication();
builder.AddFeatureFlags();

builder.AddNpgsqlDbContext<FinancialsContext>("financialsdb", configureSettings: s => s.ConnectionString = ConnectionPool.Limit(s.ConnectionString));
builder.Services.AddDatabaseInitializer<FinancialsContext>(async (db, services, ct) =>
{
    // What payments and affiliates kept while they were Identity's and Catalog's, once.
    await LegacyImport.RunAsync(db, services.GetRequiredService<IConfiguration>(), services.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(LegacyImport)), ct);

    // In development with the test network running, an empty affiliate programs table starts with a test program per
    // main service. Only when empty: programs an admin removed don't come back while any other is left.
    if (services.GetRequiredService<IHostEnvironment>().IsDevelopment()
        && AffiliateNetworks.TestNetwork(services.GetRequiredService<IConfiguration>()) is not null
        && !await db.AffiliatePrograms.AnyAsync(ct))
    {
        db.AffiliatePrograms.AddRange(StarterPrograms.Missing([], services.GetRequiredService<TimeProvider>().GetUtcNow().UtcDateTime));
        await db.SaveChangesAsync(ct);
    }
});

builder.AddRabbitMQEventBus("eventbus")
    .AddSubscription<UserDeletedIntegrationEvent, UserDeletedHandler>()
    .AddSubscription<OutboundClickedIntegrationEvent, OutboundClickedHandler>();
builder.Services.AddIntegrationEventOutbox<FinancialsContext>();

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddOptions<FinancialOptions>().Bind(builder.Configuration.GetSection("Financials"))
    .Validate(o => o.PaymentFeePercent is >= 0 and < 100 && o.PaymentFeeFixed >= 0, "Financials fee settings must be a percent from 0 to under 100 and a fixed fee of 0 or more.")
    .ValidateOnStart();

// Payments: the provider named in Payments:Provider sells plans; Stripe is always registered (its webhooks need its
// signature check even when another provider sells), and the test provider only in development.
builder.Services.AddOptions<PaymentOptions>().Bind(builder.Configuration.GetSection("Payments")).ValidateOnStart();
builder.Services.AddSingleton<IValidateOptions<PaymentOptions>, PaymentOptionsValidator>();
// What an admin set on the Financials page lies over configuration (EffectivePaymentOptions); everything reads
// the result. Keys entered there are encrypted under Payments:SettingsKey.
builder.Services.AddSingleton<SettingsCipher>();
builder.Services.AddSingleton<EffectivePaymentOptions>();
builder.Services.AddSingleton<IOptions<PaymentOptions>>(sp => sp.GetRequiredService<EffectivePaymentOptions>());
builder.Services.AddHostedService<PaymentSettingsRefresher>();
if (builder.Environment.IsDevelopment())
{
    builder.Services.AddSingleton<IPaymentProvider, TestPaymentProvider>();
}
AddPaymentProvider<StripePaymentProvider>(options => options.Stripe.ApiBase);
AddPaymentProvider<PolarPaymentProvider>(options => options.Polar.ApiBase);
builder.Services.AddScoped<PaymentProviders>();
builder.Services.AddScoped<SubscriptionLedger>();
builder.Services.AddHostedService<SubscriptionLapse>();

// Affiliate programs: an admin's from Settings (the affiliate_programs table), then the configuration's (read on each
// use, so an edited appsettings file applies without a restart), then the development test network's catch-all, so a
// real program for a service still wins. Catalog.API builds the links from the copy it's sent.
builder.Services.AddMemoryCache();
builder.Services.Configure<AffiliateOptions>(builder.Configuration.GetSection("Affiliates"));
builder.Services.AddScoped<AffiliatePrograms>();
builder.Services.AddHostedService<AffiliateProgramsPublisher>();

// What Identity is asked, which owns the members: a buyer's region, and how many accounts there are. One quick try,
// without the default retries: were Identity down, the answer should take a few seconds and say so, not half a minute.
#pragma warning disable EXTEXP0001 // RemoveAllResilienceHandlers is experimental; Catalog.API's ops clients use it the same way.
builder.Services.AddHttpClient(Members.Client, http =>
    {
        http.BaseAddress = new Uri("https+http://identity-api/");
        http.Timeout = TimeSpan.FromSeconds(5);
    })
    .RemoveAllResilienceHandlers();
#pragma warning restore EXTEXP0001
builder.Services.AddSingleton<Members>();

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
app.MapRevenueApi();
app.MapPaymentsApi();
app.MapPaymentSettingsApi();
app.MapAffiliateApi();

app.Run();

// A provider that calls its API over a typed HttpClient. No automatic retries: a repeated cancel, or a delete the
// provider already carried out, comes back as an error, and a checkout shouldn't keep the buyer waiting 30 s; one try
// within 20 s, and the caller answers 502.
void AddPaymentProvider<T>(Func<PaymentOptions, string> apiBase) where T : class, IPaymentProvider
{
#pragma warning disable EXTEXP0001 // RemoveAllResilienceHandlers is experimental; it undoes the ServiceDefaults handler for this client.
    builder.Services.AddHttpClient<T>((sp, client) =>
        {
            client.BaseAddress = new Uri(apiBase(sp.GetRequiredService<IOptionsMonitor<PaymentOptions>>().CurrentValue));
            client.Timeout = TimeSpan.FromSeconds(20);
        })
        .RemoveAllResilienceHandlers();
#pragma warning restore EXTEXP0001
    builder.Services.AddTransient<IPaymentProvider>(sp => sp.GetRequiredService<T>());
}
