using System.Net.Http.Json;

var builder = DistributedApplication.CreateBuilder(args);

// White label: the app's name in the UI, page titles and the text services write ("★ 4.5 from 12 <name> users").
// Passed to the web app and the services that put it in text; the phone has its own (MobileSettings.AppName).
var appName = builder.Configuration["Branding:AppName"] is { Length: > 0 } configuredName ? configuredName : "TV Tracker";

// Secrets are Aspire secret parameters (Parameters:<name>: user secrets, environment variables, or azd's environment
// secrets in a deployment). Running locally, one that isn't set takes its development value, which matches
// Realms/tvtracker-realm.json; a publish never carries those, so a deployment has to be given every one. Identity.API puts
// them into Keycloak's realm on start (client secrets: RealmSecretsSeeder; the app admin's password: AdminAccountSeeder), so
// a realm imported from the file doesn't keep the development values.
IResourceBuilder<ParameterResource> Secret(string name, string developmentValue)
{
    if (!builder.ExecutionContext.IsPublishMode && string.IsNullOrEmpty(builder.Configuration[$"Parameters:{name}"]))
    {
        builder.Configuration[$"Parameters:{name}"] = developmentValue;
    }
    return builder.AddParameter(name, secret: true);
}

// Where a publish goes: one machine running Docker Compose. `aspire publish` writes a compose file and an .env of the
// parameters it needs (README, Deploying). Running locally, this does nothing.
var compose = builder.AddDockerComposeEnvironment("tvtracker");
var publishing = builder.ExecutionContext.IsPublishMode;

// ---------------------------------------------------------------------------
// Infrastructure (containers; Docker or Podman must be running)
// ---------------------------------------------------------------------------

// The pgvector image is Postgres plus the vector extension, for semantic search. Same major version and Debian release
// (trixie, whose collation version existing databases were created with) as Aspire's default postgres image.
var postgres = builder.AddPostgres("postgres")
    .WithImage("pgvector/pgvector", "0.8.7-pg18-trixie")
    .WithLifetime(ContainerLifetime.Persistent);
// Same volume WithDataVolume would create, but mounted where Postgres 18 keeps its data: WithDataVolume picks the path
// from the image tag, and doesn't recognise pgvector's tags as Postgres 18.
postgres.WithVolume(VolumeNameGenerator.Generate(postgres, "data"), "/var/lib/postgresql");

// One database per service: no service reads another's tables.
var identityDb = postgres.AddDatabase("identitydb");
var catalogDb = postgres.AddDatabase("catalogdb");
var trackingDb = postgres.AddDatabase("trackingdb");
var ratingsDb = postgres.AddDatabase("ratingsdb");
var researchDb = postgres.AddDatabase("researchdb");
var recommendationsDb = postgres.AddDatabase("recommendationsdb");
var notificationsDb = postgres.AddDatabase("notificationsdb");
var analyticsDb = postgres.AddDatabase("analyticsdb");
var socialDb = postgres.AddDatabase("socialdb");

// Redis holds the web app's server-side tokens and the keys that protect its sign-in cookie, so it keeps its data
// across restarts: without the volume, restarting Redis signs everyone out.
var redis = builder.AddRedis("redis").WithDataVolume();

// The queues are durable (events waiting to be handled, retries, dead letters); the volume is what keeps them when
// the broker's container is replaced.
var eventBus = builder.AddRabbitMQ("eventbus")
    .WithDataVolume()
    .WithManagementPlugin()
    .WithLifetime(ContainerLifetime.Persistent);

// Keycloak: sign-up, sign-in, password reset, external logins. The "tvtracker" realm is imported from ./Realms
// on first start. It listens on a fixed port (8080) so token issuers stay the same for the web app, the services
// and the Android emulator (10.0.2.2:8080). Admin console: http://localhost:8080 (admin / the parameter below).
var keycloakAdminPassword = Secret("keycloak-admin-password", "admin");

