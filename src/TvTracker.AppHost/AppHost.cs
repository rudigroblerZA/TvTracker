using Aspire.Hosting.Utils;

var builder = DistributedApplication.CreateBuilder(args);

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

// DbGate (Community Toolkit): a web UI for browsing and querying every database, already connected. Open it from the
// dashboard's "dbgate" resource.
postgres.WithDbGate(dbGate => dbGate.WithLifetime(ContainerLifetime.Persistent));

// One database per service: no service reads another's tables.
var identityDb = postgres.AddDatabase("identitydb");
var catalogDb = postgres.AddDatabase("catalogdb");
var trackingDb = postgres.AddDatabase("trackingdb");
var ratingsDb = postgres.AddDatabase("ratingsdb");
var recommendationsDb = postgres.AddDatabase("recommendationsdb");
var notificationsDb = postgres.AddDatabase("notificationsdb");
var analyticsDb = postgres.AddDatabase("analyticsdb");

var redis = builder.AddRedis("redis");

var eventBus = builder.AddRabbitMQ("eventbus")
    .WithManagementPlugin()
    .WithLifetime(ContainerLifetime.Persistent);

// Keycloak: sign-up, sign-in, password reset, external logins. The "tvtracker" realm is imported from ./Realms
// on first start. It listens on a fixed port (8080) so token issuers stay the same for the web app, the services
// and the Android emulator (10.0.2.2:8080). Admin console: http://localhost:8080 (admin / the parameter below).
var keycloakAdminPassword = builder.AddParameter("keycloak-admin-password", "admin", secret: true);

var keycloak = builder.AddContainer("keycloak", "quay.io/keycloak/keycloak", "26.4")
    .WithArgs("start-dev", "--import-realm")
    .WithEnvironment("KC_BOOTSTRAP_ADMIN_USERNAME", "admin")
    .WithEnvironment("KC_BOOTSTRAP_ADMIN_PASSWORD", keycloakAdminPassword)
    .WithEnvironment("KC_HEALTH_ENABLED", "true")
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
var flagd = builder.AddFlagd("flagd")
    .WithBindFileSync("./Flags/")
    .WithLifetime(ContainerLifetime.Persistent);

// Development secrets; they match Realms/tvtracker-realm.json. Change both for any shared environment.
const string WebClientSecret = "tvtracker-web-dev-secret";
const string IdentityAdminClientSecret = "tvtracker-identity-admin-dev-secret";

// ---------------------------------------------------------------------------
// Services
// ---------------------------------------------------------------------------

var identityApi = builder.AddProject<Projects.Identity_API>("identity-api")
    .WithReference(identityDb).WaitFor(identityDb)
    .WithReference(eventBus).WaitFor(eventBus)
    .WithEnvironment("Identity__Url", keycloakUrl)
    .WithEnvironment("Identity__AdminClientSecret", IdentityAdminClientSecret)
    .WaitFor(keycloak)
    .WithHttpHealthCheck("/health");

var catalogApi = builder.AddProject<Projects.Catalog_API>("catalog-api")
    .WithReference(catalogDb).WaitFor(catalogDb)
    .WithReference(flagd)
    .WithReference(redis).WaitFor(redis)
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
    .WithEnvironment("Identity__Url", keycloakUrl)
    .WithHttpHealthCheck("/health");

var recommendationsApi = builder.AddProject<Projects.Recommendations_API>("recommendations-api")
    .WithReference(recommendationsDb).WaitFor(recommendationsDb)
    .WithReference(eventBus).WaitFor(eventBus)
    .WithReference(redis).WaitFor(redis)
    .WithEnvironment("Identity__Url", keycloakUrl)
    .WithHttpHealthCheck("/health");

var notificationsApi = builder.AddProject<Projects.Notifications_API>("notifications-api")
    .WithReference(notificationsDb).WaitFor(notificationsDb)
    .WithReference(eventBus).WaitFor(eventBus)
    .WithEnvironment("Identity__Url", keycloakUrl)
    .WithHttpHealthCheck("/health");

// Analytics.API owns analyticsdb; the worker consumes catalog and rating events and computes insights hourly.
var analyticsApi = builder.AddProject<Projects.Analytics_API>("analytics-api")
    .WithReference(analyticsDb).WaitFor(analyticsDb)
    .WithReference(flagd)
    .WithEnvironment("Identity__Url", keycloakUrl)
    .WithHttpHealthCheck("/health");

var analyticsWorker = builder.AddProject<Projects.Analytics_Worker>("analytics-worker")
    .WithReference(analyticsDb)
    .WithReference(eventBus).WaitFor(eventBus)
    .WaitFor(analyticsApi)
    .WithHttpHealthCheck("/health");

// The data export collects each service's data for the user.
identityApi
    .WithReference(trackingApi)
    .WithReference(ratingsApi)
    .WithReference(recommendationsApi)
    .WithReference(notificationsApi);

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
    .WithHttpHealthCheck("/health");

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

// Docker Model Runner serves the default embedding model (dmr/nomic-embed-text-v1.5). The AppHost runs it as a
// container (CPU; the model is kept in a volume), unless ModelRunner:Url points at one already running, such as
// Docker Desktop's (http://localhost:12434, which can use the GPU). The sync worker pulls the model on first start;
// until it's ready, search matches titles only. Not started for an in-process model: the built-in server models
// are the dmr/ ones, and a custom one says Source: Server.
var embeddingModelId = builder.Configuration["Embeddings:Model"] ?? "";
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
}

// Search asks the sync worker to import matches from the provider. Until the worker is up, search uses what the catalog has.
catalogApi.WithReference(catalogSync);

// The ops dashboard (/ops on Catalog.API) includes Analytics' stats.
catalogApi.WithReference(analyticsApi);

// On first start (no shows yet), Analytics asks the sync worker to replay the catalog.
analyticsWorker.WithReference(catalogSync);

// ---------------------------------------------------------------------------
// Gateways and clients
// ---------------------------------------------------------------------------

// The MAUI app talks to the services only through this gateway (http://localhost:5110, or 10.0.2.2:5110 from the Android emulator).
builder.AddProject<Projects.Mobile_Bff>("mobile-bff")
    .WithExternalHttpEndpoints()
    .WithReference(flagd.GetEndpoint("ofrep"))
    .WithReference(identityApi)
    .WithReference(catalogApi)
    .WithReference(trackingApi)
    .WithReference(ratingsApi)
    .WithReference(recommendationsApi)
    .WithReference(notificationsApi)
    .WithReference(analyticsApi);

builder.AddProject<Projects.WebApp>("webapp")
    .WithExternalHttpEndpoints()
    .WithReference(flagd)
    .WithReference(redis).WaitFor(redis)
    .WithEnvironment("Identity__Url", keycloakUrl)
    .WithEnvironment("Identity__WebClientSecret", WebClientSecret)
    .WaitFor(keycloak)
    .WithReference(identityApi)
    .WithReference(catalogApi).WaitFor(catalogApi)
    .WithReference(trackingApi).WaitFor(trackingApi)
    .WithReference(ratingsApi)
    .WithReference(recommendationsApi)
    .WithReference(notificationsApi)
    .WithReference(analyticsApi);

builder.Build().Run();
