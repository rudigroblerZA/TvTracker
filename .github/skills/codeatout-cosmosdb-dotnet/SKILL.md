---
name: codeatout-cosmosdb-dotnet
description: Implements Azure Cosmos DB for NoSQL persistence in .NET with consistent configuration, serialization, document contracts, repositories, and query handling. Use when adding or changing Cosmos DB SDK integration in a .NET service.
---

# Azure Cosmos DB for NoSQL in .NET

Apply `codeatout-cosmosdb-data-modeling` first for container, partition-key,
indexing, lifecycle, and access-pattern decisions. This skill applies those
decisions consistently in a .NET service; it does not prescribe a particular
internal library or architecture.

## Discover first

Inspect the target framework, installed Cosmos SDK and identity packages,
existing configuration binding, dependency lifetimes, JSON serializer options,
document base types, repository and query abstractions, container definitions,
and tests. Identify the persisted JSON contract of existing documents before
changing property names, enum representation, null handling, discriminator
values, IDs, partition keys, or audit fields.

Use the repository's package-management workflow and current Microsoft Cosmos
SDK documentation. Do not introduce a competing repository abstraction,
serialization library, or dependency-injection pattern incidentally.

## Organisation Cosmos libraries

When the consuming service already uses the organisation-provided Cosmos
libraries, extend them rather than bypassing their registration, serialization,
repository, query, audit, or MediatR integration:

| Library | Purpose |
| --- | --- |
| `Microsoft.Azure.Cosmos` | Official Azure Cosmos DB for NoSQL .NET SDK. |
| `Stratos.Core.Cosmos` | Standard configuration, client/session registration, document and repository abstractions. |
| `Stratos.Core.Cosmos.MediatR` | MediatR integration for the standard Cosmos abstractions. |
| `Stratos.Core.Serialization.Converters` | Shared converters, including strongly typed dictionary serialization. |

Inspect the installed package versions and their existing usage before relying
on a wrapper API. Do not add these packages to a service that uses another
approved data-access stack without an explicit architectural decision.

## Configuration and registration

- Bind endpoint and database settings from a validated configuration section;
  provide non-secret settings through deployment configuration. Keep connection
  strings, keys, certificates, and tokens out of source, appsettings files,
  logs, and exception messages.
- Prefer the platform's supported workload or managed identity where available.
  Use an approved secret store only when an identity-based option is not
  possible.
- Configure the SDK client, connection mode, retry, timeout, diagnostics, and
  serialization centrally. Reuse the thread-safe client/session for its
  supported lifetime rather than constructing one per operation.
- Maintain one intentional set of JSON options for persisted documents.
  Explicitly configure enum, custom value-object, dictionary, and polymorphic
  handling when the document contract requires it. Add backward-compatible
  readers and a migration plan before changing existing JSON.

### Registration and serialization example

Use `Stratos.Core.Cosmos` where it is already the approved integration. Its
`ConfigureCosmos` registration centralizes the SDK client/session and accepts
the SDK's serializer options. The example retains CLR property names in the
stored JSON and uses converters needed by the consuming document model:

```csharp
public static class CosmosServiceCollectionExtensions
{
    public static IServiceCollection AddApplicationCosmos(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        CosmosConfiguration cosmosConfiguration = configuration
            .GetRequiredSection("Cosmos")
            .Get<CosmosConfiguration>()
            ?? throw new InvalidOperationException("The Cosmos configuration section is invalid.");

        services.AddSingleton(cosmosConfiguration);
        services.ConfigureCosmos(cosmosConfiguration, options =>
        {
            options.ConnectionMode = cosmosConfiguration.ConnectionMode
                ?? ConnectionMode.Gateway;
            options.UseSystemTextJsonSerializerWithOptions = new JsonSerializerOptions
            {
                Converters =
                {
                    new JsonStringEnumConverter(),
                    new StronglyTypedDictionaryConverterFactory()
                }
            };
        });

        return services;
    }
}
```

`ConfigureCosmos`, `CosmosConfiguration`, and
`StronglyTypedDictionaryConverterFactory` come from the organisation libraries
listed above. Add a compatibility type resolver only when existing documents
require it, and test both historical and new JSON payloads.

