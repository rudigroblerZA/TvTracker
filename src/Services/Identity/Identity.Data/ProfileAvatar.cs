namespace Identity.Data;

/// <summary>
/// A picture a user uploaded for their profile: small (the client shrinks it to a square first), kept here in the
/// database so it is backed up and deleted with everything else of theirs.
/// </summary>
public class ProfileAvatar
{
    public required string UserId { get; set; }

    /// <summary>
    /// The kind of picture ("image/jpeg", "image/png" or "image/webp"), as <see cref="ContentTypeOf"/> found from the bytes on upload.
    /// </summary>
    public required string ContentType { get; set; }

    public required byte[] Bytes { get; set; }

    /// <summary>
    /// When it was uploaded; what a browser's copy is checked against.
    /// </summary>
    public DateTime UpdatedAtUtc { get; set; }

    /// <summary>
    /// The most an uploaded picture may weigh. A 256-pixel square JPEG is a tenth of this.
    /// </summary>
    public const int MaxBytes = 256 * 1024;

    /// <summary>
    /// The kinds of picture taken, by how their files begin; null for anything else (SVG among them, which can carry script).
    /// </summary>
    public static string? ContentTypeOf(ReadOnlySpan<byte> bytes) =>
        bytes.StartsWith<byte>([0xFF, 0xD8, 0xFF]) ? "image/jpeg"
        : bytes.StartsWith<byte>([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]) ? "image/png"
        : bytes.Length >= 12 && bytes[..4].SequenceEqual("RIFF"u8) && bytes.Slice(8, 4).SequenceEqual("WEBP"u8) ? "image/webp"
        : null;
}
