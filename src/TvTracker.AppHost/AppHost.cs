using System.Net.Http.Json;
using Aspire.Hosting.Utils;

var builder = DistributedApplication.CreateBuilder(args);

// White label: the app's name in the UI, page titles and the text services write ("★ 4.5 from 12 <name> users").
// Passed to the web app and the services that put it in text; the phone has its own (MobileSettings.AppName).
var appName = builder.Configuration["Branding:AppName"] is { Length: > 0 } configuredName ? configuredName : "TV Tracker";

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
var researchDb = postgres.AddDatabase("researchdb");
var recommendationsDb = postgres.AddDatabase("recommendationsdb");
var notificationsDb = postgres.AddDatabase("notificationsdb");
var analyticsDb = postgres.AddDatabase("analyticsdb");
var socialDb = postgres.AddDatabase("socialdb");

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
// Flags/overrides.json is a second source, written by the flags page (Identity.API); for a key in both, flagd uses the
// later source. It isn't checked in, so it's created empty when missing.
var flagsDirectory = Path.Combine(builder.AppHostDirectory, "Flags");
var flagOverrides = Path.Combine(flagsDirectory, "overrides.json");
if (!File.Exists(flagOverrides))
{
    File.WriteAllText(flagOverrides, "{ \"flags\": {} }\n");
}
var flagd = builder.AddFlagd("flagd")
    .WithBindFileSync("./Flags/")
    .WithArgs("--uri", "file:./flags/overrides.json")
    .WithLifetime(ContainerLifetime.Persistent);

// Development secrets; they match Realms/tvtracker-realm.json. Change both for any shared environment.
const string WebClientSecret = "tvtracker-web-dev-secret";
const string IdentityAdminClientSecret = "tvtracker-identity-admin-dev-secret";
const string McpClientSecret = "tvtracker-mcp-dev-secret";
const string AppAdminPassword = "admin";

// ---------------------------------------------------------------------------
// Services
// ---------------------------------------------------------------------------

var identityApi = builder.AddProject<Projects.Identity_API>("identity-api")
    .WithReference(identityDb).WaitFor(identityDb)
    .WithReference(flagd)
    .WithReference(eventBus).WaitFor(eventBus)
    .WithEnvironment("Identity__Url", keycloakUrl)
    .WithEnvironment("Identity__AdminClientSecret", IdentityAdminClientSecret)
    .WithEnvironment("Flags__Directory", flagsDirectory)
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
if (builder.Configuration["Research:TmdbApiKey"] is { Length: > 0 } tmdbKey)
{
    researchApi.WithEnvironment("Research__TmdbApiKey", tmdbKey);
}

// Internal MCP server (http://localhost:5112/mcp): read-only tools over the catalog and Analytics, for questions like
// "landman insights" from an MCP client. It signs in to Analytics as its own Keycloak client, which it creates in
// development if the realm predates it. Development only unless Mcp:ApiKey is set.
var mcpServer = builder.AddProject<Projects.Mcp_Server>("mcp-server")
    .WithReference(catalogApi)
    .WithReference(analyticsApi)
    .WithEnvironment("Identity__Url", keycloakUrl)
    .WithReference(researchApi)
    .WithEnvironment("Mcp__ClientSecret", McpClientSecret)
    .WithEnvironment("Keycloak__AdminUsername", "admin")
    .WithEnvironment("Keycloak__AdminPassword", keycloakAdminPassword)
    .WaitFor(keycloak)
    .WithHttpHealthCheck("/health");

// MCP gateway (http://localhost:5113/mcp): one endpoint, with an API key, in front of the app's MCP servers
// (Gateway:Servers:{name}:Url, optionally BearerToken and a Tools allow-list), logging every call. Open WebUI uses it.
// More servers can be added under Gateway:Servers in this project's configuration.
const string McpGatewayKey = "tvtracker-gateway-dev-key";
var mcpGateway = builder.AddProject<Projects.Mcp_Gateway>("mcp-gateway")
    .WithEnvironment("Gateway__ApiKey", McpGatewayKey)
    .WithEnvironment("Gateway__Servers__tvtracker__Url", ReferenceExpression.Create($"{mcpServer.GetEndpoint("http")}/mcp"))
    .WaitFor(mcpServer)
    .WithHttpHealthCheck("/health");