var keycloak = builder.AddContainer("keycloak", "quay.io/keycloak/keycloak", "26.4")
    // Locally, development mode on its own file database. Published, production mode on Postgres (see Deployment below).
    .WithArgs(publishing ? "start" : "start-dev", "--import-realm")
    .WithEnvironment("KC_BOOTSTRAP_ADMIN_USERNAME", "admin")
    .WithEnvironment("KC_BOOTSTRAP_ADMIN_PASSWORD", keycloakAdminPassword)
    .WithEnvironment("KC_HEALTH_ENABLED", "true")
    // The web app can proxy the login pages (WebApp:PublicUrl below); Keycloak builds their links from X-Forwarded-*.
    .WithEnvironment("KC_PROXY_HEADERS", "xforwarded")
    .WithVolume("tvtracker-keycloak-data", "/opt/keycloak/data")
    .WithBindMount("./Realms", "/opt/keycloak/data/import", isReadOnly: true)
    .WithHttpEndpoint(port: 8080, targetPort: 8080, name: "http")
    .WithHttpEndpoint(targetPort: 9000, name: "management")
    .WithHttpHealthCheck("/health/ready", endpointName: "management")
    .WithLifetime(ContainerLifetime.Persistent);

var keycloakUrl = keycloak.GetEndpoint("http");

// flagd: the OpenFeature flag server, reading ./Flags/flagd.json and picking up edits to it while running. Services
// and the web app evaluate flags through the OpenFeature SDK (flagd provider, ConnectionStrings:flagd); the mobile
// app through the BFF's /features route, over OFREP. Nothing waits for it: with flagd down, flags take their defaults.
// Flags/overrides.json is a second source, written by the flags page (Identity.API); for a key in both, flagd uses the
// later source. It isn't checked in, so it's created empty when missing.
var flagsDirectory = Path.Combine(builder.AppHostDirectory, "Flags");
var flagOverrides = Path.Combine(flagsDirectory, "overrides.json");
if (!builder.ExecutionContext.IsPublishMode && !File.Exists(flagOverrides))
{
    File.WriteAllText(flagOverrides, "{ \"flags\": {} }\n");
}
var flagd = builder.AddFlagd("flagd")
    .WithBindFileSync("./Flags/")
    .WithArgs("--uri", "file:./flags/overrides.json")
    .WithLifetime(ContainerLifetime.Persistent);

// The Keycloak clients' secrets and the app admin's password (see Secret above).
var webClientSecret = Secret("web-client-secret", "tvtracker-web-dev-secret");
var identityAdminClientSecret = Secret("identity-admin-client-secret", "tvtracker-identity-admin-dev-secret");
var appAdminPassword = Secret("app-admin-password", "admin");
// The internal client other services sign in as to call the sync worker's imports, replay and overrides.
var internalClientSecret = Secret("internal-client-secret", "tvtracker-internal-dev-secret");

// ---------------------------------------------------------------------------
// Services
// ---------------------------------------------------------------------------

var identityApi = builder.AddProject<Projects.Identity_API>("identity-api")
    .WithReference(identityDb).WaitFor(identityDb)
    .WithReference(flagd)
    .WithReference(eventBus).WaitFor(eventBus)
    .WithEnvironment("Identity__Url", keycloakUrl)
    .WithEnvironment("Identity__AdminClientSecret", identityAdminClientSecret)
    .WithEnvironment("Keycloak__AdminUsername", "admin")
    .WithEnvironment("Keycloak__AdminPassword", keycloakAdminPassword)
    .WithEnvironment("Keycloak__ClientSecrets__tvtracker-web", webClientSecret)
    .WithEnvironment("Keycloak__ClientSecrets__tvtracker-identity-admin", identityAdminClientSecret)
    .WithEnvironment("Keycloak__ClientSecrets__tvtracker-internal", internalClientSecret)
    .WithEnvironment("Keycloak__ServiceClients__tvtracker-internal", "Internal service calls")
    // Where the flags page writes its overrides: the AppHost's folder locally; published, the folder mounted into
    // both this service and flagd (Deployment below).
    .WithEnvironment("Flags__Directory", publishing ? "/flags" : flagsDirectory)
    .WaitFor(keycloak)
    .WithHttpHealthCheck("/health");

var catalogApi = builder.AddProject<Projects.Catalog_API>("catalog-api")
    .WithEnvironment("Branding__AppName", appName)
    .WithReference(catalogDb).WaitFor(catalogDb)
    .WithReference(flagd).WithReference(identityApi)
    .WithReference(redis).WaitFor(redis)
    .WithReference(eventBus).WaitFor(eventBus)
    .WithEnvironment("Identity__Url", keycloakUrl)
    .WithHttpHealthCheck("/health");

