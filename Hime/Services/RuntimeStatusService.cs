using System.Diagnostics;
using System.Net.NetworkInformation;
using Hime.Data.Services;
using Microsoft.Extensions.Options;

namespace Hime.Services;

/// <summary>Builds a sanitized operational snapshot shared by the dashboard and admin command.</summary>
public sealed class RuntimeStatusService
{
    private readonly RuntimeDiagnostics _diagnostics;
    private readonly ConversationMessageDispatcher _messages;
    private readonly ScheduledReplyDispatcher _scheduled;
    private readonly AutoVoiceDeliveryService _voice;
    private readonly VoiceOutboxStore _voiceOutbox;
    private readonly VoiceCacheMaintenanceService _voiceCache;
    private readonly GroupStickerCollector _stickers;
    private readonly LiteDbWriteBehindService _databaseWrites;
    private readonly OpenCodeServerService _openCodeServer;
    private readonly OpenCodeAgentClient _openCodeAgent;
    private readonly RuntimeDiagnosticsOptions _runtimeOptions;
    private readonly DiagnosticsDashboardOptions _dashboard;

    public RuntimeStatusService(
        RuntimeDiagnostics diagnostics,
        ConversationMessageDispatcher messages,
        ScheduledReplyDispatcher scheduled,
        AutoVoiceDeliveryService voice,
        VoiceOutboxStore voiceOutbox,
        VoiceCacheMaintenanceService voiceCache,
        GroupStickerCollector stickers,
        LiteDbWriteBehindService databaseWrites,
        OpenCodeServerService openCodeServer,
        OpenCodeAgentClient openCodeAgent,
        IOptions<RuntimeDiagnosticsOptions> runtimeOptions,
        IOptions<DiagnosticsDashboardOptions> dashboard)
    {
        _diagnostics = diagnostics;
        _messages = messages;
        _scheduled = scheduled;
        _voice = voice;
        _voiceOutbox = voiceOutbox;
        _voiceCache = voiceCache;
        _stickers = stickers;
        _databaseWrites = databaseWrites;
        _openCodeServer = openCodeServer;
        _openCodeAgent = openCodeAgent;
        _runtimeOptions = runtimeOptions.Value;
        _dashboard = dashboard.Value;
    }

    public RuntimeStatusSnapshot Snapshot()
    {
        using var process = Process.GetCurrentProcess();
        var diagnostics = _diagnostics.Snapshot();
        var listeningPorts = IPGlobalProperties.GetIPGlobalProperties()
            .GetActiveTcpListeners()
            .Select(endpoint => endpoint.Port)
            .ToHashSet();
        var services = _runtimeOptions.DependencyPorts
            .Where(port => port is > 0 and <= 65535)
            .Distinct()
            .ToDictionary(
                port => port.ToString(),
                port => listeningPorts.Contains(port));
        var circuit = _openCodeAgent.CircuitStatus;

        var queues = new RuntimeQueueSnapshot(
            _messages.PendingCount,
            _messages.BusyConversationCount,
            _messages.PartitionCount,
            _scheduled.PendingCount,
            _voice.PendingCount,
            _voiceOutbox.PendingCount,
            _voiceOutbox.FailedCount,
            _stickers.PendingAnalysisCount,
            _databaseWrites.PendingCount);
        var hits = diagnostics.Counters.GetValueOrDefault("voice.cache.hit");
        var misses = diagnostics.Counters.GetValueOrDefault("voice.cache.miss");
        var cache = new RuntimeVoiceCacheSnapshot(
            _voiceCache.CacheFileCount,
            Math.Round(_voiceCache.CacheBytes / 1024d / 1024d, 1),
            hits,
            misses,
            hits + misses == 0 ? 0 : Math.Round(hits * 100d / (hits + misses), 1));
        var alerts = BuildAlerts(diagnostics, queues, services);

        return new RuntimeStatusSnapshot(
            DateTimeOffset.UtcNow,
            diagnostics.StartedAt,
            Environment.ProcessId,
            Math.Round(process.WorkingSet64 / 1024d / 1024d, 1),
            Math.Round(process.PrivateMemorySize64 / 1024d / 1024d, 1),
            queues,
            cache,
            services,
            _openCodeServer.IsReady,
            circuit,
            _dashboard.Enabled ? $"http://127.0.0.1:{Math.Clamp(_dashboard.Port, 1024, 65535)}/" : null,
            alerts,
            diagnostics);
    }

