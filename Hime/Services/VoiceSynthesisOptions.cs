namespace Hime.Services;

/// <summary>
/// 本地 TTS + RVC 两阶段语音合成配置。命令参数使用独立数组，避免经过 shell。
/// </summary>
public sealed class VoiceSynthesisOptions
{
    public bool Enabled { get; set; }

    /// <summary>RVC 模型根目录。相对路径会从进程目录及其父目录中查找。</summary>
    public string RvcRootDirectory { get; set; } = "..\\RVC";

    /// <summary>最终 WAV 缓存目录。</summary>
    public string CacheDirectory { get; set; } = "data\\voice-cache";

    public VoiceCacheOptions Cache { get; set; } = new();

    public VoiceWarmupOptions Warmup { get; set; } = new();

    public VoiceOutboxOptions Outbox { get; set; } = new();

    public string DefaultVoice { get; set; } = string.Empty;

    /// <summary>
    /// 为正常 AI 回复、群内自然接话和主动发言自动追加语音。
    /// 文本会先立即发送，语音在后台合成完成后作为独立 QQ 语音消息发送。
    /// </summary>
    public bool AutoReplyVoiceEnabled { get; set; }

    /// <summary>自动回复使用的声线；当前默认使用中文秧秧声线。</summary>
    public string AutoReplyVoice { get; set; } = string.Empty;

    /// <summary>Voice used by /voice compare-emotion. Empty means DefaultVoice.</summary>
    public string EmotionComparisonVoice { get; set; } = string.Empty;