var trackingApi = builder.AddProject<Projects.Tracking_API>("tracking-api")
    .WithReference(trackingDb).WaitFor(trackingDb)
    .WithReference(flagd)
    .WithReference(eventBus).WaitFor(eventBus)
    .WithReference(redis).WaitFor(redis)
    .WithEnvironment("Identity__Url", keycloakUrl)
    .WithHttpHealthCheck("/health");

var ratingsApi = builder.AddProject<Projects.Ratings_API>("ratings-api")
    .WithReference(ratingsDb).WaitFor(ratingsDb)
    .WithReference(eventBus).WaitFor(eventBus)
    .WithReference(redis).WaitFor(redis)
    .WithReference(flagd)
    .WithEnvironment("Identity__Url", keycloakUrl)
    .WithHttpHealthCheck("/health");

var recommendationsApi = builder.AddProject<Projects.Recommendations_API>("recommendations-api")
    .WithReference(recommendationsDb).WaitFor(recommendationsDb)
    .WithReference(eventBus).WaitFor(eventBus)
    .WithReference(redis).WaitFor(redis)
    .WithReference(flagd)
    .WithEnvironment("Identity__Url", keycloakUrl)
    .WithHttpHealthCheck("/health");

var notificationsApi = builder.AddProject<Projects.Notifications_API>("notifications-api")
    .WithEnvironment("Branding__AppName", appName)
    .WithReference(notificationsDb).WaitFor(notificationsDb)
    .WithReference(catalogApi)
    .WithReference(eventBus).WaitFor(eventBus)
    .WithReference(flagd)
    .WithEnvironment("Identity__Url", keycloakUrl)
    .WithHttpHealthCheck("/health");

// Analytics.API owns analyticsdb; the worker consumes catalog and rating events and computes insights hourly.
var analyticsApi = builder.AddProject<Projects.Analytics_API>("analytics-api")
    .WithReference(analyticsDb).WaitFor(analyticsDb)
    .WithReference(flagd)
    .WithEnvironment("Identity__Url", keycloakUrl)
    .WithHttpHealthCheck("/health");

var analyticsWorker = builder.AddProject<Projects.Analytics_Worker>("analytics-worker")
    .WithEnvironment("Branding__AppName", appName)
    .WithReference(analyticsDb)
    .WithReference(eventBus).WaitFor(eventBus)
    .WaitFor(analyticsApi)
    .WithEnvironment("Identity__Url", keycloakUrl)
    .WithHttpHealthCheck("/health");

// Social.API: who follows whom, and the feed of their activity, built from tracking and rating events.
var socialApi = builder.AddProject<Projects.Social_API>("social-api")
    .WithEnvironment("Branding__AppName", appName)
    .WithReference(socialDb).WaitFor(socialDb)
    .WithReference(eventBus).WaitFor(eventBus)
    .WithReference(flagd)
    .WithEnvironment("Identity__Url", keycloakUrl)
    .WithHttpHealthCheck("/health");

// Research: moderators enrich a show from Wikidata, Wikipedia, TMDB (Research:TmdbApiKey) and pages they add, with an
// insight drafted by Model Runner's chat model; saved research is the show's trusted record, and its catalog fields
// (summary, network, premiere date) override the provider's through the sync worker.
var researchApi = builder.AddProject<Projects.Research_API>("research-api")
    .WithReference(researchDb).WaitFor(researchDb)
    .WithReference(eventBus).WaitFor(eventBus)
    .WithReference(flagd).WithReference(identityApi)
    .WithReference(catalogApi)
    .WithEnvironment("Identity__Url", keycloakUrl)
    .WithHttpHealthCheck("/health");
// TMDB's key, for research sources: plain configuration locally (Research:TmdbApiKey); in a deployment the secret
// parameter tmdb-api-key, so it isn't written into the published manifest.
if (builder.Configuration["Parameters:tmdb-api-key"] is { Length: > 0 })
{
    researchApi.WithEnvironment("Research__TmdbApiKey", builder.AddParameter("tmdb-api-key", secret: true));
}
else if (!builder.ExecutionContext.IsPublishMode && builder.Configuration["Research:TmdbApiKey"] is { Length: > 0 } tmdbKey)
{
    researchApi.WithEnvironment("Research__TmdbApiKey", tmdbKey);
}

