---
name: codeatout-commands
description: Designs validated write operations and command messages without assuming a specific messaging framework. Use when adding or changing a state-changing application operation.
---

# Commands

Apply `codeatout-nservicebus` when a command is sent through an existing
NServiceBus endpoint. This skill defines the application operation; the
messaging skill defines endpoint delivery, Outbox, and recoverability.

## Discover first

Identify the write boundary, transaction or consistency boundary, execution
mode, existing result/error contract, validation path, authorization model,
idempotency mechanism, audit requirements, and caller correlation context.
Confirm whether the operation is handled synchronously, in-process through a
mediator, or durably through a message transport before choosing its contract.

## Principles

- Model the intent, required data, caller context, and idempotency semantics
  explicitly.
- Validate untrusted input before state changes and enforce authorization at the
  resource boundary.
- Keep commands immutable where the language and repository conventions allow.
- Define expected success, validation failure, conflict, retry, and
  cancellation behaviour.
- Keep the transport contract, application command, persistence document, and
  HTTP request model separate when the repository distinguishes those
  boundaries. Map explicitly rather than leaking transport or storage fields
  into a command.
- Propagate cancellation and existing correlation context. Do not start
  detached writes that can outlive the request, handler, or message delivery.
- Use the repository's existing result pattern, whether that is an exception
  policy, nullable result, typed outcome, or `ProblemDetails` mapping. Map
  known expected failures deliberately; do not catch arbitrary programming
  failures merely to return a success-shaped result.
- For at-least-once delivery, associate a stable idempotency key or business
  identity with the effect. Persist or otherwise durably enforce deduplication
  when repeating the write would be unsafe.
- Avoid embedding secrets, raw personal data, or unbounded payloads in
  messages unless explicitly required and protected.

## Validation and execution

Keep command validation independent from execution. Use an existing validation
pipeline where the repository has one, or invoke the established validator at
the application boundary. Do not make a command handler responsible for
reimplementing HTTP model binding or transport deserialization validation.

If the repository already uses FluentValidation, a command validator can
express only the command's invariant inputs:

```csharp
public sealed record ChangeItemStatusCommand(
    string ItemId,
    ItemStatus Status,
    string IdempotencyKey);

public sealed class ChangeItemStatusValidator
    : AbstractValidator<ChangeItemStatusCommand>
{
    public ChangeItemStatusValidator()
    {
        RuleFor(command => command.ItemId).NotEmpty();
        RuleFor(command => command.Status).IsInEnum();
        RuleFor(command => command.IdempotencyKey).NotEmpty();
    }
}
```

The validator is illustrative only; do not add FluentValidation solely to
follow this pattern. Use the repository's supported validation mechanism.

Keep handler orchestration focused on authorization, idempotency, domain work,
and persistence. The handler should return or raise the established outcome
for duplicate, conflict, forbidden, validation, and unexpected cases, while
the transport boundary maps it to HTTP, a reply, or error handling:

```csharp
public sealed class ChangeItemStatusHandler(
    IItemWriter items,
    IItemAuthorizer authorizer)
{
    public async Task<ChangeResult> HandleAsync(
        ChangeItemStatusCommand command,
        CancellationToken cancellationToken)
    {
        await authorizer.DemandChangeAsync(command.ItemId, cancellationToken);

        return await items.ChangeStatusOnceAsync(
            command.ItemId,
            command.Status,
            command.IdempotencyKey,
            cancellationToken);
    }
}
```

## Validate

Test accepted input, invalid input, authorization denial, duplicate delivery,
conflict, cancellation, and observable state changes. Where a handler
publishes follow-up work, test that it occurs only after the relevant durable
state transition and remains safe on retry.

Create or update these focused tests in the same change as every command,
validator, handler, or command-mapping change. Do not treat implementation
tests as optional because the operation is asynchronous or dispatched through
a message transport.