    /// <summary>Stable public voice names mapped to configured profiles.</summary>
    public Dictionary<string, string> VoiceAliases { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Emotion-label aliases used when a profile has no exact reference clip.</summary>
    public Dictionary<string, string> EmotionAliases { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>双语文本优先朗读其中的简体中文行，避免中文音色误读日文。</summary>
    public bool AutoReplySpeakChineseOnly { get; set; } = true;

    /// <summary>自动语音后台队列上限；满载时优先保留直接对话，丢弃低优先级主动语音。</summary>
    public int AutoReplyQueueCapacity { get; set; } = 8;

    /// <summary>排队超过该时间的自动语音不再发送，避免语音与当前话题错位。</summary>
    public int AutoReplyMaxQueueAgeSeconds { get; set; } = 30;

    public int TimeoutSeconds { get; set; } = 120;

    public int MaxTextLength { get; set; } = 500;

    public VoiceCommandOptions BaseTts { get; set; } = new();

    public VoiceCommandOptions Rvc { get; set; } = new();

    /// <summary>GPT-SoVITS 本地 Web API 配置；未启用时不会影响既有 RVC 声线。</summary>
    public GptSoVitsOptions GptSoVits { get; set; } = new();

    /// <summary>IndexTTS2 本地 Web API 配置；与 GPT-SoVITS 互斥驻留显存。</summary>
    public IndexTtsOptions IndexTts { get; set; } = new();

    public Dictionary<string, RvcVoiceProfile> Voices { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);

    public bool IsValid() =>
        !string.IsNullOrWhiteSpace(DefaultVoice) &&
        !string.IsNullOrWhiteSpace(AutoReplyVoice) &&
        Voices.Count > 0 &&
        Voices.ContainsKey(DefaultVoice) &&
        Voices.ContainsKey(AutoReplyVoice) &&
        (string.IsNullOrWhiteSpace(EmotionComparisonVoice) || Voices.ContainsKey(EmotionComparisonVoice)) &&
        VoiceAliases.All(pair =>
            !string.IsNullOrWhiteSpace(pair.Key) &&
            !string.IsNullOrWhiteSpace(pair.Value) &&
            Voices.ContainsKey(pair.Value)) &&
        EmotionAliases.All(pair =>
            !string.IsNullOrWhiteSpace(pair.Key) &&
            !string.IsNullOrWhiteSpace(pair.Value)) &&
        IndexTts.FallbackEmotionVector.Count == 8 &&
        IndexTts.EmotionVectors.All(rule =>
            rule.Markers.Any(marker => !string.IsNullOrWhiteSpace(marker)) &&
            rule.Values.Count == 8 &&
            rule.Values.All(value => value is >= 0 and <= 1));
}

public sealed class VoiceCacheOptions
{
    public int MaximumMegabytes { get; set; } = 2048;
    public int RetentionDays { get; set; } = 30;
    public int MaintenanceIntervalMinutes { get; set; } = 60;
}

public sealed class VoiceWarmupOptions
{
    public bool Enabled { get; set; } = true;
    public string Voice { get; set; } = string.Empty;
    public string Text { get; set; } = "今天也请多关照。";
    public int DelaySeconds { get; set; } = 3;
}

public sealed class VoiceOutboxOptions
{
    public bool Enabled { get; set; } = true;
    public int RecoveryScanSeconds { get; set; } = 15;
    public int RecoveryMaximumAgeMinutes { get; set; } = 5;
    public int MaximumAttempts { get; set; } = 5;
    public int InitialRecoveryDelaySeconds { get; set; } = 10;
}

/// <summary>
/// 外部命令配置。支持的占位符见 <see cref="VoiceSynthesisService"/>。
/// </summary>
public sealed class VoiceCommandOptions
{
    public string ExecutablePath { get; set; } = string.Empty;

    public List<string> Arguments { get; set; } = [];

    public string? WorkingDirectory { get; set; }
}

public sealed class RvcVoiceProfile
{
    public bool Enabled { get; set; } = true;

    /// <summary>声线后端：rvc（默认）、gpt-sovits 或 index-tts2。</summary>
    public string Engine { get; set; } = "rvc";

    public string ModelPath { get; set; } = string.Empty;

    public string IndexPath { get; set; } = string.Empty;

    public string BaseTtsVoice { get; set; } = string.Empty;

    public int Pitch { get; set; }

    /// <summary>用于兼容不同 CLI 的附加占位符。</summary>
    public Dictionary<string, string> Parameters { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>仅 Engine=gpt-sovits 时使用。</summary>
    public GptSoVitsVoiceProfile GptSoVits { get; set; } = new();

    /// <summary>仅 Engine=index-tts2 时使用。</summary>
    public IndexTtsVoiceProfile IndexTts { get; set; } = new();
}

/// <summary>由官方 api_v2.py 提供的本地 GPT-SoVITS 服务配置。</summary>
public sealed class GptSoVitsOptions
{
    public bool Enabled { get; set; }

    public bool AutoStartLocalServer { get; set; } = true;

    public string BaseUrl { get; set; } = "http://127.0.0.1:9880";

    public string PythonExecutablePath { get; set; } = string.Empty;

    public string WorkingDirectory { get; set; } = string.Empty;

    public string ApiScriptPath { get; set; } = "api_v2.py";

    public string TtsConfigPath { get; set; } = string.Empty;

    public int StartupTimeoutSeconds { get; set; } = 120;

    public int RequestTimeoutSeconds { get; set; } = 300;
}

/// <summary>由 Hime 专用 hime_server.py 提供的本地 IndexTTS2 服务配置。</summary>
public sealed class IndexTtsOptions
{
    public bool Enabled { get; set; }

    public bool AutoStartLocalServer { get; set; }

    public string BaseUrl { get; set; } = "http://127.0.0.1:9892";

    public string PythonExecutablePath { get; set; } = string.Empty;

    public string WorkingDirectory { get; set; } = string.Empty;

    public string ApiScriptPath { get; set; } = "hime_server.py";

    public string ModelDirectory { get; set; } = "checkpoints";

    public string WorkDirectory { get; set; } = "runtime\\hime-api";

    public string NumbaCacheDirectory { get; set; } = string.Empty;

    public int StartupTimeoutSeconds { get; set; } = 180;

    public int RequestTimeoutSeconds { get; set; } = 360;

    /// <summary>
    /// Ordered, hot-reloadable mappings from dynamic emotion labels to the
    /// IndexTTS2 vector order: happy, angry, sad, afraid, disgusted,
    /// melancholic, surprised, calm.
    /// </summary>
    public List<IndexTtsEmotionVectorRule> EmotionVectors { get; set; } = [];

    /// <summary>Used when no configured marker matches. Exactly eight values are expected.</summary>
    public List<double> FallbackEmotionVector { get; set; } = [];
}

public sealed class IndexTtsEmotionVectorRule
{
    public List<string> Markers { get; set; } = [];

    public List<double> Values { get; set; } = [];
}

/// <summary>单个 GPT-SoVITS 声线的权重与参考音频。</summary>
public sealed class GptSoVitsVoiceProfile
{
    public string GptModelPath { get; set; } = string.Empty;

    public string SoVitsModelPath { get; set; } = string.Empty;

    public string ReferenceAudioPath { get; set; } = string.Empty;

    public string PromptText { get; set; } = string.Empty;

    public string PromptLanguage { get; set; } = "zh";

    public string TextLanguage { get; set; } = "zh";

    public string TextSplitMethod { get; set; } = "cut5";

    public double SpeedFactor { get; set; } = 1.0;

    /// <summary>
    /// Keeps the default reference clip for every emotion so the speaker identity
    /// remains stable. Emotion speed can be disabled separately below.
    /// </summary>
    public bool PreferStableReferenceAudio { get; set; }

    /// <summary>
    /// Uses the profile's base speed for every emotion. This avoids articulation
    /// drift from stretching emotional reference prosody while keeping emotion
    /// available to text and sticker selection.
    /// </summary>
    public bool PreferStableProsody { get; set; }

    /// <summary>
    /// Optional emotion-specific reference clips. Missing emotions safely fall back
    /// to the default reference above, so older voice profiles remain compatible.
    /// </summary>
    public Dictionary<string, GptSoVitsEmotionReference> EmotionReferences { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);
}

public sealed class GptSoVitsEmotionReference
{
    public string ReferenceAudioPath { get; set; } = string.Empty;

    public string PromptText { get; set; } = string.Empty;

    public string PromptLanguage { get; set; } = "zh";

    public double? SpeedFactor { get; set; }
}

/// <summary>单个 IndexTTS2 声线的音色参考与情绪强度。</summary>
public sealed class IndexTtsVoiceProfile
{
    public string ReferenceAudioPath { get; set; } = string.Empty;

    public double EmotionAlpha { get; set; } = 1.0;

    /// <summary>
    /// 为 false 时不注入外部情绪向量，情感和全局风格完全跟随音色参考，
    /// 通常能获得更高的本音还原度。
    /// </summary>
    public bool UseEmotionVector { get; set; } = true;
}
