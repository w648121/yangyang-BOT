using System.Diagnostics;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using Hime.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Hime.Services;

public enum VoiceSynthesisStatus
{
    Success,
    Disabled,
    InvalidRequest,
    ConfigurationError,
    TimedOut,
    Cancelled,
    Failed
}

public sealed record VoiceSynthesisResult(
    VoiceSynthesisStatus Status,
    string? FilePath = null,
    string? Error = null,
    bool FromCache = false)
{
    public bool IsSuccess => Status == VoiceSynthesisStatus.Success && FilePath is not null;
}

/// <summary>
/// 串行执行基础 TTS 与 RVC 声线转换，并缓存最终 WAV。
/// 参数占位符：{text}、{input}、{output}、{voice}、{model}、{index}、
/// {pitch}、{rvcRoot}，以及声线 Parameters 中声明的自定义占位符。
/// </summary>
public sealed class VoiceSynthesisService
{
    private const int MaxCapturedOutputCharacters = 16_384;
    private const string CanonicalYangyangVoice = "yangyang-indextts2-faithful-a";
    private readonly VoiceSynthesisOptions _options;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<VoiceSynthesisService> _logger;
    private readonly VoiceEngineCoordinator _engineCoordinator;
    private readonly GptSoVitsServerService _gptSoVitsServer;
    private readonly RuntimeDiagnostics _diagnostics;
    private readonly SemaphoreSlim _serialGate = new(1, 1);
    private string? _loadedGptSoVitsVoice;
    private long _loadedGptSoVitsGeneration = -1;

    public VoiceSynthesisService(
        IOptions<VoiceSynthesisOptions> options,
        IHttpClientFactory httpClientFactory,
        VoiceEngineCoordinator engineCoordinator,
        GptSoVitsServerService gptSoVitsServer,
        RuntimeDiagnostics diagnostics,
        ILogger<VoiceSynthesisService> logger)
    {
        _options = options.Value;
        _httpClientFactory = httpClientFactory;
        _engineCoordinator = engineCoordinator;
        _gptSoVitsServer = gptSoVitsServer;
        _diagnostics = diagnostics;
        _logger = logger;
    }

    public IReadOnlyCollection<string> AvailableVoices => _options.Voices
        .Where(pair => pair.Value.Enabled)
        .Select(pair => pair.Key)
        .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
        .ToArray();

    public async Task<VoiceSynthesisResult> SynthesizeAsync(
        string text,
        string? voice = null,
        CancellationToken cancellationToken = default,
        string? emotion = null,
        double? emotionAlphaOverride = null,
        bool bypassCache = false)
    {
        if (!_options.Enabled)
            return new(VoiceSynthesisStatus.Disabled, Error: "语音合成功能未启用。");

        text = text?.Trim() ?? string.Empty;
        if (text.Length == 0)
            return new(VoiceSynthesisStatus.InvalidRequest, Error: "待合成文本不能为空。");
        if (text.Length > Math.Max(1, _options.MaxTextLength))
            return new(VoiceSynthesisStatus.InvalidRequest, Error: $"文本超过 {_options.MaxTextLength} 字限制。");

        voice = string.IsNullOrWhiteSpace(voice) ? _options.DefaultVoice : voice.Trim();
        // `yangyang` is the stable public alias used by prompts and older commands.
        // Keep that alias while allowing the underlying baseline engine to evolve.
        if (string.Equals(voice, "yangyang", StringComparison.OrdinalIgnoreCase))
            voice = CanonicalYangyangVoice;
        if (!_options.Voices.TryGetValue(voice, out var profile) || !profile.Enabled)
            return new(VoiceSynthesisStatus.InvalidRequest, Error: $"未知或已停用的声线：{voice}");

        try
        {
            await _serialGate.WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return new(VoiceSynthesisStatus.Cancelled, Error: "语音合成已取消。");
        }

        try
        {
            return await SynthesizeCoreAsync(
                text,
                voice,
                profile,
                emotion,
                emotionAlphaOverride,
                bypassCache,
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return new(VoiceSynthesisStatus.Cancelled, Error: "语音合成已取消。");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "语音合成失败 (Voice={Voice})", voice);
            return new(VoiceSynthesisStatus.Failed, Error: ex.Message);
        }
        finally
        {
            _serialGate.Release();
        }
    }

