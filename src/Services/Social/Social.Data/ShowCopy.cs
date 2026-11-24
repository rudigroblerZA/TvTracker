namespace Social.Data;

/// <summary>
/// The catalog's show title and poster, for rendering the feed.
/// </summary>
public class ShowCopy
{
    public Guid ShowId { get; set; }
    public required string Title { get; set; }
    public string? PosterUrl { get; set; }
}
