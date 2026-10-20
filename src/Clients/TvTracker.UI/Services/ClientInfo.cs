namespace TvTracker.UI.Services;

/// <summary>
/// The browser's IP address as the web app saw it, set once per circuit (see <c>Routes</c>). API calls send it as
/// X-Forwarded-For, so services can tell anonymous web visitors apart (Catalog.API's search rate limit).
/// Null in the mobile app, whose gateway adds the header itself.
/// </summary>
public sealed class ClientInfo
{
    public string? Address { get; set; }
}