    private async Task<VoiceSynthesisResult> SynthesizeCoreAsync(
        string text,
        string voice,
        RvcVoiceProfile profile,
        string? emotion,
        double? emotionAlphaOverride,
        bool bypassCache,
        CancellationToken cancellationToken)
    {
        if (string.Equals(profile.Engine, "gpt-sovits", StringComparison.OrdinalIgnoreCase))
            return await SynthesizeGptSoVitsAsync(text, voice, profile, emotion, bypassCache, cancellationToken);
        if (string.Equals(profile.Engine, "index-tts2", StringComparison.OrdinalIgnoreCase))
            return await SynthesizeIndexTtsAsync(
                text,
                voice,
                profile,
                emotion,
                emotionAlphaOverride,
                bypassCache,
                cancellationToken);

        var validationError = ValidateCommands();
        if (validationError is not null)
            return new(VoiceSynthesisStatus.ConfigurationError, Error: validationError);

        var rvcRoot = ResolveDirectory(_options.RvcRootDirectory);
        var model = ResolveUnderRoot(rvcRoot, profile.ModelPath);
        var index = ResolveUnderRoot(rvcRoot, profile.IndexPath);
        if (!File.Exists(model))
            return new(VoiceSynthesisStatus.ConfigurationError, Error: $"找不到 RVC 模型：{model}");
        if (!string.IsNullOrWhiteSpace(profile.IndexPath) && !File.Exists(index))
            return new(VoiceSynthesisStatus.ConfigurationError, Error: $"找不到 RVC 索引：{index}");

        var cacheDirectory = ResolveWritableDirectory(_options.CacheDirectory);
        Directory.CreateDirectory(cacheDirectory);
        var cacheKey = CreateCacheKey(text, voice, profile, model, index);
        var cachedOutput = Path.Combine(cacheDirectory, $"{voice}_{cacheKey}.wav");
        if (!bypassCache && IsUsableWave(cachedOutput))
        {
            RecordCacheHit(cachedOutput);
            return new(VoiceSynthesisStatus.Success, cachedOutput, FromCache: true);
        }
        _diagnostics.Increment("voice.cache.miss");

        var jobDirectory = Path.Combine(cacheDirectory, ".work", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(jobDirectory);
        var baseWave = Path.Combine(jobDirectory, "base.wav");
        var convertedWave = Path.Combine(jobDirectory, "converted.wav");

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, _options.TimeoutSeconds)));

            // 第一阶段的 {output} 必须指向基础 WAV；第二阶段再切换到 RVC 输出。
            var values = BuildValues(text, voice, profile, rvcRoot, model, index, baseWave, baseWave);
            var baseResult = await RunCommandAsync(_options.BaseTts, values, timeout.Token);
            if (!baseResult.IsSuccess)
                return MapCommandFailure("基础 TTS", baseResult, cancellationToken);
            if (!IsUsableWave(baseWave))
                return new(VoiceSynthesisStatus.Failed, Error: "基础 TTS 未生成有效 WAV 文件。");

            values["input"] = baseWave;
            values["output"] = convertedWave;
            var rvcResult = await RunCommandAsync(_options.Rvc, values, timeout.Token);
            if (!rvcResult.IsSuccess)
                return MapCommandFailure("RVC", rvcResult, cancellationToken);
            if (!IsUsableWave(convertedWave))
                return new(VoiceSynthesisStatus.Failed, Error: "RVC 未生成有效 WAV 文件。");

            File.Move(convertedWave, cachedOutput, overwrite: true);
            _logger.LogInformation("语音合成完成 (Voice={Voice}, Path={Path})", voice, cachedOutput);
            return new(VoiceSynthesisStatus.Success, cachedOutput);
        }
        finally
        {
            TryDeleteDirectory(jobDirectory);
        }
    }

    private async Task<VoiceSynthesisResult> SynthesizeGptSoVitsAsync(
        string text,
        string voice,
        RvcVoiceProfile profile,
        string? emotion,
        bool bypassCache,
        CancellationToken cancellationToken)
    {
        var options = _options.GptSoVits;
        var model = profile.GptSoVits;
        if (!options.Enabled)
            return new(VoiceSynthesisStatus.ConfigurationError, Error: "GPT-SoVITS 后端未启用。");
        if (!Uri.TryCreate(options.BaseUrl, UriKind.Absolute, out var baseUri))
            return new(VoiceSynthesisStatus.ConfigurationError, Error: "GPT-SoVITS BaseUrl 无效。");

        var root = string.IsNullOrWhiteSpace(options.WorkingDirectory)
            ? AppContext.BaseDirectory
            : ResolveDirectory(options.WorkingDirectory);
        var emotionReference = ResolveEmotionReference(model, emotion);
        var useEmotionReferenceAudio = emotionReference is not null && !model.PreferStableReferenceAudio;
        var referenceAudioPath = useEmotionReferenceAudio ? emotionReference!.ReferenceAudioPath : model.ReferenceAudioPath;
        var promptText = useEmotionReferenceAudio ? emotionReference!.PromptText : model.PromptText;
        var promptLanguage = useEmotionReferenceAudio ? emotionReference!.PromptLanguage : model.PromptLanguage;
        var speedFactor = model.PreferStableProsody
            ? model.SpeedFactor
            : emotionReference?.SpeedFactor ?? model.SpeedFactor;
        var gptModel = ResolveConfiguredPath(model.GptModelPath, root);
        var soVitsModel = ResolveConfiguredPath(model.SoVitsModelPath, root);
        var referenceAudio = ResolveConfiguredPath(referenceAudioPath, root);
        if (!File.Exists(gptModel) || !File.Exists(soVitsModel) || !File.Exists(referenceAudio))
        {
            return new(
                VoiceSynthesisStatus.ConfigurationError,
                Error: $"GPT-SoVITS 声线文件不完整（GPT={File.Exists(gptModel)}, SoVITS={File.Exists(soVitsModel)}, Reference={File.Exists(referenceAudio)}）。");
        }
        if (string.IsNullOrWhiteSpace(promptText))
            return new(VoiceSynthesisStatus.ConfigurationError, Error: "GPT-SoVITS 缺少参考音频对应的提示文本。");

        var cacheDirectory = ResolveWritableDirectory(_options.CacheDirectory);
        Directory.CreateDirectory(cacheDirectory);
        var cacheKey = CreateGptSoVitsCacheKey(
            text, voice, model, gptModel, soVitsModel, referenceAudio, promptText, promptLanguage, speedFactor);
        var cachedOutput = Path.Combine(cacheDirectory, $"{voice}_{cacheKey}.wav");
        if (!bypassCache && IsUsableWave(cachedOutput))
        {
            RecordCacheHit(cachedOutput);
            return new(VoiceSynthesisStatus.Success, cachedOutput, FromCache: true);
        }
        _diagnostics.Increment("voice.cache.miss");

        if (!await _engineCoordinator.EnsureEngineAsync("gpt-sovits", cancellationToken))
            return new(VoiceSynthesisStatus.Failed, Error: "GPT-SoVITS 服务启动失败或未在期限内就绪。");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, options.RequestTimeoutSeconds)));
        var client = _httpClientFactory.CreateClient();
        client.Timeout = Timeout.InfiniteTimeSpan;

        var generation = _gptSoVitsServer.Generation;
        if (!string.Equals(_loadedGptSoVitsVoice, voice, StringComparison.OrdinalIgnoreCase) ||
            _loadedGptSoVitsGeneration != generation)
        {
            var gptSwitch = await SetGptSoVitsWeightsAsync(client, baseUri, "set_gpt_weights", gptModel, timeout.Token);
            if (gptSwitch is not null)
                return new(VoiceSynthesisStatus.Failed, Error: gptSwitch);

            var soVitsSwitch = await SetGptSoVitsWeightsAsync(client, baseUri, "set_sovits_weights", soVitsModel, timeout.Token);
            if (soVitsSwitch is not null)
                return new(VoiceSynthesisStatus.Failed, Error: soVitsSwitch);

            _loadedGptSoVitsVoice = voice;
            _loadedGptSoVitsGeneration = generation;
        }

        var request = new
        {
            text,
            text_lang = string.IsNullOrWhiteSpace(model.TextLanguage) ? "zh" : model.TextLanguage,
            ref_audio_path = referenceAudio,
            prompt_lang = string.IsNullOrWhiteSpace(promptLanguage) ? "zh" : promptLanguage,
            prompt_text = promptText,
            text_split_method = string.IsNullOrWhiteSpace(model.TextSplitMethod) ? "cut5" : model.TextSplitMethod,
            batch_size = 1,
            media_type = "wav",
            streaming_mode = false,
            speed_factor = Math.Clamp(speedFactor, 0.5, 2.0),
            parallel_infer = false
        };

        try
        {
            using var response = await client.PostAsJsonAsync(new Uri(baseUri, "tts"), request, timeout.Token);
            if (!response.IsSuccessStatusCode)
            {
                var details = await ReadHttpFailureAsync(response, timeout.Token);
                return new(VoiceSynthesisStatus.Failed, Error: $"GPT-SoVITS 合成请求失败（HTTP {(int)response.StatusCode}）：{details}");
            }

            var temporaryOutput = cachedOutput + ".tmp";
            await using (var input = await response.Content.ReadAsStreamAsync(timeout.Token))
            await using (var output = new FileStream(temporaryOutput, FileMode.Create, FileAccess.Write, FileShare.None))
                await input.CopyToAsync(output, timeout.Token);

            if (!IsUsableWave(temporaryOutput))
            {
                File.Delete(temporaryOutput);
                return new(VoiceSynthesisStatus.Failed, Error: "GPT-SoVITS 未返回有效 WAV 音频。");
            }

            File.Move(temporaryOutput, cachedOutput, overwrite: true);
            _logger.LogInformation(
                "GPT-SoVITS 语音合成完成 (Voice={Voice}, Emotion={Emotion}, StableReference={StableReference}, StableProsody={StableProsody}, Path={Path})",
                voice,
                emotion ?? "neutral",
                model.PreferStableReferenceAudio,
                model.PreferStableProsody,
                cachedOutput);
            return new(VoiceSynthesisStatus.Success, cachedOutput);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new(VoiceSynthesisStatus.Cancelled, Error: "GPT-SoVITS 合成已取消。");
        }
        catch (OperationCanceledException)
        {
            return new(VoiceSynthesisStatus.TimedOut, Error: "GPT-SoVITS 合成超时。");
        }
    }

    private async Task<VoiceSynthesisResult> SynthesizeIndexTtsAsync(
        string text,
        string voice,
        RvcVoiceProfile profile,
        string? emotion,
        double? emotionAlphaOverride,
        bool bypassCache,
        CancellationToken cancellationToken)
    {
        var options = _options.IndexTts;
        var model = profile.IndexTts;
        if (!options.Enabled)
            return new(VoiceSynthesisStatus.ConfigurationError, Error: "IndexTTS2 后端未启用。");
        if (!Uri.TryCreate(options.BaseUrl, UriKind.Absolute, out var baseUri))
            return new(VoiceSynthesisStatus.ConfigurationError, Error: "IndexTTS2 BaseUrl 无效。");

        var root = string.IsNullOrWhiteSpace(options.WorkingDirectory)
            ? AppContext.BaseDirectory
            : ResolveDirectory(options.WorkingDirectory);
        var referenceAudio = ResolveConfiguredPath(model.ReferenceAudioPath, root);
        if (!File.Exists(referenceAudio))
            return new(VoiceSynthesisStatus.ConfigurationError, Error: $"找不到 IndexTTS2 参考音频：{referenceAudio}");

        var emotionVector = model.UseEmotionVector
            ? ResolveIndexTtsEmotionVector(emotion)
            : null;
        var emotionAlpha = Math.Clamp(emotionAlphaOverride ?? model.EmotionAlpha, 0.0, 1.5);
        var cacheDirectory = ResolveWritableDirectory(_options.CacheDirectory);
        Directory.CreateDirectory(cacheDirectory);
        var cacheKey = CreateIndexTtsCacheKey(text, voice, referenceAudio, emotionVector, emotionAlpha);
        var cachedOutput = Path.Combine(cacheDirectory, $"{voice}_{cacheKey}.wav");
        if (!bypassCache && IsUsableWave(cachedOutput))
        {
            RecordCacheHit(cachedOutput);
            return new(VoiceSynthesisStatus.Success, cachedOutput, FromCache: true);
        }
        _diagnostics.Increment("voice.cache.miss");

        if (!await _engineCoordinator.EnsureEngineAsync("index-tts2", cancellationToken))
            return new(VoiceSynthesisStatus.Failed, Error: "IndexTTS2 服务启动失败或未在期限内就绪。");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, options.RequestTimeoutSeconds)));
        var client = _httpClientFactory.CreateClient();
        client.Timeout = Timeout.InfiniteTimeSpan;
        var request = new
        {
            text,
            ref_audio_path = referenceAudio,
            emo_vector = emotionVector,
            emo_alpha = emotionAlpha
        };

        try
        {
            using var response = await client.PostAsJsonAsync(new Uri(baseUri, "tts"), request, timeout.Token);
            if (!response.IsSuccessStatusCode)
            {
                var details = await ReadHttpFailureAsync(response, timeout.Token);
                return new(VoiceSynthesisStatus.Failed, Error: $"IndexTTS2 合成请求失败（HTTP {(int)response.StatusCode}）：{details}");
            }

            var temporaryOutput = cachedOutput + ".tmp";
            await using (var input = await response.Content.ReadAsStreamAsync(timeout.Token))
            await using (var output = new FileStream(temporaryOutput, FileMode.Create, FileAccess.Write, FileShare.None))
                await input.CopyToAsync(output, timeout.Token);

            if (!IsUsableWave(temporaryOutput))
            {
                File.Delete(temporaryOutput);
                return new(VoiceSynthesisStatus.Failed, Error: "IndexTTS2 未返回有效 WAV 音频。");
            }

            File.Move(temporaryOutput, cachedOutput, overwrite: true);
            _logger.LogInformation(
                "IndexTTS2 语音合成完成 (Voice={Voice}, Emotion={Emotion}, Path={Path})",
                voice,
                emotion ?? "neutral",
                cachedOutput);
            return new(VoiceSynthesisStatus.Success, cachedOutput);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new(VoiceSynthesisStatus.Cancelled, Error: "IndexTTS2 合成已取消。");
        }
        catch (OperationCanceledException)
        {
            return new(VoiceSynthesisStatus.TimedOut, Error: "IndexTTS2 合成超时。");
        }
    }

    private static async Task<string?> SetGptSoVitsWeightsAsync(
        HttpClient client,
        Uri baseUri,
        string endpoint,
        string modelPath,
        CancellationToken cancellationToken)
    {
        using var response = await client.GetAsync(
            new Uri(baseUri, $"{endpoint}?weights_path={Uri.EscapeDataString(modelPath)}"),
            cancellationToken);
        if (response.IsSuccessStatusCode)
            return null;

        var details = await ReadHttpFailureAsync(response, cancellationToken);
        return $"GPT-SoVITS 切换权重失败（{endpoint}, HTTP {(int)response.StatusCode}）：{details}";
    }

    private static async Task<string> ReadHttpFailureAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var text = await response.Content.ReadAsStringAsync(cancellationToken);
        return string.IsNullOrWhiteSpace(text)
            ? "服务未返回错误详情。"
            : text[..Math.Min(text.Length, 2_000)];
    }

    private string? ValidateCommands()
    {
        if (string.IsNullOrWhiteSpace(_options.BaseTts.ExecutablePath))
            return "未配置基础 TTS 可执行文件。";
        if (string.IsNullOrWhiteSpace(_options.Rvc.ExecutablePath))
            return "未配置 RVC CLI 可执行文件。";
        return null;
    }

    private Dictionary<string, string> BuildValues(
        string text,
        string voice,
        RvcVoiceProfile profile,
        string rvcRoot,
        string model,
        string index,
        string input,
        string output)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["text"] = text,
            ["voice"] = string.IsNullOrWhiteSpace(profile.BaseTtsVoice) ? voice : profile.BaseTtsVoice,
            ["model"] = model,
            ["index"] = index,
            ["pitch"] = profile.Pitch.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["rvcRoot"] = rvcRoot,
            ["input"] = input,
            ["output"] = output
        };

        foreach (var pair in profile.Parameters)
            values[pair.Key] = pair.Value;
        return values;
    }

    private async Task<CommandResult> RunCommandAsync(
        VoiceCommandOptions command,
        IReadOnlyDictionary<string, string> values,
        CancellationToken cancellationToken)
    {
        var executable = Expand(command.ExecutablePath, values);
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        if (!string.IsNullOrWhiteSpace(command.WorkingDirectory))
            startInfo.WorkingDirectory = ResolveDirectory(Expand(command.WorkingDirectory, values));

        foreach (var argument in command.Arguments)
            startInfo.ArgumentList.Add(Expand(argument, values));

        using var process = new Process { StartInfo = startInfo };
        try
        {
            if (!process.Start())
                return new(false, false, -1, "进程未能启动。");

            var stdoutTask = ReadLimitedAsync(process.StandardOutput);
            var stderrTask = ReadLimitedAsync(process.StandardError);
            try
            {
                await process.WaitForExitAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                TryKill(process);
                await Task.WhenAll(stdoutTask, stderrTask);
                return new(false, true, -1, "命令执行超时或被取消。");
            }

            var output = await stdoutTask;
            var error = await stderrTask;
            var details = string.Join(Environment.NewLine, new[] { error, output }.Where(value => !string.IsNullOrWhiteSpace(value)));
            return new(process.ExitCode == 0, false, process.ExitCode, details);
        }
        catch (Exception ex)
        {
            TryKill(process);
            return new(false, false, -1, ex.Message);
        }
    }

    private VoiceSynthesisResult MapCommandFailure(
        string stage,
        CommandResult result,
        CancellationToken callerToken)
    {
        if (callerToken.IsCancellationRequested)
            return new(VoiceSynthesisStatus.Cancelled, Error: $"{stage} 已取消。");
        if (result.WasCancelled)
            return new(VoiceSynthesisStatus.TimedOut, Error: $"{stage} 执行超时。");

        var details = string.IsNullOrWhiteSpace(result.Details) ? "无错误输出" : result.Details;
        _logger.LogWarning("{Stage} 失败 (ExitCode={ExitCode}): {Details}", stage, result.ExitCode, details);
        return new(VoiceSynthesisStatus.Failed, Error: $"{stage} 执行失败（退出码 {result.ExitCode}）：{details}");
    }

    private string CreateCacheKey(
        string text,
        string voice,
        RvcVoiceProfile profile,
        string model,
        string index)
    {
        var modelStamp = File.GetLastWriteTimeUtc(model).Ticks;
        var indexStamp = File.Exists(index) ? File.GetLastWriteTimeUtc(index).Ticks : 0;
        var profileParameters = string.Join(
            ';',
            profile.Parameters.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                .Select(pair => $"{pair.Key}={pair.Value}"));
        var source = string.Join(
            '\n',
            text,
            voice,
            profile.BaseTtsVoice,
            profile.Pitch,
            profileParameters,
            model,
            modelStamp,
            index,
            indexStamp,
            CreateCommandFingerprint(_options.BaseTts),
            CreateCommandFingerprint(_options.Rvc));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source)))[..24].ToLowerInvariant();
    }

    private static string CreateGptSoVitsCacheKey(
        string text,
        string voice,
        GptSoVitsVoiceProfile profile,
        string gptModel,
        string soVitsModel,
        string referenceAudio,
        string promptText,
        string promptLanguage,
        double speedFactor)
    {
        var source = string.Join(
            '\n',
            text,
            voice,
            promptText,
            promptLanguage,
            profile.TextLanguage,
            profile.TextSplitMethod,
            speedFactor.ToString(System.Globalization.CultureInfo.InvariantCulture),
            gptModel,
            File.GetLastWriteTimeUtc(gptModel).Ticks,
            soVitsModel,
            File.GetLastWriteTimeUtc(soVitsModel).Ticks,
            referenceAudio,
            File.GetLastWriteTimeUtc(referenceAudio).Ticks);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source)))[..24].ToLowerInvariant();
    }

    private static string CreateIndexTtsCacheKey(
        string text,
        string voice,
        string referenceAudio,
        IReadOnlyList<double>? emotionVector,
        double emotionAlpha)
    {
        var source = string.Join(
            '\n',
            text,
            voice,
            referenceAudio,
            File.GetLastWriteTimeUtc(referenceAudio).Ticks,
            emotionVector is null
                ? "speaker-reference-style"
                : string.Join(',', emotionVector.Select(value => value.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture))),
            emotionAlpha.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source)))[..24].ToLowerInvariant();
    }

    /// <summary>
    /// IndexTTS2 的顺序为 happy、angry、sad、afraid、disgusted、melancholic、surprised、calm。
    /// 动态标签无法命中时回退到轻度 calm，避免强行使用不匹配的夸张情绪。
    /// </summary>
    private static double[] ResolveIndexTtsEmotionVector(string? emotion)
    {
        var value = (emotion ?? "neutral").Trim().ToLowerInvariant();
        var vector = new double[8];

        if (value.Contains("excited") || value.Contains("兴奋") || value.Contains("激动"))
        {
            vector[0] = 0.65;
            vector[6] = 0.15;
        }
        else if (value.Contains("happy") || value.Contains("joy") || value.Contains("开心") ||
                 value.Contains("喜悦") || value.Contains("proud") || value.Contains("得意"))
        {
            vector[0] = 0.62;
        }
        else if (value.Contains("annoy") || value.Contains("烦") || value.Contains("不耐"))
        {
            vector[1] = 0.42;
            vector[4] = 0.12;
        }
        else if (value.Contains("angry") || value.Contains("anger") || value.Contains("生气") || value.Contains("愤怒"))
        {
            vector[1] = 0.65;
        }
        else if (value.Contains("melanch") || value.Contains("忧郁") || value.Contains("孤独") || value.Contains("失落"))
        {
            vector[5] = 0.58;
            vector[2] = 0.18;
        }
        else if (value.Contains("sad") || value.Contains("悲") || value.Contains("难过"))
        {
            vector[2] = 0.62;
            vector[5] = 0.20;
        }
        else if (value.Contains("fear") || value.Contains("afraid") || value.Contains("害怕") ||
                 value.Contains("恐惧") || value.Contains("anxious") || value.Contains("焦虑"))
        {
            vector[3] = 0.58;
        }
        else if (value.Contains("disgust") || value.Contains("嫌弃") || value.Contains("厌恶"))
        {
            vector[4] = 0.58;
        }
        else if (value.Contains("surpris") || value.Contains("惊") || value.Contains("震惊"))
        {
            vector[6] = 0.62;
        }
        else if (value.Contains("shy") || value.Contains("embarrass") || value.Contains("害羞") || value.Contains("尴尬"))
        {
            vector[0] = 0.20;
            vector[3] = 0.12;
            vector[7] = 0.28;
        }
        else if (value.Contains("comfort") || value.Contains("安慰") || value.Contains("gentle") || value.Contains("温柔"))
        {
            vector[0] = 0.10;
            vector[7] = 0.48;
        }
        else if (value.Contains("serious") || value.Contains("严肃") || value.Contains("认真"))
        {
            vector[1] = 0.08;
            vector[7] = 0.42;
        }
        else
        {
            vector[7] = 0.35;
        }

        return vector;
    }

    private static GptSoVitsEmotionReference? ResolveEmotionReference(
        GptSoVitsVoiceProfile profile,
        string? emotion)
    {
        if (profile.EmotionReferences.Count == 0)
            return null;

        var normalized = (emotion ?? "neutral").Trim().ToLowerInvariant();
        if (profile.EmotionReferences.TryGetValue(normalized, out var exact))
            return exact;

        var fallback = normalized switch
        {
            "proud" => "happy",
            "embarrassed" => "shy",
            "angry" => "serious",
            "surprised" => "happy",
            _ => "neutral"
        };
        return profile.EmotionReferences.TryGetValue(fallback, out var mapped) ? mapped : null;
    }

    private static string CreateCommandFingerprint(VoiceCommandOptions command)
    {
        static string Stamp(string value)
        {
            if (value.Contains('{') || !File.Exists(value))
                return value;
            return $"{value}@{File.GetLastWriteTimeUtc(value).Ticks}";
        }

        return string.Join(
            '|',
            Stamp(command.ExecutablePath),
            command.WorkingDirectory ?? string.Empty,
            string.Join('\u001f', command.Arguments.Select(Stamp)));
    }

    private static string Expand(string template, IReadOnlyDictionary<string, string> values)
    {
        var result = template;
        foreach (var pair in values)
            result = result.Replace($"{{{pair.Key}}}", pair.Value, StringComparison.OrdinalIgnoreCase);
        return result;
    }

    private static bool IsUsableWave(string path)
    {
        if (!File.Exists(path) || new FileInfo(path).Length < 44)
            return false;

        Span<byte> header = stackalloc byte[12];
        using var stream = File.OpenRead(path);
        if (stream.Read(header) != header.Length)
            return false;
        return header[..4].SequenceEqual("RIFF"u8) && header[8..].SequenceEqual("WAVE"u8);
    }

    private void RecordCacheHit(string path)
    {
        _diagnostics.Increment("voice.cache.hit");
        try
        {
            File.SetLastAccessTimeUtc(path, DateTime.UtcNow);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug(ex, "Unable to update voice cache access time for {Path}", path);
        }
    }

    private static async Task<string> ReadLimitedAsync(StreamReader reader)
    {
        var buffer = new char[2048];
        var output = new StringBuilder();
        while (true)
        {
            var read = await reader.ReadAsync(buffer);
            if (read == 0)
                break;
            if (output.Length < MaxCapturedOutputCharacters)
                output.Append(buffer, 0, Math.Min(read, MaxCapturedOutputCharacters - output.Length));
        }
        return output.ToString();
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch
        {
            // 进程可能恰好已经退出。
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch
        {
            // 清理失败不影响已返回的合成结果，遗留目录可由维护任务清理。
        }
    }

    private static string ResolveUnderRoot(string root, string path) =>
        Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(root, path));

    private static string ResolveConfiguredPath(string path, string root) =>
        Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(root, path));

    private static string ResolveWritableDirectory(string path)
    {
        if (Path.IsPathRooted(path))
            return Path.GetFullPath(path);
        return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, path));
    }

    private static string ResolveDirectory(string path)
    {
        if (Path.IsPathRooted(path))
            return Path.GetFullPath(path);

        foreach (var origin in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            var directory = new DirectoryInfo(origin);
            while (directory is not null)
            {
                var candidate = Path.GetFullPath(Path.Combine(directory.FullName, path));
                if (Directory.Exists(candidate))
                    return candidate;
                directory = directory.Parent;
            }
        }

        return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, path));
    }

    private sealed record CommandResult(bool IsSuccess, bool WasCancelled, int ExitCode, string Details);
}
