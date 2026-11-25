using TvTracker.EventBus;

namespace TvTracker.IntegrationEvents;

// Published by Financials.API, which sells the plans. Identity.API owns each member's plan and applies these.

/// <summary>
/// A subscription started, or came back after ending: the member has paid for <paramref name="Plan"/>. Not published at
/// each renewal.
/// </summary>
public record SubscriptionStartedIntegrationEvent(string UserId, string Plan, DateTimeOffset StartedAtUtc) : IntegrationEvent;

/// <summary>
/// A subscription ended (cancelled, lapsed or stopped at the provider) and nothing else of the member's is running: they
/// no longer pay for <paramref name="Plan"/>.
/// </summary>
public record SubscriptionEndedIntegrationEvent(string UserId, string Plan, DateTimeOffset EndedAtUtc) : IntegrationEvent;
