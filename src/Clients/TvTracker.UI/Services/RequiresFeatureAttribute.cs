namespace TvTracker.UI.Services;

/// <summary>
/// Marks a page as part of a flagged feature: while the flag is off, the router shows "not available" instead, and
/// the page never loads (so it doesn't call an API that answers 404).
/// </summary>
/// <param name="flag">One of <see cref="FeatureFlags"/>.</param>
[AttributeUsage(AttributeTargets.Class)]
public sealed class RequiresFeatureAttribute(string flag) : Attribute
{
    public string Flag => flag;
}
