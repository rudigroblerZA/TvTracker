using System.Security.Claims;
using OpenFeature.Model;

namespace TvTracker;

/// <summary>
/// The feature flags, by their keys in flagd (src/TvTracker.AppHost/Flags/flagd.json). Shared by the services and the
/// UI (TvTracker.UI links this file), so both sides gate a feature by the same key. Every flag is a boolean whose
/// default is <see cref="DefaultValue"/>.
/// </summary>
public static class FeatureFlags
{
    public const string Blend = "blend";
    public const string Universes = "universes";
    public const string EssentialCut = "essential-cut";
    public const string TonightPlan = "tonight-plan";
    public const string ReunionRadar = "reunion-radar";
    public const string ShowDna = "show-dna";
    public const string Forecasts = "forecasts";

    /// <summary>
    /// What a flag evaluates to when flagd is down, unreachable or doesn't know the flag: on. A flag server outage
    /// shouldn't take features away; turning one off is a deliberate change in flagd.json.
    /// </summary>
    public const bool DefaultValue = true;

    /// <summary>
    /// The evaluation context for a user, so flags can target them: <c>targetingKey</c> is the Keycloak user id
    /// (also what fractional rollouts hash), and <c>email</c> is included when known. Signed out, it's empty.
    /// </summary>
    public static EvaluationContext ContextFor(ClaimsPrincipal? user)
    {
        var context = EvaluationContext.Builder();
        if (user?.Identity?.IsAuthenticated == true)
        {
            if ((user.FindFirst("sub") ?? user.FindFirst(ClaimTypes.NameIdentifier))?.Value is { } id)
            {
                context.SetTargetingKey(id);
            }
            if ((user.FindFirst("email") ?? user.FindFirst(ClaimTypes.Email))?.Value is { } email)
            {
                context.Set("email", email);
            }
        }
        return context.Build();
    }
}
