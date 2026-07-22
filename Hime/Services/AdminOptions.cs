namespace Hime.Services;

/// <summary>
/// Restricts non-AI diagnostic commands to explicitly trusted QQ accounts.
/// The commands intentionally expose only read-only, non-secret runtime facts.
/// </summary>
public sealed class AdminOptions
{
    public bool Enabled { get; set; } = true;

    public List<long> AllowedUserIds { get; set; } = [];

    /// <summary>Administrative commands are never answered in a group.</summary>
    public bool PrivateChatOnly { get; set; } = true;

    /// <summary>
    /// Relative directories whose text files may be read through /admin file.
    /// Configuration, databases, caches and any path outside the app directory are excluded.
    /// </summary>
    public List<string> AllowedReadDirectories { get; set; } =
    [
        "personas"
    ];

    public int MaxFileReadCharacters { get; set; } = 6000;
}