For a service that deliberately uses only `Microsoft.Azure.Cosmos`, create one
small `CosmosSerializer` adapter and register a singleton `CosmosClient` at
the composition root. Do not create an SDK client for each operation:

```csharp
public sealed class SystemTextJsonCosmosSerializer(JsonSerializerOptions options)
    : CosmosSerializer
{
    public override T FromStream<T>(Stream stream)
    {
        using (stream)
        {
            return JsonSerializer.Deserialize<T>(stream, options)
                ?? throw new JsonException("Cosmos returned an empty document.");
        }
    }

    public override Stream ToStream<T>(T input)
        => new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(input, options));
}
```

### JSON property-name contract

Cosmos stores JSON exactly as serialized, and Cosmos SQL field references are
case-sensitive. The established convention is to preserve Pascal-case CLR
property names for ordinary application fields:

| C# property | Stored JSON | Query field |
| --- | --- | --- |
| `VerificationContextId` | `VerificationContextId` | `c.VerificationContextId` |
| `DocumentType` | `DocumentType` | `c.DocumentType` |
| `CreatedDate` | `CreatedDate` | `c.CreatedDate` |

Use `[JsonPropertyName]` only when the persisted name must differ. Cosmos
requires the item identifier field to be exactly `id`, and its per-item TTL
field is exactly `ttl`; these are valid explicit overrides. Do not configure a
global camel-case naming policy for an existing Pascal-case container: it
changes the stored contract and every query field. Treat such a change as a
data migration. When a new cosmos instance is created and there is no exisiting
standard, prefer camel-case for naming and properties.

## Containers and document contracts

- Keep container names and partition-key paths in a central, typed registry.
  Repositories and query handlers should reference it rather than repeat
  string literals.
- Model persisted documents separately from API contracts when their lifecycle,
  shape, or data-classification requirements differ. Use a shared base type
  only for genuinely universal storage fields, such as identity and audit
  metadata.
- Treat the JSON document shape as a durable public contract. Use explicit
  serialized property names only where necessary, such as Cosmos system
  properties, and preserve casing and field names for existing data.
- When a container stores multiple document shapes, use a stable discriminator
  and include it in every type-specific query. Define discriminator values in
  one place and do not derive them from refactorable CLR type names.
- Apply item TTL only when the container's TTL configuration, retention,
  deletion, restoration, and downstream implications are understood. Specify
  it using Cosmos's reserved TTL property name without leaking it into
  application-facing contracts.

### Container and document examples

Use typed metadata rather than repeated container and partition-key strings:

```csharp
public sealed record ContainerInfo
{
    public required string Name { get; init; }

    public required string PartitionKeyProperty { get; init; }

    public string PartitionKeyPath => $"/{PartitionKeyProperty}";
}

public static class ApplicationContainers
{
    public static ContainerInfo Verifications { get; } = new()
    {
        Name = "Verifications",
        PartitionKeyProperty = "VerificationContextId"
    };
}
```

For a new mixed-purpose container, explicitly stabilize the JSON property
names only for Cosmos system fields. Existing containers must retain their
established serialized names and casing instead.

```csharp
public static class DocumentTypes
{
    public const string State = "State";
    public const string Audit = "Audit";
}

public abstract class VerificationDocument
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    public required string VerificationContextId { get; init; }

    public DateTimeOffset? CreatedDate { get; set; }

    public string? CreatedBy { get; set; }

    public DateTimeOffset? ModifiedDate { get; set; }

    public string? ModifiedBy { get; set; }
}

public sealed class VerificationStateDocument : VerificationDocument
{
    public string DocumentType { get; } = DocumentTypes.State;

    [JsonPropertyName("ttl")]
    public int? Ttl { get; init; }
}
```

## Repository and query boundaries

- Give each repository a narrow, domain-oriented interface. Let it own
  container selection, document mapping, point-read partition keys, and
  consistent audit-field behaviour.
- Set creation audit fields only for new documents, then update modification
  fields on each write. Obtain the clock and actor from injectable abstractions
  so tests are deterministic; provide a safe system actor only for approved
  non-user workflows.
