namespace Hime.Services;

public enum SetuSourceMode
{
    Lolicon,
    Random
}

public sealed record SetuRequest(
    int Count,
    IReadOnlyList<string> Tags,
    SetuSourceMode Source,
    bool FromSemanticModel = false,
    bool RejectedUnsafe = false);

public sealed record SetuImage(
    string OriginalUrl,
    string Source,
    string? Title = null,
    string? Author = null,
    long? PixivId = null,
    IReadOnlyList<string>? Tags = null,
    bool VerifiedNonAi = false,
    bool VerifiedNonR18 = false);

public sealed record SetuFetchResult(
    IReadOnlyList<SetuImage> Images,
    string MatchMode,
    string? Error = null);
