using Microsoft.Extensions.Logging;

namespace Hime.Hosting;

/// <summary>
/// 在 8GB 显卡上互斥运行 GPT-SoVITS 与 IndexTTS2，避免两个模型同时占用显存。
/// </summary>
public sealed class VoiceEngineCoordinator
{
    private readonly GptSoVitsServerService _gptSoVits;
    private readonly IndexTtsServerService _indexTts;
    private readonly ILogger<VoiceEngineCoordinator> _logger;
    private readonly SemaphoreSlim _switchGate = new(1, 1);

    public VoiceEngineCoordinator(
        GptSoVitsServerService gptSoVits,
        IndexTtsServerService indexTts,
        ILogger<VoiceEngineCoordinator> logger)
    {
        _gptSoVits = gptSoVits;
        _indexTts = indexTts;
        _logger = logger;
    }

    public async Task<bool> EnsureEngineAsync(string engine, CancellationToken cancellationToken = default)
    {
        await _switchGate.WaitAsync(cancellationToken);
        try
        {
            if (string.Equals(engine, "index-tts2", StringComparison.OrdinalIgnoreCase))
            {
                if (_indexTts.IsReady)
                    return await _indexTts.EnsureRunningAsync(cancellationToken);

                _logger.LogInformation("语音引擎切换：GPT-SoVITS -> IndexTTS2");
                await _gptSoVits.StopForEngineSwitchAsync(cancellationToken);
                await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
                return await _indexTts.EnsureRunningAsync(cancellationToken);
            }

            if (string.Equals(engine, "gpt-sovits", StringComparison.OrdinalIgnoreCase))
            {
                if (_gptSoVits.IsReady)
                    return await _gptSoVits.EnsureRunningAsync(cancellationToken);

                _logger.LogInformation("语音引擎切换：IndexTTS2 -> GPT-SoVITS");
                await _indexTts.StopForEngineSwitchAsync(cancellationToken);
                await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
                return await _gptSoVits.EnsureRunningAsync(cancellationToken);
            }

            return true;
        }
        finally
        {
            _switchGate.Release();
        }
    }

    /// <summary>
    /// Restarts a backend after a healthy process begins returning inference failures.
    /// This is intentionally separate from the health probe: a model can answer
    /// <c>/health</c> while its CUDA allocator is no longer able to synthesize.
    /// </summary>
    public async Task<bool> RestartEngineAsync(string engine, CancellationToken cancellationToken = default)
    {
        await _switchGate.WaitAsync(cancellationToken);
        try
        {
            if (string.Equals(engine, "index-tts2", StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogWarning("IndexTTS2 inference failed; restarting the backend once to recover GPU state.");
                await _indexTts.StopForEngineSwitchAsync(cancellationToken);
                await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
                return await _indexTts.EnsureRunningAsync(cancellationToken);
            }

            if (string.Equals(engine, "gpt-sovits", StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogWarning("GPT-SoVITS inference failed; restarting the backend once.");
                await _gptSoVits.StopForEngineSwitchAsync(cancellationToken);
                await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
                return await _gptSoVits.EnsureRunningAsync(cancellationToken);
            }

            return false;
        }
        finally
        {
            _switchGate.Release();
        }
    }
}