// The data export collects each service's data for the user.
// Moderators (Ratings.API's moderation page): admins and Moderation:Users. Moderation:BlockedTerms are refused in posts.
foreach (var section in new[] { "Admin:Users", "Moderation:Users", "Moderation:BlockedTerms" })
{
    foreach (var item in builder.Configuration.GetSection(section).GetChildren())
    {
        ratingsApi.WithEnvironment($"{section.Replace(":", "__")}__{item.Key}", item.Value);
        if (section != "Moderation:BlockedTerms")
        {
            researchApi.WithEnvironment($"{section.Replace(":", "__")}__{item.Key}", item.Value);
        }
    }
}

// Admin:Users (Keycloak usernames) may use the feature flags page. The app's own admin account (admin / admin in
// development, separate from Keycloak's console admin in the master realm) is in the realm file, and Identity.API
// creates it in a realm imported before it was added.
foreach (var admin in builder.Configuration.GetSection("Admin:Users").GetChildren())
{
    identityApi.WithEnvironment($"Admin__Users__{admin.Key}", admin.Value);
    // ...and the affiliate report (Catalog.API).
    catalogApi.WithEnvironment($"Admin__Users__{admin.Key}", admin.Value);
}
identityApi
    .WithEnvironment("Admin__Account__Username", "admin")
    .WithEnvironment("Admin__Account__Password", appAdminPassword);

identityApi
    .WithReference(trackingApi)
    .WithReference(ratingsApi)
    .WithReference(recommendationsApi)
    .WithReference(notificationsApi)
    .WithReference(socialApi)
    .WithReference(analyticsApi)
    .WithReference(researchApi);

// The sync worker publishes catalog events, so it starts once the consuming services are healthy
// (their health includes "queue bound and consuming") and Catalog.API has created the schema.
var catalogSync = builder.AddProject<Projects.Catalog_SyncWorker>("catalog-sync")
    .WithReference(catalogDb)
    .WithReference(eventBus)
    .WithReference(redis)
    .WaitFor(catalogApi)
    .WaitFor(trackingApi)
    .WaitFor(recommendationsApi)
    .WaitFor(notificationsApi)
    .WaitFor(analyticsWorker)
    .WaitFor(socialApi)
    .WithEnvironment("Identity__Url", keycloakUrl)
    .WithHttpHealthCheck("/health");

// Where the catalog comes from: TVmaze unless the AppHost's Catalog:Provider says "wikidata" (CC0), "tvdb" (TheTVDB) or
// "mock". Catalog.API is told too, since it matches the provider's ids (history imports, character trails) by the
// shows' source; the web app is told further down, for the footer's credit.
var catalogProvider = builder.Configuration["Catalog:Provider"] is { Length: > 0 } configuredProvider ? configuredProvider.Trim().ToLowerInvariant() : null;
if (catalogProvider is not null)
{
    catalogApi.WithEnvironment("Catalog__Provider", catalogProvider);
    catalogSync.WithEnvironment("Catalog__Provider", catalogProvider);
}
// TheTVDB needs the project's API key: a secret parameter (locally, user secrets: Parameters:tvdb-api-key), and a
// subscriber's PIN when the key is on TheTVDB's user-supported model (Parameters:tvdb-pin).
if (catalogProvider == "tvdb")
{
    catalogSync.WithEnvironment("Tvdb__ApiKey", builder.AddParameter("tvdb-api-key", secret: true));
    if (builder.Configuration["Parameters:tvdb-pin"] is { Length: > 0 })
    {
        catalogSync.WithEnvironment("Tvdb__Pin", builder.AddParameter("tvdb-pin", secret: true));
    }
}

// The sync worker's callers sign in as the internal client; the worker accepts nothing else on its internal APIs.
foreach (var caller in new[] { catalogApi, ratingsApi, researchApi, analyticsWorker, socialApi })
{
    caller.WithEnvironment("Internal__ClientSecret", internalClientSecret);
}