// Ask the app: questions about shows, answered by Model Runner's chat model calling the app's MCP tools through the
// gateway (its endpoint and model are set with Model Runner's below). Signed in, behind the "ask" flag.
var assistantApi = builder.AddProject<Projects.Assistant_API>("assistant-api")
    .WithReference(flagd).WithReference(identityApi)
    .WithEnvironment("Identity__Url", keycloakUrl)
    .WithEnvironment("Assistant__GatewayUrl", ReferenceExpression.Create($"{mcpGateway.GetEndpoint("http")}/mcp"))
    .WithEnvironment("Assistant__GatewayKey", McpGatewayKey)
    .WaitFor(mcpGateway)
    .WithHttpHealthCheck("/health");

foreach (var server in builder.Configuration.GetSection("Gateway:Servers").GetChildren())
{
    foreach (var setting in server.GetChildren().Where(c => c.Value is not null))
    {
        mcpGateway.WithEnvironment($"Gateway__Servers__{server.Key}__{setting.Key}", setting.Value);
    }
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
}
identityApi
    .WithEnvironment("Admin__Account__Username", "admin")
    .WithEnvironment("Admin__Account__Password", AppAdminPassword);

identityApi
    .WithReference(trackingApi)
    .WithReference(ratingsApi)
    .WithReference(recommendationsApi)
    .WithReference(notificationsApi)
    .WithReference(socialApi)
    .WithReference(analyticsApi);

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

// Docker Model Runner serves the default embedding model (dmr/qwen3-embedding-0.6b). The AppHost runs it as a
// container (CPU; the model is kept in a volume), unless ModelRunner:Url points at one already running, such as
// Docker Desktop's (http://localhost:12434, which can use the GPU). The sync worker pulls the model on first start;
// until it's ready, search matches titles only. Not started for an in-process model: the built-in server models
// are the dmr/ ones, and a custom one says Source: Server.
var embeddingModelId = builder.Configuration["Embeddings:Model"] ?? "";
// The model's name on Model Runner, for Open WebUI's document search: a configured model's own, else the built-in one's.
var servedEmbeddingModel = builder.Configuration[$"Embeddings:Models:{embeddingModelId}:ServedModel"] is { Length: > 0 } configuredServed
    ? configuredServed
    : embeddingModelId == "dmr/nomic-embed-text-v1.5" ? "ai/nomic-embed-text-v1.5" : "ai/qwen3-embedding:0.6b";
// The chat model Open WebUI chats with, Research drafts insights with and the assistant answers with; pulled with the
// dashboard's command. The default is the smallest that calls tools reliably (qwen3 0.6B, ~400 MB, fine on a CPU).
var chatModel = builder.Configuration["ModelRunner:ChatModel"] is { Length: > 0 } configuredChat ? configuredChat : "ai/qwen3:0.6B-Q4_K_M";
researchApi.WithEnvironment("Research__ChatModel", chatModel);
assistantApi.WithEnvironment("Assistant__ChatModel", chatModel);
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
    assistantApi.WithEnvironment("Assistant__ChatEndpoint", endpoint);

    // Open WebUI runs in a container, where localhost is the container itself.
    var fromContainer = new UriBuilder(modelRunnerUrl);
    if (fromContainer.Host is "localhost" or "127.0.0.1")
    {
        fromContainer.Host = "host.docker.internal";
    }
    AddOpenWebUi(ReferenceExpression.Create($"{fromContainer.Uri.ToString().TrimEnd('/')}/engines/v1"), null);
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
    assistantApi.WithEnvironment("Assistant__ChatEndpoint", endpoint);

    // Model Runner serves only the embedding model until a chat model is pulled: this adds a "Pull <model>" command to
    // the model-runner resource on the dashboard (Model Runner's own pull API, which the sync worker also uses).
    modelRunner.WithHttpCommand("/models/create", $"Pull {chatModel}", commandName: "pull-chat-model",
        commandOptions: new HttpCommandOptions
        {
            Method = HttpMethod.Post,
            Description = $"Pulls {chatModel} for chatting in Open WebUI, drafting research insights and answering Ask. Large models take minutes.",
            IconName = "ArrowDownload",
            PrepareRequest = context =>
            {
                context.Request.Content = JsonContent.Create(new { from = chatModel });
                return Task.CompletedTask;
            }
        });

    AddOpenWebUi(ReferenceExpression.Create($"{modelRunner.GetEndpoint("http")}/engines/v1"), modelRunner);
}

