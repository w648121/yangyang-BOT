using System.Diagnostics;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Hime.Services;

/// <summary>
/// Owns the loopback-only OpenCode HTTP service used by Hime. Its random local password
/// is stored beside runtime data rather than in project configuration, so a normal Hime
/// restart can safely reconnect to the same protected local server.
/// </summary>
public sealed class OpenCodeServerService : IHostedService, IDisposable
{
    private readonly OpenCodeAgentOptions _options;
    private readonly AiOptions _aiOptions;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<OpenCodeServerService> _logger;
    private readonly OpenCodeStickerCatalogPublisher _stickerCatalogPublisher;
    private readonly string _serverPassword;
    private Process? _ownedProcess;

    public OpenCodeServerService(
        IOptions<OpenCodeAgentOptions> options,
        IOptions<AiOptions> aiOptions,
        IHttpClientFactory httpClientFactory,
        OpenCodeStickerCatalogPublisher stickerCatalogPublisher,
        ILogger<OpenCodeServerService> logger)
    {
        _options = options.Value;
        _aiOptions = aiOptions.Value;
        _httpClientFactory = httpClientFactory;
        _stickerCatalogPublisher = stickerCatalogPublisher;
        _logger = logger;
        _serverPassword = LoadOrCreatePassword();
    }

    public bool IsReady { get; private set; }

    public Uri BaseUri => new(EnsureTrailingSlash(_options.BaseUrl));

    public void ApplyAuthorization(HttpRequestMessage request)
    {
        var token = Convert.ToBase64String(Encoding.UTF8.GetBytes($"opencode:{_serverPassword}"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", token);
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!_options.Enabled)
        {
            _logger.LogInformation("OpenCode agent is disabled.");
            return;
        }

        _stickerCatalogPublisher.Publish();

        if (await IsAvailableAsync(cancellationToken))
        {
            IsReady = true;
            _logger.LogInformation("Reusing the protected OpenCode agent at {BaseUrl}.", BaseUri);
            return;
        }

        if (!_options.AutoStartLocalServer)
        {
            _logger.LogWarning("OpenCode agent is enabled but its local server is unavailable at {BaseUrl}.", _options.BaseUrl);
            return;
        }

        var executablePath = ResolvePath(_options.ServerExecutablePath);
        if (string.IsNullOrWhiteSpace(executablePath) || !File.Exists(executablePath))
        {
            _logger.LogWarning("OpenCode executable was not found at {Path}.", executablePath);
            return;
        }

        var (host, port) = GetLoopbackEndpoint();
        var startInfo = CreateStartInfo(executablePath, host, port);
        startInfo.Environment["HIME_OPENCODE_BASE_URL"] = _aiOptions.BaseUrl;
        startInfo.Environment["HIME_OPENCODE_API_KEY"] = _aiOptions.ApiKey;
        startInfo.Environment["OPENCODE_SERVER_PASSWORD"] = _serverPassword;
        startInfo.Environment["HIME_STICKER_CATALOG_PATH"] = _stickerCatalogPublisher.SnapshotPath;

        _ownedProcess = Process.Start(startInfo);
        if (_ownedProcess is null)
        {
            _logger.LogWarning("Could not start the local OpenCode server.");
            return;
        }

        _ownedProcess.OutputDataReceived += (_, eventArgs) =>
        {
            if (!string.IsNullOrWhiteSpace(eventArgs.Data))
                _logger.LogDebug("OpenCode: {Message}", eventArgs.Data);
        };
        _ownedProcess.ErrorDataReceived += (_, eventArgs) =>
        {
            if (!string.IsNullOrWhiteSpace(eventArgs.Data))
                _logger.LogWarning("OpenCode: {Message}", eventArgs.Data);
        };
        _ownedProcess.BeginOutputReadLine();
        _ownedProcess.BeginErrorReadLine();

        var timeout = TimeSpan.FromSeconds(Math.Clamp(_options.StartupTimeoutSeconds, 5, 90));
        var deadline = DateTimeOffset.UtcNow.Add(timeout);
        while (DateTimeOffset.UtcNow < deadline && !cancellationToken.IsCancellationRequested)
        {
            if (await IsAvailableAsync(cancellationToken))
            {
                IsReady = true;
                _logger.LogInformation("Restricted OpenCode agent is ready at {BaseUrl}.", BaseUri);
                return;
            }

            if (_ownedProcess.HasExited)
            {
                _logger.LogWarning("OpenCode server exited during startup (ExitCode={ExitCode}).", _ownedProcess.ExitCode);
                return;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken);
        }

        _logger.LogWarning("OpenCode server did not become ready at {BaseUrl}.", BaseUri);
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        IsReady = false;
        if (_ownedProcess is { HasExited: false })
        {
            try
            {
                _ownedProcess.Kill(entireProcessTree: true);
                _ownedProcess.WaitForExit(3000);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Could not stop the locally owned OpenCode server cleanly.");
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
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(BaseUri, "global/health"));
            ApplyAuthorization(request);
            using var response = await _httpClientFactory.CreateClient().SendAsync(request, timeout.Token);
            return response.IsSuccessStatusCode;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private ProcessStartInfo CreateStartInfo(string executablePath, string host, int port)
    {
        var isPowerShellScript = string.Equals(Path.GetExtension(executablePath), ".ps1", StringComparison.OrdinalIgnoreCase);
        var startInfo = new ProcessStartInfo
        {
            FileName = isPowerShellScript ? "powershell.exe" : executablePath,
            WorkingDirectory = AppContext.BaseDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        if (isPowerShellScript)
        {
            startInfo.ArgumentList.Add("-NoProfile");
            startInfo.ArgumentList.Add("-ExecutionPolicy");
            startInfo.ArgumentList.Add("Bypass");
            startInfo.ArgumentList.Add("-File");
            startInfo.ArgumentList.Add(executablePath);
        }
        startInfo.ArgumentList.Add("serve");
        startInfo.ArgumentList.Add("--hostname");
        startInfo.ArgumentList.Add(host);
        startInfo.ArgumentList.Add("--port");
        startInfo.ArgumentList.Add(port.ToString());
        return startInfo;
    }

    private (string Host, int Port) GetLoopbackEndpoint()
    {
        if (!Uri.TryCreate(_options.BaseUrl, UriKind.Absolute, out var uri) || !uri.IsLoopback)
            throw new InvalidOperationException("OpenCodeAgent:BaseUrl must be a loopback HTTP URL.");

        return (uri.Host, uri.Port);
    }

    private static string? ResolvePath(string? configuredPath)
    {
        if (string.IsNullOrWhiteSpace(configuredPath))
            return null;
        return Path.IsPathFullyQualified(configuredPath)
            ? configuredPath
            : Path.GetFullPath(configuredPath, AppContext.BaseDirectory);
    }

    private static string EnsureTrailingSlash(string baseUrl) =>
        baseUrl.EndsWith("/", StringComparison.Ordinal) ? baseUrl : baseUrl + "/";

    private static string LoadOrCreatePassword()
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "data");
        var path = Path.Combine(directory, "opencode-server.password");
        if (File.Exists(path))
        {
            var existing = File.ReadAllText(path).Trim();
            if (existing.Length >= 32)
                return existing;
        }

        Directory.CreateDirectory(directory);
        var generated = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        File.WriteAllText(path, generated + Environment.NewLine, Encoding.UTF8);
        return generated;
    }
}
