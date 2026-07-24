using System.Diagnostics;
using System.Net.Sockets;
using System.Text.Json;
using Hime.Services;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Hime.Hosting;

/// <summary>
/// Starts the local IndexTTS2 service on demand and owns its process lifetime.
/// Startup failures retain a short output tail so port/model errors remain visible.
/// </summary>
public sealed class IndexTtsServerService : IHostedService, IDisposable
{
    private readonly IndexTtsOptions _options;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<IndexTtsServerService> _logger;
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private readonly object _outputSync = new();
    private readonly Queue<string> _processOutputTail = new();
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
            var baseUri = new Uri(_options.BaseUrl, UriKind.Absolute);
            if (await IsTcpPortOccupiedAsync(baseUri, cancellationToken))
            {
                _logger.LogError(
                    "IndexTTS2 cannot start because {Host}:{Port} is occupied by another process. " +
                    "The occupant did not return an IndexTTS2 health response. Change VoiceSynthesis:IndexTts:BaseUrl or stop that process.",
                    baseUri.Host,
                    baseUri.Port);
                return false;
            }

            var workingDirectory = ResolvePath(_options.WorkingDirectory);
            var python = ResolvePath(_options.PythonExecutablePath);
            var script = ResolvePath(_options.ApiScriptPath, workingDirectory);
            var modelDirectory = ResolvePath(_options.ModelDirectory, workingDirectory);
            var workDirectory = ResolvePath(_options.WorkDirectory, workingDirectory);
            if (!File.Exists(python) || !File.Exists(script) || !Directory.Exists(modelDirectory))
            {
                _logger.LogWarning(
                    "IndexTTS2 cannot start because runtime files are missing (Python={PythonExists}, Api={ApiExists}, Model={ModelExists}).",
                    File.Exists(python), File.Exists(script), Directory.Exists(modelDirectory));
                return false;
            }

            Directory.CreateDirectory(workDirectory);
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

            ClearOutputTail();
            _process?.Dispose();
            _process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
            _process.OutputDataReceived += (_, eventArgs) =>
            {
                if (!string.IsNullOrWhiteSpace(eventArgs.Data))
                    CaptureProcessOutput("stdout", eventArgs.Data);
            };
            _process.ErrorDataReceived += (_, eventArgs) =>
            {
                if (!string.IsNullOrWhiteSpace(eventArgs.Data))
                    CaptureProcessOutput("stderr", eventArgs.Data);
            };

            if (!_process.Start())
                return false;
            _process.BeginOutputReadLine();
            _process.BeginErrorReadLine();
            _logger.LogInformation("Starting IndexTTS2 service (PID={ProcessId}, Url={BaseUrl}).", _process.Id, _options.BaseUrl);

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(Math.Max(5, _options.StartupTimeoutSeconds)));
            try
            {
                while (!timeout.IsCancellationRequested && !_process.HasExited)
                {
                    if (await IsReadyAsync(timeout.Token))
                    {
                        _isReady = true;
                        _logger.LogInformation("IndexTTS2 service is ready ({BaseUrl}).", _options.BaseUrl);
                        return true;
                    }
                    await Task.Delay(TimeSpan.FromSeconds(2), timeout.Token);
                }
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // Convert the startup timeout into a clear false result below.
            }

            var exitCode = _process.HasExited ? _process.ExitCode.ToString() : "still-running";
            _logger.LogWarning(
                "IndexTTS2 did not become ready (Url={BaseUrl}, ExitCode={ExitCode}). Recent process output:{NewLine}{OutputTail}",
                _options.BaseUrl,
                exitCode,
                Environment.NewLine,
                GetOutputTail());
            TerminateManagedProcess();
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
                _logger.LogDebug(ex, "Stopping IndexTTS2 through HTTP failed; terminating the managed process instead.");
            }

            TerminateManagedProcess();
            _process?.Dispose();
            _process = null;
            _logger.LogInformation("IndexTTS2 stopped and released its process resources.");
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => StopForEngineSwitchAsync(cancellationToken);

    private async Task<bool> IsReadyAsync(CancellationToken cancellationToken)
    {
        try
        {
            var baseUri = new Uri(_options.BaseUrl.TrimEnd('/') + "/", UriKind.Absolute);
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(baseUri, "health"));
            using var response = await _httpClientFactory.CreateClient().SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
                return false;

            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            using var document = JsonDocument.Parse(json);
            return document.RootElement.TryGetProperty("engine", out var engine) &&
                   string.Equals(engine.GetString(), "index-tts2", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static async Task<bool> IsTcpPortOccupiedAsync(Uri baseUri, CancellationToken cancellationToken)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(1));
            using var client = new TcpClient();
            await client.ConnectAsync(baseUri.Host, baseUri.Port, timeout.Token);
            return client.Connected;
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException)
        {
            return false;
        }
    }

    private void CaptureProcessOutput(string stream, string line)
    {
        _logger.LogDebug("IndexTTS2 {Stream}: {Output}", stream, line);
        lock (_outputSync)
        {
            _processOutputTail.Enqueue($"[{stream}] {line}");
            while (_processOutputTail.Count > 40)
                _processOutputTail.Dequeue();
        }
    }

    private void ClearOutputTail()
    {
        lock (_outputSync)
            _processOutputTail.Clear();
    }

    private string GetOutputTail()
    {
        lock (_outputSync)
            return _processOutputTail.Count == 0
                ? "(no output captured)"
                : string.Join(Environment.NewLine, _processOutputTail);
    }

    private void TerminateManagedProcess()
    {
        try
        {
            if (_process is { HasExited: false })
                _process.Kill(entireProcessTree: true);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to terminate the IndexTTS2 process tree.");
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