// The embedding model for semantic search: the "Embeddings" section of the AppHost's configuration (Model, and any
// Models:{id} settings, such as a Docker Model Runner endpoint). Passed to both, since Catalog.API embeds search terms
// with the model the sync worker embedded the shows with; changing the model re-embeds the catalog.
foreach (var (key, value) in Microsoft.Extensions.Configuration.ConfigurationExtensions.AsEnumerable(builder.Configuration.GetSection("Embeddings"), makePathsRelative: true))
{
    if (value is not null)
    {
        var name = "Embeddings__" + key.Replace(":", "__");
        catalogApi.WithEnvironment(name, value);
        catalogSync.WithEnvironment(name, value);
    }
}

// Docker Model Runner serves the default embedding model (dmr/qwen3-embedding-0.6b). The AppHost runs it as a
// container (CPU; the model is kept in a volume), unless ModelRunner:Url points at one already running, such as
// Docker Desktop's (http://localhost:12434, which can use the GPU). The sync worker pulls the model on first start;
// until it's ready, search matches titles only. Not started for an in-process model: the built-in server models
// are the dmr/ ones, and a custom one says Source: Server.
var embeddingModelId = builder.Configuration["Embeddings:Model"] ?? "";
// The chat model Research drafts insights with; pulled with the dashboard's command. The
// default is small enough for a CPU (qwen3 0.6B, ~400 MB).
var chatModel = builder.Configuration["ModelRunner:ChatModel"] is { Length: > 0 } configuredChat ? configuredChat : "ai/qwen3:0.6B-Q4_K_M";
researchApi.WithEnvironment("Research__ChatModel", chatModel);
var servedModel = embeddingModelId.StartsWith("dmr/", StringComparison.Ordinal)
    || string.Equals(builder.Configuration[$"Embeddings:Models:{embeddingModelId}:Source"], "Server", StringComparison.OrdinalIgnoreCase);
if (!servedModel)
{
    // Nothing to do: Catalog.API and the sync worker run the model themselves.
}
else if (builder.Configuration["ModelRunner:Url"] is { Length: > 0 } modelRunnerUrl)
{
    var endpoint = modelRunnerUrl.TrimEnd('/') + "/engines/v1/";
    catalogApi.WithEnvironment("Embeddings__Endpoint", endpoint);
    catalogSync.WithEnvironment("Embeddings__Endpoint", endpoint);
    researchApi.WithEnvironment("Research__ChatEndpoint", endpoint);
}
else
{
    var modelRunner = builder.AddContainer("model-runner", "docker/model-runner", "v1.2.9")
        .WithHttpEndpoint(targetPort: 12434, name: "http")
        .WithVolume("tvtracker-model-runner", "/models")
        .WithHttpHealthCheck("/models")
        .WithLifetime(ContainerLifetime.Persistent);
    var endpoint = ReferenceExpression.Create($"{modelRunner.GetEndpoint("http")}/engines/v1/");
    catalogApi.WithEnvironment("Embeddings__Endpoint", endpoint).WaitFor(modelRunner);
    catalogSync.WithEnvironment("Embeddings__Endpoint", endpoint).WaitFor(modelRunner);
    researchApi.WithEnvironment("Research__ChatEndpoint", endpoint);

    // Model Runner serves only the embedding model until a chat model is pulled: this adds a "Pull <model>" command to
    // the model-runner resource on the dashboard (Model Runner's own pull API, which the sync worker also uses).
    modelRunner.WithHttpCommand("/models/create", $"Pull {chatModel}", commandName: "pull-chat-model",
        commandOptions: new HttpCommandOptions
        {
            Method = HttpMethod.Post,
            Description = $"Pulls {chatModel} for drafting research insights. Large models take minutes.",
            IconName = "ArrowDownload",
            PrepareRequest = context =>
            {
                context.Request.Content = JsonContent.Create(new { from = chatModel });
                return Task.CompletedTask;
            }
        });
}

// Search asks the sync worker to import matches from the provider. Until the worker is up, search uses what the catalog has.
catalogApi.WithReference(catalogSync);

// Moderators' accepted corrections, and saved research, are applied to the catalog by the sync worker.
ratingsApi.WithReference(catalogSync);
researchApi.WithReference(catalogSync);

