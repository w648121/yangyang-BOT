using LiteDB;

namespace Hime.Data.Models;

/// <summary>按群和图片内容哈希统计的表情包记录。</summary>
public sealed class GroupStickerRecord
{
    [BsonId]
    public string Id { get; set; } = string.Empty;

    public long GroupId { get; set; }

    public string Sha256 { get; set; } = string.Empty;

    public int Occurrences { get; set; }

    public string SourcePath { get; set; } = string.Empty;

    public string? CollectedPath { get; set; }

    public string Emotion { get; set; } = "neutral";

    /// <summary>多次出现时累计本地情绪判断，以多数票稳定分类。</summary>
    public Dictionary<string, int> EmotionVotes { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public string? LastAnalysisSource { get; set; }

    public double? LastVisualConfidence { get; set; }

    /// <summary>Local-only visual metadata; it contains no OCR or cloud-generated image description.</summary>
    public string? LastLocalVisualDescription { get; set; }

    /// <summary>Safe, locally inferred anime-expression tags such as smile or crying.</summary>
    public List<string> LastSemanticTags { get; set; } = [];

    /// <summary>Independent multi-label emotion scores; values do not need to sum to one.</summary>
    public Dictionary<string, double> EmotionScores { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Conversation-use intents such as comforting, teasing, friendly, or calm.</summary>
    public List<string> IntentTags { get; set; } = [];

    public DateTime FirstSeenAt { get; set; }

    public DateTime LastSeenAt { get; set; }
}
