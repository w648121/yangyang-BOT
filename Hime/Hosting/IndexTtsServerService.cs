using System.Diagnostics;
using Hime.Services;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Hime.Hosting;

/// <summary>按需启动 IndexTTS2，并在切回 GPT-SoVITS 时释放全部显存。</summary>
public sealed class IndexTtsServerService : IHostedService, IDisposable
{
    private readonly IndexTtsOptions _options;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<IndexTtsServerService> _logger;
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private Process? _process;
    private volatile bool _isReady;

    public bool IsReady => _isReady;

    public IndexTtsServerService(
        IOptions<VoiceSynthesisOptions> voiceOptions,
        IHttpClientFactory httpClientFactory,
        ILogger<IndexTtsServerService> logger)
    {
        _options = voiceOptions.Value.IndexTts;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (_options.Enabled && _options.AutoStartLocalServer)
            await EnsureRunningAsync(cancellationToken);
    }

    public async Task<bool> EnsureRunningAsync(CancellationToken cancellationToken = default)
    {
        if (!_options.Enabled)
            return false;

        await _lifecycleGate.WaitAsync(cancellationToken);
        try
        {
            if (await IsReadyAsync(cancellationToken))
            {
                _isReady = true;
                return true;
            }

            _isReady = false;
            var workingDirectory = ResolvePath(_options.WorkingDirectory);
            var python = ResolvePath(_options.PythonExecutablePath);
            var script = ResolvePath(_options.ApiScriptPath, workingDirectory);
            var modelDirectory = ResolvePath(_options.ModelDirectory, workingDirectory);
            var workDirectory = ResolvePath(_options.WorkDirectory, workingDirectory);
            if (!File.Exists(python) || !File.Exists(script) || !Directory.Exists(modelDirectory))
            {
                _logger.LogWarning(
                    "IndexTTS2 未启动：缺少运行文件（Python={PythonExists}, Api={ApiExists}, Model={ModelExists}）",
                    File.Exists(python), File.Exists(script), Directory.Exists(modelDirectory));
                return false;
            }

            Directory.CreateDirectory(workDirectory);
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
            startInfo.ArgumentList.Add("--host");
            startInfo.ArgumentList.Add(baseUri.Host);
            startInfo.ArgumentList.Add("--port");
            startInfo.ArgumentList.Add(baseUri.Port.ToString(System.Globalization.CultureInfo.InvariantCulture));
            startInfo.ArgumentList.Add("--model-dir");
            startInfo.ArgumentList.Add(modelDirectory);
            startInfo.ArgumentList.Add("--work-dir");
            startInfo.ArgumentList.Add(workDirectory);
            if (!string.IsNullOrWhiteSpace(_options.NumbaCacheDirectory))
                startInfo.Environment["NUMBA_CACHE_DIR"] = ResolvePath(_options.NumbaCacheDirectory);

            _process?.Dispose();
            _process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
            _process.OutputDataReceived += (_, eventArgs) =>
            {
                if (!string.IsNullOrWhiteSpace(eventArgs.Data))
                    _logger.LogDebug("IndexTTS2: {Output}", eventArgs.Data);
            };
            _process.ErrorDataReceived += (_, eventArgs) =>
            {
                if (!string.IsNullOrWhiteSpace(eventArgs.Data))
                    _logger.LogDebug("IndexTTS2: {Output}", eventArgs.Data);
            };

            if (!_process.Start())
                return false;
            _process.BeginOutputReadLine();
            _process.BeginErrorReadLine();
            _logger.LogInformation("正在启动 IndexTTS2 服务 (PID={ProcessId}, Url={BaseUrl})", _process.Id, _options.BaseUrl);

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(Math.Max(5, _options.StartupTimeoutSeconds)));
            try
            {
                while (!timeout.IsCancellationRequested && !_process.HasExited)
                {
                    if (await IsReadyAsync(timeout.Token))
                    {
                        _isReady = true;
                        _logger.LogInformation("IndexTTS2 服务已就绪 ({BaseUrl})", _options.BaseUrl);
                        return true;
                    }
                    await Task.Delay(TimeSpan.FromSeconds(2), timeout.Token);
                }
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // 转为 false，让调用端返回清晰的启动失败信息。
            }

            _logger.LogWarning("IndexTTS2 在启动期限内未就绪。");
            return false;
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public async Task StopForEngineSwitchAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycleGate.WaitAsync(cancellationToken);
        try
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
                _logger.LogDebug(ex, "通过 HTTP 停止 IndexTTS2 失败，将尝试终止托管进程。");
            }

            try
            {
                if (_process is { HasExited: false })
                    _process.Kill(entireProcessTree: true);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "终止 IndexTTS2 进程时出现异常。");
            }

            _process?.Dispose();
            _process = null;
            _logger.LogInformation("IndexTTS2 已停止并释放显存。");
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) =>
        StopForEngineSwitchAsync(cancellationToken);

    private async Task<bool> IsReadyAsync(CancellationToken cancellationToken)
    {
        try
        {
            var baseUri = new Uri(_options.BaseUrl.TrimEnd('/') + "/", UriKind.Absolute);
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(baseUri, "health"));
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

    public void Dispose()
    {
        _process?.Dispose();
        _lifecycleGate.Dispose();
    }
}
