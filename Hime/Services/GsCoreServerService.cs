using System.Diagnostics;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Hime.Services;

/// <summary>
/// Owns the optional local GsCore child process.  Hime remains the only QQ bot;
/// GsCore is exposed only as a loopback HTTP feature service.
/// </summary>
public sealed class GsCoreServerService : IHostedService, IDisposable
{
    private readonly ILogger<GsCoreServerService> _logger;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly GsCoreOptions _options;
    private readonly SemaphoreSlim _startGate = new(1, 1);
    private Process? _ownedProcess;
    private bool _disposed;

    public GsCoreServerService(
        ILogger<GsCoreServerService> logger,
        IHttpClientFactory httpClientFactory,
        IOptions<GsCoreOptions> options)
    {
        _logger = logger;
        _httpClientFactory = httpClientFactory;
        _options = options.Value;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!_options.Enabled)
        {
            _logger.LogInformation("GsCore bridge is disabled.");
            return;
        }

        if (!IsLoopbackBaseUrl())
            throw new InvalidOperationException("GsCore BaseUrl must use a loopback address.");

        var ready = await EnsureAvailableAsync(cancellationToken);
        if (!ready)
        {
            _logger.LogWarning(
                "GsCore did not become ready within {Timeout}s. Hime will continue running and ww commands will report that the service is unavailable.",
                Math.Max(5, _options.StartupTimeoutSeconds));
        }
    }

    public async Task<bool> EnsureAvailableAsync(CancellationToken cancellationToken = default)
    {
        if (!_options.Enabled)
            return false;

        if (await ProbeAsync(cancellationToken))
            return true;

        if (!_options.AutoStartLocalServer)
            return false;

        await _startGate.WaitAsync(cancellationToken);
        try
        {
            if (await ProbeAsync(cancellationToken))
                return true;

            if (_ownedProcess is null || _ownedProcess.HasExited)
            {
                _ownedProcess?.Dispose();
                _ownedProcess = null;
                StartOwnedProcess();
            }

            var deadline = DateTimeOffset.UtcNow.AddSeconds(Math.Max(5, _options.StartupTimeoutSeconds));
            while (DateTimeOffset.UtcNow < deadline)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (_ownedProcess is { HasExited: true })
                {
                    _logger.LogError("GsCore exited during startup with code {ExitCode}.", _ownedProcess.ExitCode);
                    return false;
                }

                if (await ProbeAsync(cancellationToken))
                {
                    _logger.LogInformation("GsCore feature service is ready at {BaseUrl}.", _options.BaseUrl);
                    return true;
                }

                await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
            }

            return false;
        }
        finally
        {
            _startGate.Release();
        }
    }

    private void StartOwnedProcess()
    {
        var executable = Path.GetFullPath(_options.CoreExecutablePath);
        var workingDirectory = Path.GetFullPath(_options.WorkingDirectory);
        if (!File.Exists(executable))
            throw new FileNotFoundException("GsCore executable was not found.", executable);
        if (!Directory.Exists(workingDirectory))
            throw new DirectoryNotFoundException($"GsCore working directory was not found: {workingDirectory}");

        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        startInfo.Environment["PYTHONUTF8"] = "1";
        startInfo.Environment["PYTHONIOENCODING"] = "utf-8";

        var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, e) =>
        {
            if (!string.IsNullOrWhiteSpace(e.Data))
                _logger.LogInformation("[GsCore] {Line}", e.Data);
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (!string.IsNullOrWhiteSpace(e.Data))
                _logger.LogWarning("[GsCore] {Line}", e.Data);
        };

        if (!process.Start())
            throw new InvalidOperationException("Unable to start GsCore.");

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        _ownedProcess = process;
        _logger.LogInformation("Started local GsCore process {ProcessId}.", process.Id);
    }

    private async Task<bool> ProbeAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(3));
            var client = _httpClientFactory.CreateClient();
            var url = new Uri(new Uri(_options.BaseUrl), "openapi.json");
            using var response = await client.GetAsync(url, timeout.Token);
            if (!response.IsSuccessStatusCode)
                return false;

            var openApi = await response.Content.ReadAsStringAsync(timeout.Token);
            return openApi.Contains("/api/send_msg", StringComparison.Ordinal);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return false;
        }
        catch (HttpRequestException)
        {
            return false;
        }
    }

    private bool IsLoopbackBaseUrl() =>
        Uri.TryCreate(_options.BaseUrl, UriKind.Absolute, out var uri) && uri.IsLoopback;

    public Task StopAsync(CancellationToken cancellationToken)
    {
        if (_ownedProcess is { HasExited: false } process)
        {
            try
            {
                process.Kill(entireProcessTree: true);
                _logger.LogInformation("Stopped owned GsCore process {ProcessId}.", process.Id);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Unable to stop owned GsCore process.");
            }
        }

        return Task.CompletedTask;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _ownedProcess?.Dispose();
        _startGate.Dispose();
    }
}
