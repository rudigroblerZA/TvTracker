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
    public const string Nanogenres = "nanogenres";
    public const string DnaTwins = "dna-twins";
    public const string GetsGood = "gets-good";
    public const string CreatorFingerprint = "creator-fingerprint";
    public const string TimeMachine = "time-machine";
    public const string Endings = "endings";
    public const string Refresher = "refresher";
    public const string CatchUp = "catch-up";
    public const string WorthGoingBack = "worth-going-back";
    public const string BacklogTriage = "backlog-triage";
    public const string TimeOff = "time-off";
    public const string SpoilerShield = "spoiler-shield";
    public const string Social = "social";
    public const string TonightTogether = "tonight-together";
    public const string CommunityInsights = "community-insights";
    public const string Corrections = "corrections";
    public const string KnownFaces = "known-faces";
    public const string HolidayEpisodes = "holiday-episodes";
    public const string Research = "research";
    public const string Ask = "ask";
    public const string SeasonVerdict = "season-verdict";
    public const string CharacterTrail = "character-trail";
    public const string EpisodeNotes = "episode-notes";
    public const string WatchNow = "watch-now";
    public const string CrossoverAlerts = "crossover-alerts";
    public const string WeeklyDigest = "weekly-digest";

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
