using Identity.API.Apis;
using Identity.API.Payments;
using Identity.API.Services;
using Identity.Data;
using Microsoft.Extensions.Options;
using TvTracker.Persistence;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.AddDefaultAuthentication();
builder.AddFeatureFlags();

builder.AddNpgsqlDbContext<IdentityContext>("identitydb", configureSettings: s => s.ConnectionString = ConnectionPool.Limit(s.ConnectionString));
builder.Services.AddDatabaseInitializer<IdentityContext>(baseline: "InitialCreate");

builder.AddRabbitMQEventBus("eventbus");
builder.Services.AddIntegrationEventOutbox<IdentityContext>();

builder.Services.AddHttpClient<KeycloakAdminClient>();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddHostedService<ProfileSnapshotPublisher>();
builder.Services.AddHostedService<RealmSecretsSeeder>();
builder.Services.AddHostedService<AdminAccountSeeder>();
if (!builder.Environment.IsDevelopment())
{
    builder.Services.AddHostedService<DemoAccountRemover>();
}

// Payments: the provider named in Payments:Provider sells plans; Stripe is always registered (its webhooks need its
// signing secret, so it does nothing unconfigured), the test provider only in development.
builder.Services.AddOptions<PaymentOptions>().Bind(builder.Configuration.GetSection("Payments")).ValidateOnStart();
builder.Services.AddSingleton<IValidateOptions<PaymentOptions>, PaymentOptionsValidator>();
// What an admin sets on the Financials page lies over that configuration; everything that reads the options reads
// the result. Keys entered there are encrypted under Payments:SettingsKey.
builder.Services.AddSingleton<SettingsCipher>();
builder.Services.AddSingleton<EffectivePaymentOptions>();
builder.Services.AddSingleton<IOptions<PaymentOptions>>(sp => sp.GetRequiredService<EffectivePaymentOptions>());
builder.Services.AddHostedService<PaymentSettingsRefresher>();
if (builder.Environment.IsDevelopment())
{
    builder.Services.AddSingleton<IPaymentProvider, TestPaymentProvider>();
}
// Stripe and Polar are both always registered: each one's webhooks carry on for subscriptions bought through it,
// whichever sells now, and neither does anything unconfigured.
AddPaymentProvider<StripePaymentProvider>(options => options.Stripe.ApiBase);
AddPaymentProvider<PolarPaymentProvider>(options => options.Polar.ApiBase);
builder.Services.AddScoped<PaymentProviders>();
builder.Services.AddScoped<SubscriptionLedger>();
builder.Services.AddHostedService<SubscriptionLapse>();

// Used by the data export to collect each service's data for the user.
foreach (var source in ProfileApi.ExportSources)
{
    builder.Services.AddHttpClient(source, client => client.BaseAddress = new Uri($"https+http://{source}-api/"));
}

builder.Services.AddProblemDetails();
builder.Services.AddOpenApi();

var app = builder.Build();

app.MapDefaultEndpoints();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseDefaultAuthentication();

app.MapGet("/api/v1/status", () => new { Service = "identity-api", Status = "ok" });
app.MapProfileApi();
app.MapFlagsApi();
app.MapPlansApi();
app.MapRevenueApi();
app.MapBrandingApi();
app.MapFamilyApi();
app.MapPaymentsApi();
app.MapPaymentSettingsApi();

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
