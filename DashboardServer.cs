using System.Net;
using System.Text;
using System.Text.Json;

namespace MailLoadTester;

/// <summary>
/// Lightweight HTTP dashboard using HttpListener (no external deps).
/// Serves a live-updating HTML page with test progress.
/// </summary>
public sealed class DashboardServer : IDisposable
{
    private readonly HttpListener _listener;
    private readonly Thread _thread;
    private volatile DashboardState _state = new();
    private bool _disposed;

    public int Port { get; }
    public string Url => $"http://localhost:{Port}/";

    public DashboardServer(int port)
    {
        Port = port;
        _listener = new HttpListener();
        _listener.Prefixes.Add($"http://localhost:{port}/");
        _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        _thread = new Thread(ProcessRequests) { IsBackground = true };
    }

    public void Start()
    {
        _listener.Start();
        _thread.Start();
    }

    public void Update(DashboardState state) => _state = state;

    private void ProcessRequests()
    {
        while (_listener.IsListening && !_disposed)
        {
            try
            {
                var ctx = _listener.GetContext();
                _ = Task.Run(() => HandleAsync(ctx));
            }
            catch
            {
                // Classified as "shutdown": GetContext() throws once _listener.Stop() has
                // been called (from Dispose), which is the normal way this loop ends —
                // the while condition re-checks IsListening/_disposed right after and exits.
            }
        }
    }

    private async Task HandleAsync(HttpListenerContext ctx)
    {
        try
        {
            var req = ctx.Request;
            var resp = ctx.Response;
            var path = req.Url?.AbsolutePath ?? "/";

            if (path == "/api/state")
            {
                var json = JsonSerializer.Serialize(_state, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
                var bytes = Encoding.UTF8.GetBytes(json);
                resp.ContentType = "application/json";
                resp.ContentLength64 = bytes.Length;
                await resp.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
            }
            else
            {
                var html = BuildHtml();
                var bytes = Encoding.UTF8.GetBytes(html);
                resp.ContentType = "text/html; charset=utf-8";
                resp.ContentLength64 = bytes.Length;
                await resp.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
            }
            resp.Close();
        }
        catch
        {
            // Classified as "expected fallback": the browser tab closing mid-request or
            // the listener shutting down mid-write is normal for a best-effort convenience
            // dashboard (see SmtpTestRunner's dashboard-start try/catch) — a failed HTTP
            // response here must never affect the actual mail-sending test.
        }
    }

    private static string BuildHtml() => @"<!DOCTYPE html>
<html><head><meta charset='utf-8'><title>MailLoadTester Dashboard</title>
<style>
body{font-family:system-ui,sans-serif;max-width:1000px;margin:2rem auto;padding:0 1rem;background:#0f172a;color:#e2e8f0}
h1{color:#38bdf8} .grid{display:grid;grid-template-columns:repeat(auto-fit,minmax(220px,1fr));gap:1rem;margin:1rem 0}
.card{background:#1e293b;border-radius:8px;padding:1rem} .label{color:#94a3b8;font-size:.85rem}
.value{font-size:1.6rem;font-weight:700;color:#f8fafc} .ok{color:#4ade80} .fail{color:#f87171}
.bar{height:8px;background:#334155;border-radius:4px;overflow:hidden;margin-top:.5rem}
.bar>div{height:100%;background:#38bdf8;transition:width .5s}
#log{background:#0b1220;border:1px solid #334155;border-radius:6px;padding:1rem;height:300px;overflow:auto;font-family:monospace;font-size:.85rem;white-space:pre-wrap}
</style></head><body>
<h1>📧 MailLoadTester Live Dashboard</h1>
<div class='grid'>
<div class='card'><div class='label'>Sent</div><div class='value ok' id='sent'>0</div></div>
<div class='card'><div class='label'>Failed</div><div class='value fail' id='failed'>0</div></div>
<div class='card'><div class='label'>Progress</div><div class='value' id='pct'>0%</div><div class='bar'><div id='bar' style='width:0%'></div></div></div>
<div class='card'><div class='label'>Throughput</div><div class='value' id='tput'>0 msg/s</div></div>
<div class='card'><div class='label'>ETA</div><div class='value' id='eta'>--</div></div>
<div class='card'><div class='label'>Phase</div><div class='value' id='phase'>Idle</div></div>
</div>
<div id='log'>Waiting for data...</div>
<script>
let logLines=[];
async function poll(){
try{
const r=await fetch('/api/state');
const d=await r.json();
document.getElementById('sent').textContent=d.sent;
document.getElementById('failed').textContent=d.failed;
document.getElementById('pct').textContent=(d.progressPct||0)+'%';
document.getElementById('bar').style.width=(d.progressPct||0)+'%';
document.getElementById('tput').textContent=(d.throughputPerSec||0).toFixed(2)+' msg/s';
document.getElementById('eta').textContent=d.etaSeconds?d.etaSeconds.toFixed(0)+'s':'--';
document.getElementById('phase').textContent=d.phase||'Idle';
if(d.logLine){logLines.push(d.logLine);if(logLines.length>100)logLines.shift();document.getElementById('log').textContent=logLines.join('\n');}
}catch(e){}
setTimeout(poll,500);
}poll();
</script></body></html>";

    public void Dispose()
    {
        _disposed = true;
        _listener.Stop();
        _listener.Close();
    }
}

public sealed record DashboardState(
    int Sent,
    int Failed,
    int Requested,
    double ProgressPct,
    double ThroughputPerSec,
    double? EtaSeconds,
    string Phase,
    string? LogLine = null);