// The ops dashboard (/ops on Catalog.API) includes Analytics' and Research's stats.
catalogApi.WithReference(analyticsApi);
catalogApi.WithReference(researchApi);

// On first start (no shows yet), Analytics asks the sync worker to replay the catalog.
analyticsWorker.WithReference(catalogSync);
socialApi.WithReference(catalogSync);

// ---------------------------------------------------------------------------
// Gateways and clients
// ---------------------------------------------------------------------------

// The MAUI app talks to the services only through this gateway (http://localhost:5110, or 10.0.2.2:5110 from the Android emulator).
var mobileBff = builder.AddProject<Projects.Mobile_Bff>("mobile-bff")
    .WithReference(flagd.GetEndpoint("ofrep"))
    .WithReference(identityApi)
    .WithReference(catalogApi)
    .WithReference(trackingApi)
    .WithReference(ratingsApi)
    .WithReference(recommendationsApi)
    .WithReference(notificationsApi)
    .WithReference(analyticsApi)
    .WithReference(socialApi)
    .WithReference(researchApi);

// WebApp:PublicUrl: the web app's address when it's reached through one forwarded address only (a cloud session's
// preview), where the browser can't reach Keycloak. Sign-in then goes through the web app (SignInThroughWebApp), and
// Identity.API allows that address as a redirect. Unset, the browser signs in at Keycloak's address.
var webPublicUrl = builder.Configuration["WebApp:PublicUrl"];
if (!string.IsNullOrEmpty(webPublicUrl))
{
    identityApi.WithEnvironment("Keycloak__WebPublicUrl", webPublicUrl);
}

// Email (alerts and the weekly digest), through Azure Communication Services: the secret parameter
// email-connection-string and Email:SenderAddress. Without them, development logs each message and a deployment
// sends none (deliveries show as failed; digests are off). Links in the digest point at the web app's public address.
if (builder.Configuration["Parameters:email-connection-string"] is { Length: > 0 } && builder.Configuration["Email:SenderAddress"] is { Length: > 0 } sender)
{
    notificationsApi
        .WithEnvironment("Email__ConnectionString", builder.AddParameter("email-connection-string", secret: true))
        .WithEnvironment("Email__SenderAddress", sender);
}
if ((builder.Configuration["Digest:BaseUrl"] ?? webPublicUrl) is { Length: > 0 } digestBaseUrl)
{
    notificationsApi.WithEnvironment("Digest__BaseUrl", digestBaseUrl);
}

var webApp = builder.AddProject<Projects.WebApp>("webapp")
    .WithEnvironment("Branding__AppName", appName)
    // Who runs the app and how to reach them, for the privacy policy and terms (README, Privacy and terms).
    .WithEnvironment("Catalog__Provider", catalogProvider ?? "")
    .WithReference(flagd)
    .WithReference(redis).WaitFor(redis)
    .WithEnvironment("Identity__Url", keycloakUrl)
    .WithEnvironment("Identity__WebClientSecret", webClientSecret)
    .WaitFor(keycloak)
    .WithReference(identityApi)
    .WithReference(catalogApi).WaitFor(catalogApi)
    .WithReference(trackingApi).WaitFor(trackingApi)
    .WithReference(ratingsApi)
    .WithReference(recommendationsApi)
    .WithReference(notificationsApi)
    .WithReference(analyticsApi)
    .WithReference(socialApi)
    .WithReference(researchApi);
if (!string.IsNullOrEmpty(webPublicUrl))
{
    webApp.WithEnvironment("WebApp__PublicUrl", webPublicUrl);
}

// The test affiliate network (running locally only; Affiliates:TestNetwork=false turns it off): a pretend network, so
// affiliate links, postbacks and the report can be tried without a real program. Test programs set up in Settings, and
// any service no program covers, send their where-to-watch clicks through the web app's /dev/affiliate-network page,
// which can report a sale (TestAffiliateNetwork, AffiliateLinks.TestNetwork).
if (!builder.ExecutionContext.IsPublishMode && !string.Equals(builder.Configuration["Affiliates:TestNetwork"], "false", StringComparison.OrdinalIgnoreCase))
{
    const string testNetworkKey = "test-network-key";
    catalogApi
        .WithEnvironment("Affiliates__TestNetwork__Url", webApp.GetEndpoint("http"))
        .WithEnvironment("Affiliates__TestNetwork__Key", testNetworkKey);
    webApp.WithEnvironment("Affiliates__TestNetworkKey", testNetworkKey);
}

