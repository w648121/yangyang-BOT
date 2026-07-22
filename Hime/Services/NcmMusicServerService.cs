using System.Diagnostics;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Hime.Services;

/// <summary>
/// Starts the bundled ncm-api-rs executable only when its loopback endpoint is not already available.
/// The child process is stopped with Hime, while an externally started server is never touched.
/// </summary>
public sealed class NcmMusicServerService : IHostedService, IDisposable
{
    private readonly MusicOptions _options;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<NcmMusicServerService> _logger;
    private Process? _ownedProcess;

    public NcmMusicServerService(
        IOptions<MusicOptions> options,
        IHttpClientFactory httpClientFactory,
        ILogger<NcmMusicServerService> logger)
    {
        _options = options.Value;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!_options.Enabled || !_options.AutoStartLocalServer || await IsAvailableAsync(cancellationToken))
            return;

        var executablePath = ResolvePath(_options.ServerExecutablePath);
        if (string.IsNullOrWhiteSpace(executablePath) || !File.Exists(executablePath))
        {
            _logger.LogWarning("Music search is enabled but ncm-server.exe was not found at {Path}", executablePath);
            return;
        }

        var workingDirectory = ResolvePath(_options.ServerWorkingDirectory);
        if (string.IsNullOrWhiteSpace(workingDirectory) || !Directory.Exists(workingDirectory))
            workingDirectory = Path.GetDirectoryName(executablePath)!;

        var startInfo = new ProcessStartInfo
        {
            FileName = executablePath,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden
        };
        startInfo.Environment["NCM_HOST"] = _options.ServerHost;
        startInfo.Environment["NCM_PORT"] = Math.Clamp(_options.ServerPort, 1, 65535).ToString();

        _ownedProcess = Process.Start(startInfo);
        if (_ownedProcess is null)
        {
            _logger.LogWarning("Could not start the local ncm music server.");
            return;
        }

        for (var attempt = 0; attempt < 20 && !cancellationToken.IsCancellationRequested; attempt++)
        {
            if (await IsAvailableAsync(cancellationToken))
            {
                _logger.LogInformation("Local ncm music server is ready at {BaseUrl}", _options.ApiBaseUrl);
                return;
            }

            if (_ownedProcess.HasExited)
            {
                _logger.LogWarning("Local ncm music server exited during startup (ExitCode={ExitCode})", _ownedProcess.ExitCode);
                return;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken);
        }

        _logger.LogWarning("Local ncm music server did not become ready at {BaseUrl}", _options.ApiBaseUrl);
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        if (_ownedProcess is { HasExited: false })
        {
            try
            {
                _ownedProcess.Kill(entireProcessTree: true);
                _ownedProcess.WaitForExit(3000);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Could not stop the locally owned ncm music server cleanly.");
            }
        }

        return Task.CompletedTask;
    }

    public void Dispose() => _ownedProcess?.Dispose();

    private async Task<bool> IsAvailableAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(1));
            using var response = await _httpClientFactory.CreateClient().GetAsync(
                new Uri(new Uri(EnsureTrailingSlash(_options.ApiBaseUrl)), "cloudsearch?keywords=Hime&limit=1&type=1"),
                timeout.Token);
            return response.IsSuccessStatusCode;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static string? ResolvePath(string? configuredPath)
    {
        if (string.IsNullOrWhiteSpace(configuredPath))
            return null;
        if (Path.IsPathFullyQualified(configuredPath))
            return configuredPath;
        return Path.GetFullPath(configuredPath, AppContext.BaseDirectory);
    }

    private static string EnsureTrailingSlash(string baseUrl) =>
        baseUrl.EndsWith("/", StringComparison.Ordinal) ? baseUrl : baseUrl + "/";
}