// Open WebUI: a web UI for Model Runner, to see the models it serves and chat with any chat model pulled into it
// ("open-webui" on the dashboard). Development only: sign-in is off, so anyone who can reach it can use it. Its
// document search embeds with Model Runner's embedding model too, so it downloads no model of its own at start.
// The image is about 6 GB; ModelRunner:OpenWebUi = false leaves it out.
void AddOpenWebUi(ReferenceExpression modelRunnerApi, IResourceBuilder<ContainerResource>? modelRunner)
{
    if (string.Equals(builder.Configuration["ModelRunner:OpenWebUi"], "false", StringComparison.OrdinalIgnoreCase))
    {
        return;
    }

    var openWebUi = builder.AddContainer("open-webui", "ghcr.io/open-webui/open-webui", "v0.7.2")
        .WithHttpEndpoint(targetPort: 8080, name: "http")
        .WithVolume("tvtracker-open-webui", "/app/backend/data")
        .WithEnvironment("WEBUI_NAME", $"{appName} · Model Runner")
        .WithEnvironment("WEBUI_AUTH", "False")
        .WithEnvironment("ENABLE_OLLAMA_API", "False")
        .WithEnvironment("OPENAI_API_BASE_URL", modelRunnerApi)
        .WithEnvironment("OPENAI_API_KEY", "model-runner")
        .WithEnvironment("RAG_EMBEDDING_ENGINE", "openai")
        .WithEnvironment("RAG_OPENAI_API_BASE_URL", modelRunnerApi)
        .WithEnvironment("RAG_OPENAI_API_KEY", "model-runner")
        .WithEnvironment("RAG_EMBEDDING_MODEL", servedEmbeddingModel)
        .WithEnvironment("ENABLE_EVALUATION_ARENA_MODELS", "False")
        .WithEnvironment("ANONYMIZED_TELEMETRY", "False")
        // The app's MCP tools ("landman insights"), through the gateway, on for every chat's tool list. The AppHost owns
        // this config, so Open WebUI reads it from here on every start instead of keeping its first copy in its database.
        // Keep the closing braces apart: ReferenceExpression turns "}}" in its literal text into "}".
        .WithEnvironment("TOOL_SERVER_CONNECTIONS", ReferenceExpression.Create(
            $"[{{\"type\":\"mcp\",\"url\":\"{mcpGateway.GetEndpoint("http")}/mcp\",\"path\":\"\",\"auth_type\":\"bearer\",\"key\":\"{McpGatewayKey}\",\"config\":{{\"enable\":true}},\"info\":{{\"id\":\"tvtracker\",\"name\":\"{appName}\",\"description\":\"Shows, insights, discovery lists and premieres from the catalog and Analytics\"}} }}]"))
        .WithEnvironment("ENABLE_PERSISTENT_CONFIG", "False")
        .WithReference(mcpGateway).WaitFor(mcpGateway)
        .WithHttpHealthCheck("/health")
        .WithLifetime(ContainerLifetime.Persistent);
    if (modelRunner is not null)
    {
        openWebUi.WaitFor(modelRunner);
    }
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
builder.AddProject<Projects.Mobile_Bff>("mobile-bff")
    .WithExternalHttpEndpoints()
    .WithReference(flagd.GetEndpoint("ofrep"))
    .WithReference(identityApi)
    .WithReference(catalogApi)
    .WithReference(trackingApi)
    .WithReference(ratingsApi)
    .WithReference(recommendationsApi)
    .WithReference(notificationsApi)
    .WithReference(analyticsApi)
    .WithReference(socialApi)
    .WithReference(researchApi)
    .WithReference(assistantApi);

builder.AddProject<Projects.WebApp>("webapp")
    .WithEnvironment("Branding__AppName", appName)
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
    .WithReference(analyticsApi)
    .WithReference(socialApi)
    .WithReference(researchApi)
    .WithReference(assistantApi);

builder.Build().Run();