// Payments. A deployment sells through Stripe: its secret key and webhook
// signing secret are secret parameters, the customer portal's login link and the plan page's public address plain ones,
// all required. Running locally, Stripe sells in test mode once its key is given (user secrets:
// Parameters:stripe-secret-key and stripe-webhook-secret, from `stripe listen`; README, Payments); otherwise the test
// provider does, its checkout being the web app's /dev/payments page, which signs the webhooks it sends Identity with a
// secret the two share.
// Both values, locally: a webhook secret left unset would be a parameter with no value, which Identity waits on for ever.
var stripeLocally = !builder.ExecutionContext.IsPublishMode
    && !string.IsNullOrEmpty(builder.Configuration["Parameters:stripe-secret-key"])
    && !string.IsNullOrEmpty(builder.Configuration["Parameters:stripe-webhook-secret"]);
if (builder.ExecutionContext.IsPublishMode || stripeLocally)
{
    identityApi
        .WithEnvironment("Payments__Provider", "stripe")
        .WithEnvironment("Payments__Stripe__SecretKey", builder.AddParameter("stripe-secret-key", secret: true))
        .WithEnvironment("Payments__Stripe__WebhookSecret", builder.AddParameter("stripe-webhook-secret", secret: true));
    if (builder.ExecutionContext.IsPublishMode)
    {
        // The address Stripe sends the buyer back to is the plan page at the site's public address (Deployment below).
        identityApi.WithEnvironment("Payments__Stripe__PortalUrl", builder.AddParameter("stripe-portal-url"));
    }
    else
    {
        // Stripe needs an absolute address to send the buyer back to: the web app's own, here, https when its launch
        // profile has it (an endpoint that doesn't exist never resolves, and Identity would wait on it for ever).
        var webScheme = webApp.Resource.Annotations.OfType<EndpointAnnotation>().Any(e => e.Name == "https") ? "https" : "http";
        identityApi.WithEnvironment("Payments__ReturnUrl", ReferenceExpression.Create($"{webApp.GetEndpoint(webScheme)}/settings/plan"));
        if (builder.Configuration["Parameters:stripe-portal-url"] is { Length: > 0 })
        {
            identityApi.WithEnvironment("Payments__Stripe__PortalUrl", builder.AddParameter("stripe-portal-url"));
        }
        // A stand-in for Stripe's API (Stripe's stripe-mock, or a recorder) when testing without an account.
        if (builder.Configuration["Stripe:ApiBase"] is { Length: > 0 } apiBase)
        {
            identityApi.WithEnvironment("Payments__Stripe__ApiBase", apiBase);
        }
    }
}
else
{
    // The test pages are linked relative to the page the buyer is on, so the browser keeps the origin (http or https)
    // it signed in on and its sign-in cookie.
    var paymentsSecret = Secret("payments-test-webhook-secret", "tvtracker-payments-dev-secret");
    identityApi
        .WithEnvironment("Payments__Provider", "test")
        .WithEnvironment("Payments__ReturnUrl", "/settings/plan")
        .WithEnvironment("Payments__Test__WebhookSecret", paymentsSecret);
    webApp.WithEnvironment("Payments__Test__WebhookSecret", paymentsSecret);
}

// ---------------------------------------------------------------------------
// Deployment: what a publish adds or changes (README, Deploying). One machine, Docker Compose, one public site.
// ---------------------------------------------------------------------------

