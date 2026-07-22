using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Hime.Services;

/// <summary>A dependency-free, loopback-only HTTP dashboard.</summary>
public sealed class DiagnosticsDashboardService : BackgroundService
{
    private readonly RuntimeStatusService _status;
    private readonly DiagnosticsDashboardOptions _options;
    private readonly ILogger<DiagnosticsDashboardService> _logger;
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private TcpListener? _listener;

    public DiagnosticsDashboardService(
        RuntimeStatusService status,
        IOptions<DiagnosticsDashboardOptions> options,
        ILogger<DiagnosticsDashboardService> logger)
    {
        _status = status;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
            return;

        var port = Math.Clamp(_options.Port, 1024, 65535);
        _listener = new TcpListener(IPAddress.Loopback, port);
        _listener.Start(Math.Clamp(_options.Backlog, 4, 128));
        _logger.LogInformation("Hime diagnostics dashboard is listening on http://127.0.0.1:{Port}/", port);

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                var client = await _listener.AcceptTcpClientAsync(stoppingToken);
                _ = HandleClientAsync(client, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        catch (SocketException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    public override Task StopAsync(CancellationToken cancellationToken)
    {
        _listener?.Stop();
        return base.StopAsync(cancellationToken);
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken cancellationToken)
    {
        using (client)
        {
            try
            {
                client.ReceiveTimeout = 5000;
                client.SendTimeout = 5000;
                await using var stream = client.GetStream();
                using var reader = new StreamReader(stream, Encoding.ASCII, false, 1024, leaveOpen: true);
                var requestLine = await reader.ReadLineAsync(cancellationToken);
                string? header;
                do
                {
                    header = await reader.ReadLineAsync(cancellationToken);
                } while (!string.IsNullOrEmpty(header));

                if (string.IsNullOrWhiteSpace(requestLine) || !requestLine.StartsWith("GET ", StringComparison.Ordinal))
                {
                    await WriteResponseAsync(stream, 405, "text/plain; charset=utf-8", "Only GET is supported.", cancellationToken);
                    return;
                }

                var path = requestLine.Split(' ', StringSplitOptions.RemoveEmptyEntries).ElementAtOrDefault(1) ?? "/";
                if (path.StartsWith("/api/status", StringComparison.OrdinalIgnoreCase))
                {
                    var json = JsonSerializer.Serialize(_status.Snapshot(), _jsonOptions);
                    await WriteResponseAsync(stream, 200, "application/json; charset=utf-8", json, cancellationToken);
                }
                else if (path is "/" or "/index.html")
                {
                    await WriteResponseAsync(stream, 200, "text/html; charset=utf-8", DashboardHtml, cancellationToken);
                }
                else
                {
                    await WriteResponseAsync(stream, 404, "text/plain; charset=utf-8", "Not found.", cancellationToken);
                }
            }
            catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException)
            {
                _logger.LogDebug(ex, "Diagnostics dashboard client disconnected");
            }
        }
    }

    private static async Task WriteResponseAsync(
        NetworkStream stream,
        int statusCode,
        string contentType,
        string content,
        CancellationToken cancellationToken)
    {
        var body = Encoding.UTF8.GetBytes(content);
        var reason = statusCode switch { 200 => "OK", 404 => "Not Found", 405 => "Method Not Allowed", _ => "Error" };
        var headers = Encoding.ASCII.GetBytes(
            $"HTTP/1.1 {statusCode} {reason}\r\nContent-Type: {contentType}\r\nContent-Length: {body.Length}\r\nCache-Control: no-store\r\nConnection: close\r\n\r\n");
        await stream.WriteAsync(headers, cancellationToken);
        await stream.WriteAsync(body, cancellationToken);
    }

    private const string DashboardHtml = """
<!doctype html><html lang="zh-CN"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
<title>Hime 运行监控</title><style>
:root{color-scheme:dark;font-family:Inter,"Microsoft YaHei",sans-serif;background:#120d16;color:#f7eefa}body{margin:0;padding:24px;background:radial-gradient(circle at top,#3a1d39,#120d16 55%)}main{max-width:1180px;margin:auto}.top{display:flex;justify-content:space-between;align-items:end;gap:16px}.muted{color:#bcaabd}.grid{display:grid;grid-template-columns:repeat(auto-fit,minmax(190px,1fr));gap:12px;margin:18px 0}.card{background:#251827dd;border:1px solid #633b62;border-radius:14px;padding:16px;box-shadow:0 10px 30px #0005}.value{font-size:25px;font-weight:750;color:#ff83cb}.ok{color:#66d9a8}.bad{color:#ff7e91}.alert{border:1px solid #96632e;background:#3b291b;padding:10px 12px;border-radius:10px;margin:7px 0}.alert.error{border-color:#a43f55;background:#3b1922}table{width:100%;border-collapse:collapse}th,td{text-align:right;padding:9px;border-bottom:1px solid #493048}th:first-child,td:first-child{text-align:left}.pill{display:inline-block;padding:4px 9px;border-radius:20px;background:#3b263b;margin:3px}code{color:#ffc6e8}h1,h2{margin:.2em 0}#error{color:#ff7e91}</style></head><body><main>
<div class="top"><div><h1>Hime 运行监控</h1><div class="muted">仅限本机访问 · 每 2 秒刷新 · 不记录聊天内容</div></div><div id="updated" class="muted"></div></div>
<div id="error"></div><section id="alerts"></section><section class="grid" id="summary"></section><section class="card"><h2>服务</h2><div id="services"></div></section><section class="card" style="margin-top:12px"><h2>阶段耗时</h2><table><thead><tr><th>阶段</th><th>完成</th><th>失败</th><th>活动</th><th>平均</th><th>P50</th><th>P95</th><th>最大</th></tr></thead><tbody id="metrics"></tbody></table></section>
<section class="card" style="margin-top:12px"><h2>计数器</h2><div id="counters"></div></section><section class="card" style="margin-top:12px"><h2>最近运行事件</h2><div id="events" class="muted"></div></section>
</main><script>
const el=(id)=>document.getElementById(id), ms=(v)=>`${Math.round(v||0)} ms`, card=(n,v)=>`<div class="card"><div class="muted">${n}</div><div class="value">${v}</div></div>`;
async function refresh(){try{const s=await fetch('/api/status',{cache:'no-store'}).then(r=>r.json());el('error').textContent='';const q=s.queues,d=s.diagnostics;
el('updated').textContent=new Date(s.capturedAt).toLocaleString();el('alerts').innerHTML=(s.alerts||[]).map(a=>`<div class="alert ${a.severity}">${a.message}</div>`).join('');el('summary').innerHTML=card('消息队列',q.messagesPending)+card('延迟回复',q.scheduledReplies)+card('语音队列',q.voiceJobs)+card('待补发语音',q.persistentVoicePending)+card('语音缓存',`${s.voiceCache.fileCount} / ${s.voiceCache.sizeMb.toFixed(1)} MB`)+card('缓存命中率',`${s.voiceCache.hitRatePercent.toFixed(1)}%`)+card('表情分析',q.stickerAnalysis)+card('数据库写入',q.databaseWrites)+card('工作集',`${s.workingSetMb.toFixed(1)} MB`);
el('services').innerHTML=Object.entries(s.services).map(([p,ok])=>`<span class="pill ${ok?'ok':'bad'}">${p} ${ok?'正常':'未就绪'}</span>`).join('')+`<span class="pill ${s.openCodeCircuit.isOpen?'bad':'ok'}">OpenCode 熔断 ${s.openCodeCircuit.isOpen?'开启':'关闭'}</span>`;
el('metrics').innerHTML=Object.entries(d.metrics).map(([n,m])=>`<tr><td><code>${n}</code></td><td>${m.completed}</td><td>${m.failed}</td><td>${m.active}</td><td>${ms(m.averageMs)}</td><td>${ms(m.p50Ms)}</td><td>${ms(m.p95Ms)}</td><td>${ms(m.maximumMs)}</td></tr>`).join('');
el('counters').innerHTML=Object.entries(d.counters).map(([n,v])=>`<span class="pill"><code>${n}</code> ${v}</span>`).join('')||'<span class="muted">暂无</span>';
el('events').innerHTML=d.recentEvents.slice().reverse().map(x=>`<div>${new Date(x.time).toLocaleTimeString()} · ${x.kind} · <code>${x.correlationId||'-'}</code> · ${x.detail}</div>`).join('')||'暂无';}catch(e){el('error').textContent='读取状态失败：'+e.message}}
refresh();setInterval(refresh,2000);
</script></body></html>
""";
}

public sealed class DiagnosticsDashboardOptions
{
    public bool Enabled { get; set; } = true;
    public int Port { get; set; } = 8770;
    public int Backlog { get; set; } = 32;
    public int AiP95WarningMs { get; set; } = 15000;
    public int VoiceP95WarningMs { get; set; } = 15000;
    public int MessageQueueWarning { get; set; } = 10;
    public int VoiceQueueWarning { get; set; } = 4;
}