- Prefer point reads with `id` and partition key. For queries, use
  `QueryDefinition` parameters for every value, pass cancellation tokens, and
  constrain by partition key and discriminator whenever the access pattern
  permits it. Never concatenate untrusted values into Cosmos SQL.
- Keep pagination, continuation-token handling, request charge, and
  `CosmosException` translation explicit at the repository or query boundary.
  Do not return SDK response types from application-facing interfaces unless
  that coupling is intentional and documented.

### Repository and query examples

Keep the write boundary responsible for the partition key and audit fields.
The actor context should resolve an authenticated caller or an approved
background-work identity; it must not be populated from arbitrary request
input.

```csharp
public interface IActorContext
{
    string Name { get; }
}

public sealed class VerificationStateRepository(
    CosmosClient cosmosClient,
    CosmosOptions cosmosOptions,
    TimeProvider timeProvider,
    IActorContext actorContext)
{
    public async Task UpsertAsync(
        VerificationStateDocument document,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(document);

        DateTimeOffset now = timeProvider.GetUtcNow();
        document.CreatedDate ??= now;
        document.CreatedBy ??= actorContext.Name;
        document.ModifiedDate = now;
        document.ModifiedBy = actorContext.Name;

        Container container = cosmosClient.GetContainer(
            cosmosOptions.DatabaseName,
            ApplicationContainers.Verifications.Name);

        await container.UpsertItemAsync(
            document,
            new PartitionKey(document.VerificationContextId),
            cancellationToken: cancellationToken);
    }
}
```

Use parameterized SQL and an explicit partition key. Continue through every
page rather than assuming one response contains all matching documents:

```csharp
public static async Task<IReadOnlyList<VerificationStateDocument>> GetStatesAsync(
    Container container,
    string verificationContextId,
    CancellationToken cancellationToken)
{
    var query = new QueryDefinition(
        """
        SELECT * FROM c
        WHERE c.VerificationContextId = @verificationContextId
        AND c.DocumentType = @documentType
        """)
        .WithParameter("@verificationContextId", verificationContextId)
        .WithParameter("@documentType", DocumentTypes.State);

    FeedIterator<VerificationStateDocument> iterator = container.GetItemQueryIterator<VerificationStateDocument>(
        query,
        requestOptions: new QueryRequestOptions
        {
            PartitionKey = new PartitionKey(verificationContextId)
        });

    var documents = new List<VerificationStateDocument>();

    while (iterator.HasMoreResults)
    {
        FeedResponse<VerificationStateDocument> page = await iterator.ReadNextAsync(cancellationToken);
        documents.AddRange(page);
    }

    return documents;
}
```

## Operational safety and tests

- Log operation category, container, safe correlation identifiers, status, and
  request charge according to the repository's telemetry conventions. Do not
  log document bodies, sensitive identifiers, connection settings, or secrets.
- Test configuration validation, container and partition-key selection,
  serialization of representative persisted documents, discriminator filters,
  parameter binding, audit mapping, cancellation, pagination, and error
  translation. Use synthetic data and a controlled Cosmos test environment
  where integration behaviour must be verified.
- For changes to an existing document contract, test reads of prior document
  versions and perform a measured rollout with migration, dual-read, or
  backfill where required.

### Contract test example

Use a fixed clock and actor to verify audit mapping, and assert the persisted
JSON fields that must remain compatible:

```csharp
[Fact]
public void Serialize_StateDocument_UsesStableCosmosPropertyNames()
{
    var document = new VerificationStateDocument
    {
        Id = "state-1",
        VerificationContextId = "context-1",
        Ttl = 3_600
    };

    using JsonDocument json = JsonDocument.Parse(JsonSerializer.Serialize(document));

    json.RootElement.GetProperty("id").GetString().ShouldBe("state-1");
    json.RootElement.GetProperty("VerificationContextId").GetString().ShouldBe("context-1");
    json.RootElement.GetProperty("DocumentType").GetString().ShouldBe(DocumentTypes.State);
    json.RootElement.GetProperty("ttl").GetInt32().ShouldBe(3_600);
}
```