if (publishing)
{
    // The site's host name (tv.example.com) and the mobile gateway's (api.tv.example.com); both must point at the machine.
    var publicHost = builder.AddParameter("public-host");
    var apiHost = builder.AddParameter("api-host");
    var publicUrl = ReferenceExpression.Create($"https://{publicHost}");

    // The front door: Caddy, the only container with ports on the machine. It gets and renews the certificates, and
    // sends the site's host to the web app and the gateway's to the mobile gateway. Nothing else is reachable from outside.
    builder.AddContainer("caddy", "docker.io/library/caddy", "2.10")
        .WithBindMount("./Deploy/Caddyfile", "/etc/caddy/Caddyfile", isReadOnly: true)
        .WithVolume("tvtracker-caddy-data", "/data")
        .WithEnvironment("PUBLIC_HOST", publicHost)
        .WithEnvironment("API_HOST", apiHost)
        .WithEnvironment("WEBAPP_UPSTREAM", webApp.GetEndpoint("http").Property(EndpointProperty.HostAndPort))
        .WithEnvironment("API_UPSTREAM", mobileBff.GetEndpoint("http").Property(EndpointProperty.HostAndPort))
        .WithHttpEndpoint(port: 80, targetPort: 80, name: "http")
        .WithHttpsEndpoint(port: 443, targetPort: 443, name: "https")
        .WithExternalHttpEndpoints();

    // Sign-in goes through the site: the web app passes Keycloak's login pages on at the public address, so Keycloak
    // itself (and its admin console) has no address outside. Keycloak keeps its data in Postgres, in a database of its
    // own, which the Postgres container creates the first time it starts.
    postgres.WithEnvironment("POSTGRES_DB", "keycloakdb");
    keycloak
        .WithEnvironment("KC_DB", "postgres")
        .WithEnvironment("KC_DB_URL_HOST", postgres.Resource.Name)
        .WithEnvironment("KC_DB_URL_DATABASE", "keycloakdb")
        .WithEnvironment("KC_DB_USERNAME", "postgres")
        .WithEnvironment("KC_DB_PASSWORD", postgres.Resource.PasswordParameter)
        // Plain HTTP inside the machine's Docker network; the address in each link comes from what Caddy and the web
        // app forward (KC_PROXY_HEADERS), so nothing fixes a host name here.
        .WithEnvironment("KC_HTTP_ENABLED", "true")
        .WithEnvironment("KC_HOSTNAME_STRICT", "false")
        .WaitFor(postgres);
    webApp.WithEnvironment("WebApp__PublicUrl", publicUrl);
    identityApi
        .WithEnvironment("Keycloak__WebPublicUrl", publicUrl)
        .WithEnvironment("Payments__ReturnUrl", ReferenceExpression.Create($"https://{publicHost}/settings/plan"))
        // The flags folder flagd reads, so the flags page's overrides reach it.
        .PublishAsDockerComposeService((_, service) => service.Volumes.Add(new()
        {
            Name = "flags", Type = "bind", Source = "${FLAGD_BINDMOUNT_0}", Target = "/flags", ReadOnly = false
        }));
    notificationsApi.WithEnvironment("Digest__BaseUrl", publicUrl);

    // Who runs the site and how to reach them: the privacy policy and terms show both.
    webApp
        .WithEnvironment("Branding__Operator", builder.AddParameter("operator-name"))
        .WithEnvironment("Branding__ContactEmail", builder.AddParameter("contact-email"));

    // Every service: Keycloak is reached by plain HTTP on the private network, and a token someone got through the
    // site names the public address as its issuer.
    foreach (var project in builder.Resources.OfType<ProjectResource>().ToList())
    {
        builder.CreateResourceBuilder(project)
            .WithEnvironment("Identity__RequireHttpsMetadata", "false")
            .WithEnvironment("Identity__PublicUrl", publicUrl);
    }

    compose.ConfigureComposeFile(file =>
    {
        foreach (var (name, service) in file.Services)
        {
            // Back up after a crash or a reboot; start order isn't guaranteed, so a service that starts before what it
            // needs fails and is started again.
            service.Restart = "unless-stopped";
            // The Aspire dashboard (logs, traces, metrics) is for whoever runs the machine: on its loopback address
            // only, to be reached through an SSH tunnel.
            if (name.EndsWith("-dashboard", StringComparison.Ordinal))
            {
                service.Ports = ["127.0.0.1:18888:18888"];
            }
        }
    });
}
else
{
    // Running locally, the web app and the gateway are opened directly.
    webApp.WithExternalHttpEndpoints();
    mobileBff.WithExternalHttpEndpoints();
    webApp
        .WithEnvironment("Branding__Operator", builder.Configuration["Branding:Operator"] ?? "")
        .WithEnvironment("Branding__ContactEmail", builder.Configuration["Branding:ContactEmail"] ?? "");
}

builder.Build().Run();