    public string BuildTextReport()
    {
        var status = Snapshot();
        var uptime = status.CapturedAt - status.StartedAt;
        var ai = status.Diagnostics.Metrics.GetValueOrDefault("ai.generate");
        var message = status.Diagnostics.Metrics.GetValueOrDefault("message.process");
        var ports = string.Join("，", status.Services.Select(pair => $"{pair.Key}:{(pair.Value ? "正常" : "未就绪")}"));
        return $"""
            Hime 运行监控
            PID：{status.ProcessId}，运行 {uptime:dd\.hh\:mm\:ss}
            内存：工作集 {status.WorkingSetMb:F1} MB，私有 {status.PrivateMemoryMb:F1} MB
            队列：消息 {status.Queues.MessagesPending}，延迟回复 {status.Queues.ScheduledReplies}，语音 {status.Queues.VoiceJobs}，待补发 {status.Queues.PersistentVoicePending}，表情分析 {status.Queues.StickerAnalysis}，数据库 {status.Queues.DatabaseWrites}
            语音缓存：{status.VoiceCache.FileCount} 个 / {status.VoiceCache.SizeMb:F1} MB，命中率 {status.VoiceCache.HitRatePercent:F1}%
            消息耗时：P50 {message?.P50Ms ?? 0:F0} ms，P95 {message?.P95Ms ?? 0:F0} ms
            AI耗时：P50 {ai?.P50Ms ?? 0:F0} ms，P95 {ai?.P95Ms ?? 0:F0} ms
            OpenCode：{(status.OpenCodeReady ? "已连接" : "未就绪")}，熔断={(status.OpenCodeCircuit.IsOpen ? "开启" : "关闭")}
            端口：{ports}
            监控页：{status.DashboardUrl ?? "未启用"}
            告警：{(status.Alerts.Count == 0 ? "无" : string.Join("；", status.Alerts.Select(alert => alert.Message)))}
            """;
    }

    private IReadOnlyList<RuntimeAlert> BuildAlerts(
        RuntimeDiagnosticsSnapshot diagnostics,
        RuntimeQueueSnapshot queues,
        IReadOnlyDictionary<string, bool> services)
    {
        var alerts = new List<RuntimeAlert>();
        var ai = diagnostics.Metrics.GetValueOrDefault("ai.generate");
        var voice = diagnostics.Metrics.GetValueOrDefault("voice.synthesize");
        if (ai?.P95Ms >= Math.Max(1000, _dashboard.AiP95WarningMs))
            alerts.Add(new("warning", "ai-latency", $"AI P95 已达到 {ai.P95Ms:F0} ms"));
        if (voice?.P95Ms >= Math.Max(1000, _dashboard.VoiceP95WarningMs))
            alerts.Add(new("warning", "voice-latency", $"语音 P95 已达到 {voice.P95Ms:F0} ms"));
        if (queues.MessagesPending >= Math.Max(1, _dashboard.MessageQueueWarning))
            alerts.Add(new("warning", "message-backlog", $"消息积压 {queues.MessagesPending} 条"));
        if (queues.VoiceJobs >= Math.Max(1, _dashboard.VoiceQueueWarning))
            alerts.Add(new("warning", "voice-backlog", $"语音队列积压 {queues.VoiceJobs} 条"));
        if (queues.PersistentVoiceFailed > 0)
            alerts.Add(new("error", "voice-outbox-failed", $"有 {queues.PersistentVoiceFailed} 条语音已达最大重试次数"));
        var serviceNames = new Dictionary<string, string>
        {
            ["3010"] = "LLBot",
            ["3100"] = "点歌服务",
            ["8765"] = "GsCore",
            ["9882"] = "IndexTTS2",
            ["42116"] = "OpenCode"
        };
        foreach (var service in services.Where(pair => !pair.Value))
        {
            var name = serviceNames.GetValueOrDefault(service.Key, $"端口 {service.Key}");
            alerts.Add(new(
                service.Key == "3010" ? "error" : "warning",
                $"service-{service.Key}-down",
                $"{name} 未就绪"));
        }
        return alerts;
    }
}

public sealed record RuntimeStatusSnapshot(
    DateTimeOffset CapturedAt,
    DateTimeOffset StartedAt,
    int ProcessId,
    double WorkingSetMb,
    double PrivateMemoryMb,
    RuntimeQueueSnapshot Queues,
    RuntimeVoiceCacheSnapshot VoiceCache,
    IReadOnlyDictionary<string, bool> Services,
    bool OpenCodeReady,
    OpenCodeCircuitStatus OpenCodeCircuit,
    string? DashboardUrl,
    IReadOnlyList<RuntimeAlert> Alerts,
    RuntimeDiagnosticsSnapshot Diagnostics);

public sealed record RuntimeQueueSnapshot(
    long MessagesPending,
    int BusyConversations,
    int MessagePartitions,
    long ScheduledReplies,
    int VoiceJobs,
    int PersistentVoicePending,
    int PersistentVoiceFailed,
    long StickerAnalysis,
    int DatabaseWrites);

public sealed record RuntimeVoiceCacheSnapshot(
    int FileCount,
    double SizeMb,
    long Hits,
    long Misses,
    double HitRatePercent);

public sealed record RuntimeAlert(string Severity, string Code, string Message);
