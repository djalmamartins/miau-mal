using System.Net;
using System.Text;
using System.Text.Json;

namespace Miau.Desktop;

public sealed class EvolutionDashboardService : IAsyncDisposable
{
    readonly EvolutionService evolution; readonly string model; readonly HttpListener listener = new(); CancellationTokenSource? cts;
    public const string Url = "http://127.0.0.1:17891/";
    public EvolutionDashboardService(EvolutionService evolution, string model) { this.evolution = evolution; this.model = model; listener.Prefixes.Add(Url); }
    public void Start() { if (listener.IsListening) return; cts = new(); listener.Start(); _ = Loop(cts.Token); }
    async Task Loop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested) try { var context = await listener.GetContextAsync().WaitAsync(ct); _ = Respond(context, ct); } catch (OperationCanceledException) { break; } catch { if (!listener.IsListening) break; }
    }
    async Task Respond(HttpListenerContext context, CancellationToken ct)
    {
        try
        {
            var path = context.Request.Url?.AbsolutePath ?? "/";
            if (path == "/") await Write(context, DashboardHtml, "text/html; charset=utf-8", ct);
            else if (path is "/api/evolution" or "/api/evolution/history" or "/api/dataset/stats" or "/api/memory" or "/api/recovery" or "/api/activity")
            {
                var snapshot = await evolution.GetSnapshotAsync(model, path == "/api/evolution", ct);
                object payload = path switch { "/api/evolution/history" => snapshot.History, "/api/dataset/stats" => new { snapshot.DatasetCompleted, snapshot.DatasetRecovery, snapshot.DatasetRejected, snapshot.DatasetReview, snapshot.AverageQualityScore }, "/api/memory" => new { snapshot.Memories }, "/api/recovery" => new { snapshot.Failures, snapshot.RecoveredFailures }, "/api/activity" => snapshot.RecentActivity, _ => snapshot };
                await Write(context, JsonSerializer.Serialize(payload), "application/json; charset=utf-8", ct);
            }
            else { context.Response.StatusCode = 404; context.Response.Close(); }
        }
        catch { try { context.Response.StatusCode = 500; context.Response.Close(); } catch { } }
    }
    static async Task Write(HttpListenerContext context, string content, string type, CancellationToken ct) { var bytes = Encoding.UTF8.GetBytes(content); context.Response.ContentType = type; context.Response.ContentLength64 = bytes.Length; await context.Response.OutputStream.WriteAsync(bytes, ct); context.Response.Close(); }
    public ValueTask DisposeAsync() { cts?.Cancel(); listener.Close(); cts?.Dispose(); return ValueTask.CompletedTask; }

    const string DashboardHtml = """
<!doctype html><html lang="pt-BR"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width"><title>MIAU1-Coder Evolution</title><style>
*{box-sizing:border-box}body{margin:0;background:#050607;color:#f4f4f5;font:14px system-ui,sans-serif}header{padding:22px 30px;border-bottom:1px solid #24272c;display:flex;justify-content:space-between}h1{margin:0;font-size:21px}.yellow{color:#ffc400}.wrap{padding:26px;max-width:1400px;margin:auto}.cards{display:grid;grid-template-columns:repeat(4,1fr);gap:12px}.card{background:#0d0f11;border:1px solid #24282d;border-radius:12px;padding:16px}.value{font-size:28px;font-weight:700;margin-top:7px}.grid{display:grid;grid-template-columns:1.25fr 1fr;gap:14px;margin-top:14px}canvas{width:100%;height:310px;background:radial-gradient(circle,#121821,#080a0d);border-radius:10px}.skill{display:grid;grid-template-columns:1fr auto;gap:5px;padding:9px 0;border-bottom:1px solid #24272c}.muted{color:#9298a2}.bar{height:5px;background:#222831;border-radius:4px;grid-column:1/3}.fill{height:100%;background:#ffc400;border-radius:4px}.activity{padding:8px 0;border-bottom:1px solid #202328}@media(max-width:800px){.cards{grid-template-columns:1fr 1fr}.grid{grid-template-columns:1fr}}</style></head>
<body><header><h1><span class="yellow">◉ ◉</span> MIAU1-Coder Evolution</h1><span id="version" class="muted"></span></header><main class="wrap"><section class="cards" id="cards"></section><section class="grid"><div class="card"><b>Rede de evolução <span class="muted">· metáfora visual</span></b><canvas id="brain" width="700" height="310"></canvas></div><div class="card"><b>Habilidades baseadas em evidências</b><div id="skills"></div></div><div class="card"><b>Dataset</b><div id="dataset"></div></div><div class="card"><b>Atividade real recente</b><div id="activity"></div></div></section></main><script>
fetch('/api/evolution').then(r=>r.json()).then(s=>{version.textContent=s.AgentVersion+' · '+s.Model;let items=[['Tarefas concluídas',s.TasksCompleted],['Tarefas falhas',s.TasksFailed],['Taxa de sucesso',s.SuccessRate+'%'],['Experiências',s.Experiences],['Memórias',s.Memories],['Recoveries',s.RecoveredFailures],['Dataset',s.DatasetCompleted],['Quality médio',s.AverageQualityScore>0?s.AverageQualityScore.toFixed(1):(s.DatasetCompleted>0?'legado / sem score':'0.0')]];cards.innerHTML=items.map(x=>`<div class=card><span class=muted>${x[0]}</span><div class=value>${x[1]}</div></div>`).join('');skills.innerHTML=s.Skills.map(x=>`<div class=skill><span>${x.Name}</span><span>${x.Successes}/${x.Attempts} · ${x.SuccessRate}%</span><div class=bar><div class=fill style="width:${x.SuccessRate}%"></div></div></div>`).join('');dataset.innerHTML=`<p>Completed: ${s.DatasetCompleted}</p><p>Recovery: ${s.DatasetRecovery}</p><p>Rejected: ${s.DatasetRejected}</p><p>Review: ${s.DatasetReview}</p><p class=muted>${s.DatasetVersion}</p>`;activity.innerHTML=s.RecentActivity.length?s.RecentActivity.map(x=>{let d=document.createElement('div');d.className='activity';d.textContent=x;return d.outerHTML}).join(''):'<p class=muted>Nenhuma atividade registrada.</p>';draw(s)});function draw(s){let c=brain,x=c.getContext('2d'),n=Math.min(80,8+s.Experiences+s.Memories),pts=[];for(let i=0;i<n;i++){let a=i*2.399,r=20+Math.sqrt(i)*20;pts.push([350+Math.cos(a)*r,155+Math.sin(a)*r*.55])}x.strokeStyle='#334155';pts.forEach((p,i)=>{for(let j=i+1;j<Math.min(n,i+4);j++){x.beginPath();x.moveTo(...p);x.lineTo(...pts[j]);x.stroke()}});x.fillStyle='#ffc400';pts.forEach(p=>{x.beginPath();x.arc(...p,2.5,0,7);x.fill()})}</script></body></html>
""";
}
