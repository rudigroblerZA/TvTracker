namespace Recommendations.Data;

public class Dismissal
{
    public required string UserId { get; set; }
    public Guid ShowId { get; set; }
    public DateTime DismissedAtUtc { get; set; }
}
