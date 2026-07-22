using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Hime.Services;

/// <summary>Primes the configured baseline voice once the local engine has started.</summary>
public sealed class VoiceWarmupService : BackgroundService
{
    private readonly VoiceSynthesisService _synthesis;
    private readonly VoiceSynthesisOptions _options;
    private readonly RuntimeDiagnostics _diagnostics;
    private readonly ILogger<VoiceWarmupService> _logger;

    public VoiceWarmupService(
        VoiceSynthesisService synthesis,
        IOptions<VoiceSynthesisOptions> options,
        RuntimeDiagnostics diagnostics,
        ILogger<VoiceWarmupService> logger)
    {
        _synthesis = synthesis;
        _options = options.Value;
        _diagnostics = diagnostics;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled || !_options.Warmup.Enabled)
            return;

        await Task.Delay(TimeSpan.FromSeconds(Math.Clamp(_options.Warmup.DelaySeconds, 0, 60)), stoppingToken);
        using var operation = _diagnostics.Begin("voice.warmup");
        var result = await _synthesis.SynthesizeAsync(
            _options.Warmup.Text,
            _options.Warmup.Voice,
            stoppingToken,
            emotion: "neutral",
            emotionAlphaOverride: null,
            bypassCache: true);
        if (!result.IsSuccess)
        {
            operation.Fail();
            _logger.LogWarning("Voice warmup failed (Voice={Voice}, Error={Error})", _options.Warmup.Voice, result.Error);
            return;
        }

        _diagnostics.Increment("voice.warmup.completed");
        _logger.LogInformation("Voice warmup completed (Voice={Voice})", _options.Warmup.Voice);
    }
}
