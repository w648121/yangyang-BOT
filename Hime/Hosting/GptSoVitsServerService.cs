using System.Diagnostics;
using Hime.Services;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Hime.Hosting;

/// <summary>按需启动官方 GPT-SoVITS api_v2.py，并且不让其启动失败阻断 QQ Bot。</summary>
public sealed class GptSoVitsServerService : IHostedService, IDisposable
{
    private readonly GptSoVitsOptions _options;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<GptSoVitsServerService> _logger;
    private Process? _process;
    private volatile bool _isReady;
    private long _generation;

    public bool IsReady => _isReady;

    /// <summary>每次重新加载服务后递增，供声线权重缓存失效使用。</summary>
    public long Generation => Interlocked.Read(ref _generation);

    public GptSoVitsServerService(
        IOptions<VoiceSynthesisOptions> voiceOptions,
        IHttpClientFactory httpClientFactory,
        ILogger<GptSoVitsServerService> logger)
    {
        _options = voiceOptions.Value.GptSoVits;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!_options.Enabled || !_options.AutoStartLocalServer)
            return;

        if (await IsReadyAsync(cancellationToken))
        {
            if (!_isReady)
                Interlocked.Increment(ref _generation);
            _isReady = true;
            _logger.LogInformation("GPT-SoVITS 服务已在运行 ({BaseUrl})", _options.BaseUrl);
            return;
        }

        var workingDirectory = ResolvePath(_options.WorkingDirectory);
        var python = ResolvePath(_options.PythonExecutablePath);
        var script = ResolvePath(_options.ApiScriptPath, workingDirectory);
        var config = ResolvePath(_options.TtsConfigPath);
        if (!File.Exists(python) || !File.Exists(script) || !File.Exists(config))
        {
            _logger.LogWarning(
                "GPT-SoVITS 未启动：缺少运行文件（Python={PythonExists}, Api={ApiExists}, Config={ConfigExists}）",
                File.Exists(python), File.Exists(script), File.Exists(config));
            return;
        }

        var baseUri = new Uri(_options.BaseUrl, UriKind.Absolute);
        var startInfo = new ProcessStartInfo
        {
            FileName = python,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        startInfo.ArgumentList.Add(script);
        startInfo.ArgumentList.Add("-a");
        startInfo.ArgumentList.Add(baseUri.Host);
        startInfo.ArgumentList.Add("-p");
        startInfo.ArgumentList.Add(baseUri.Port.ToString(System.Globalization.CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add(config);

        try
        {
            _process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
            _process.OutputDataReceived += (_, eventArgs) =>
            {
                if (!string.IsNullOrWhiteSpace(eventArgs.Data))
                    _logger.LogDebug("GPT-SoVITS: {Output}", eventArgs.Data);
            };
            _process.ErrorDataReceived += (_, eventArgs) =>
            {
                if (!string.IsNullOrWhiteSpace(eventArgs.Data))
                    _logger.LogWarning("GPT-SoVITS: {Output}", eventArgs.Data);
            };

            if (!_process.Start())
            {
                _logger.LogWarning("GPT-SoVITS 进程未能启动。");
                return;
            }

            _process.BeginOutputReadLine();
            _process.BeginErrorReadLine();
            _logger.LogInformation("正在启动 GPT-SoVITS 服务 (PID={ProcessId}, Url={BaseUrl})", _process.Id, _options.BaseUrl);

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, _options.StartupTimeoutSeconds)));
            while (!timeout.IsCancellationRequested && !_process.HasExited)
            {
                if (await IsReadyAsync(timeout.Token))
                {
                    Interlocked.Increment(ref _generation);
                    _isReady = true;
                    _logger.LogInformation("GPT-SoVITS 服务已就绪 ({BaseUrl})", _options.BaseUrl);
                    return;
                }
                await Task.Delay(TimeSpan.FromSeconds(2), timeout.Token);
            }

            _logger.LogWarning("GPT-SoVITS 在启动期限内未就绪；QQ Bot 将继续运行，语音调用会返回详细错误。");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "启动 GPT-SoVITS 服务失败；不会影响机器人其他功能。");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _isReady = false;
        try
        {
            if (_process is { HasExited: false })
                _process.Kill(entireProcessTree: true);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "停止 GPT-SoVITS 服务时出现异常。");
        }
        return Task.CompletedTask;
    }

    /// <summary>语音引擎切回 GPT-SoVITS 时确保服务已启动并完成模型加载。</summary>
    public async Task<bool> EnsureRunningAsync(CancellationToken cancellationToken = default)
    {
        if (!_options.Enabled)
            return false;

        if (await IsReadyAsync(cancellationToken))
        {
            if (!_isReady)
                Interlocked.Increment(ref _generation);
            _isReady = true;
            return true;
        }

        _isReady = false;
        await StartAsync(cancellationToken);
        return await WaitUntilReadyAsync(cancellationToken);
    }

    /// <summary>释放 GPT-SoVITS 显存，供 IndexTTS2 独占 8GB 显卡。</summary>
    public async Task StopForEngineSwitchAsync(CancellationToken cancellationToken = default)
    {
        _isReady = false;
        try
        {
            var baseUri = new Uri(_options.BaseUrl.TrimEnd('/') + "/", UriKind.Absolute);
            using var client = _httpClientFactory.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(10);
            using var response = await client.GetAsync(new Uri(baseUri, "control?command=exit"), cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "通过 HTTP 停止 GPT-SoVITS 失败，将尝试终止托管进程。");
        }

        await StopAsync(cancellationToken);
        _process?.Dispose();
        _process = null;
        _logger.LogInformation("GPT-SoVITS 已停止并释放显存。");
    }

    /// <summary>
    /// Auto voice delivery can begin before this hosted service finishes loading
    /// large models. Keep the queued delivery alive until the HTTP API is ready.
    /// </summary>
    public async Task<bool> WaitUntilReadyAsync(CancellationToken cancellationToken = default)
    {
        if (!_options.Enabled)
            return false;

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Max(5, _options.StartupTimeoutSeconds)));
        try
        {
            while (!timeout.IsCancellationRequested)
            {
                if (await IsReadyAsync(timeout.Token))
                {
                    _isReady = true;
                    return true;
                }

                _isReady = false;
                await Task.Delay(TimeSpan.FromSeconds(2), timeout.Token);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return false;
        }

        return false;
    }

    private async Task<bool> IsReadyAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(new Uri(_options.BaseUrl.TrimEnd('/') + "/"), "docs"));
            using var response = await _httpClientFactory.CreateClient().SendAsync(request, cancellationToken);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    private static string ResolvePath(string path, string? root = null)
    {
        if (string.IsNullOrWhiteSpace(path))
            return string.Empty;
        return Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(root ?? AppContext.BaseDirectory, path));
    }

    public void Dispose() => _process?.Dispose();
}
