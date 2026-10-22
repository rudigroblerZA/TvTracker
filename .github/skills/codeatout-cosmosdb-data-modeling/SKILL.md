---
name: codeatout-cosmosdb-data-modeling
description: Designs Azure Cosmos DB for NoSQL data models from access patterns, scale, consistency, and cost requirements. Use when creating or changing Cosmos DB containers, partition keys, indexing, denormalization, or data migration plans.
---

# Azure Cosmos DB for NoSQL Data Modeling

## Discover first

Confirm that Azure Cosmos DB for NoSQL is the intended API and inspect existing
containers, partition keys, indexing policies, consistency settings, SDK
usage, infrastructure definitions, data migration approach, and operational
telemetry. Use current Microsoft documentation for service limits, feature
availability, and pricing; do not rely on fixed RU, partition, or pricing
figures.

Capture each required read, write, query, batch, and change-feed access
pattern. For every pattern, identify its caller, required result shape,
partition-key knowledge, expected average and peak rate, latency objective,
result and document size, consistency requirement, and retention period.

## Model from access patterns

- Choose aggregate and container boundaries from co-access, transactional
  requirements, independent scaling, indexing, change-feed, backup, and
  ownership needs—not from a one-container-per-entity rule.
- Use a single document for bounded data read and updated atomically together.
  Use multiple documents in one logical partition for related, independently
  sized or updated data that requires transactional operations. Use separate
  containers when operational characteristics or ownership differ.
- Select a partition key that is present in high-value access patterns,
  distributes storage and request load, and avoids concentration from popular,
  low-cardinality, or time-based values. Evaluate hierarchical keys, time
  bucketing, or write sharding only when the access patterns and measured load
  justify their added query complexity.
- Prefer point reads with both `id` and partition key when retrieving a known
  item. Treat cross-partition queries as an explicit, measured trade-off.
- Denormalize small, stable, frequently read attributes only with a documented
  source of truth, update propagation, consistency expectation, and repair
  strategy.

## Indexing, integrity, and lifecycle

- Derive included, excluded, composite, spatial, full-text, vector, or other
  indexing choices from actual query predicates and sort requirements. Assess
  their write, storage, and query impact with representative data.
- Model uniqueness deliberately. Verify whether `id`, partition key, unique-key
  policy, transactional operation, or an application-level reservation meets
  the required scope and concurrency semantics.
- Use TTL only for data whose expiry, legal retention, restoration, and
  downstream processing implications are understood. Do not use TTL as a
  substitute for deletion, archival, or compliance design.
- Keep partition keys, identifiers, document examples, logs, and metrics free
  of secrets and unnecessary restricted or personal data. Use synthetic data
  in design documentation.

## Scale, consistency, and change

Document multi-region, write-region, failover, conflict-resolution,
consistency, availability, and recovery requirements. Align consistency choices
with each workflow's correctness and latency needs rather than applying one
level everywhere.

Treat a partition-key or container-boundary change as a data migration. Plan
the target container, backfill, dual-read or dual-write period where required,
verification, cutover, rollback, and retirement of the prior container.

## Deliver and validate

Produce a reviewable model that maps each access pattern to its container,
partition key, item shape, operation, index support, consistency, and expected
cost or RU evidence. Record alternatives, trade-offs, unresolved assumptions,
and ownership.

Validate the design with representative data and workload tests. Measure
request charge, latency, throttling, partition distribution, index impact, and
failure behaviour. Confirm that security, data retention, authorization,
observability, migration, and recovery requirements remain satisfied.
