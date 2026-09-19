import * as vscode from 'vscode';

export function activate(context: vscode.ExtensionContext) {
  context.subscriptions.push(vscode.window.registerWebviewViewProvider('miau.chat', new MiauView(context), {
    webviewOptions: { retainContextWhenHidden: true }
  }));
}
export function deactivate() {}

class MiauView implements vscode.WebviewViewProvider {
  private controller?: AbortController;

  constructor(private readonly context: vscode.ExtensionContext) {}

  resolveWebviewView(view: vscode.WebviewView) {
    view.webview.options = { enableScripts: true };
    const cfg = vscode.workspace.getConfiguration('miau');
    view.webview.html = html(cfg.get<string>('model') ?? 'qwen2.5-coder:7b');

    view.webview.onDidReceiveMessage(async m => {
      if (m.type === 'cancel') {
        this.controller?.abort();
        view.webview.postMessage({ type: 'cancelled' });
        return;
      }
      if (m.type !== 'ask') return;

      const folder = vscode.workspace.workspaceFolders?.[0]?.uri.fsPath;
      if (!folder) return view.webview.postMessage({ type: 'error', text: 'Abra uma pasta no VS Code.' });

      const current = vscode.workspace.getConfiguration('miau');
      const api = current.get<string>('apiUrl') ?? 'http://127.0.0.1:11435';
      const model = current.get<string>('model') ?? 'qwen2.5-coder:7b';
      this.controller = new AbortController();

      try {
        view.webview.postMessage({ type: 'started', model, prompt: m.text });
        const res = await fetch(api + '/agent/run', {
          method: 'POST',
          headers: { 'content-type': 'application/json' },
          body: JSON.stringify({ workspace: folder, prompt: m.text, model, maxSteps: 30 }),
          signal: this.controller.signal
        });
        const raw = await res.text();
        let body: any;
        try { body = JSON.parse(raw); } catch { body = { answer: raw }; }
        if (!res.ok) throw new Error(body?.detail ?? body?.answer ?? raw);
        view.webview.postMessage({ type: 'answer', text: body.answer ?? 'Concluído.', events: body.events ?? [], model });
      } catch (e: any) {
        if (e?.name === 'AbortError') view.webview.postMessage({ type: 'cancelled' });
        else view.webview.postMessage({ type: 'error', text: e?.message ?? String(e) });
      } finally {
        this.controller = undefined;
      }
    });
  }
}

function html(model: string) {
  const safeModel = model.replace(/[&<>"']/g, c => ({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[c]!));
  return `<!doctype html>
<html>
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width,initial-scale=1">
<style>
*{box-sizing:border-box}body{margin:0;color:var(--vscode-foreground);background:var(--vscode-sideBar-background);font-family:var(--vscode-font-family);font-size:13px;height:100vh;display:flex;flex-direction:column}
header{height:46px;display:flex;align-items:center;justify-content:space-between;padding:0 12px;border-bottom:1px solid var(--vscode-widget-border)}
.brand{display:flex;align-items:center;gap:8px;font-weight:600}.cat{font-size:18px}.model{font-size:11px;color:var(--vscode-descriptionForeground)}
#thread{flex:1;overflow:auto;padding:14px 12px 120px}.empty{color:var(--vscode-descriptionForeground);padding:26px 6px}.empty b{display:block;color:var(--vscode-foreground);font-size:15px;margin-bottom:6px}
.msg{margin:0 0 18px}.who{font-size:11px;font-weight:600;margin-bottom:6px;color:var(--vscode-descriptionForeground)}.bubble{white-space:pre-wrap;line-height:1.5}
.user .bubble{background:var(--vscode-input-background);border:1px solid var(--vscode-widget-border);padding:9px 10px;border-radius:6px}
.activity{margin-top:10px;border-left:2px solid var(--vscode-widget-border);padding-left:9px;color:var(--vscode-descriptionForeground);font-size:12px}.event{padding:3px 0}
.running{display:flex;gap:7px;align-items:center;color:var(--vscode-descriptionForeground)}.dot{width:7px;height:7px;border-radius:50%;background:var(--vscode-progressBar-background);animation:pulse 1s infinite alternate}@keyframes pulse{to{opacity:.25}}
.composer{position:absolute;left:8px;right:8px;bottom:8px;background:var(--vscode-input-background);border:1px solid var(--vscode-focusBorder);border-radius:7px;overflow:hidden;box-shadow:0 4px 16px rgba(0,0,0,.18)}
textarea{display:block;width:100%;min-height:72px;max-height:180px;resize:vertical;border:0;outline:0;padding:10px;background:transparent;color:var(--vscode-input-foreground);font:inherit}
.actions{display:flex;align-items:center;justify-content:space-between;padding:6px 7px;border-top:1px solid var(--vscode-widget-border)}button{border:0;border-radius:4px;padding:5px 10px;background:var(--vscode-button-background);color:var(--vscode-button-foreground);cursor:pointer}button:hover{background:var(--vscode-button-hoverBackground)}button.secondary{background:transparent;color:var(--vscode-foreground)}button:disabled{opacity:.45;cursor:default}.hint{font-size:10px;color:var(--vscode-descriptionForeground)}
</style>
</head>
<body>
<header><div class="brand"><span class="cat">🐱</span><span>MIAU</span></div><div class="model" id="model">${safeModel}</div></header>
<div id="thread"><div class="empty" id="empty"><b>Agente de programação local</b>Peça para analisar, criar, editar, executar testes ou trabalhar com Git no projeto aberto.</div></div>
<div class="composer"><textarea id="q" placeholder="Peça ao MIAU para trabalhar..."></textarea><div class="actions"><span class="hint">Enter envia · Shift+Enter quebra linha</span><div><button class="secondary" id="stop" disabled>Parar</button><button id="send">Enviar ↑</button></div></div></div>
<script>
const v=acquireVsCodeApi(),q=document.getElementById('q'),send=document.getElementById('send'),stop=document.getElementById('stop'),thread=document.getElementById('thread'),empty=document.getElementById('empty'),md=document.getElementById('model');
let current=null;
const esc=s=>String(s??'').replace(/[&<>]/g,c=>({'&':'&amp;','<':'&lt;','>':'&gt;'}[c]));
function bottom(){thread.scrollTop=thread.scrollHeight}
function ask(){const text=q.value.trim();if(!text||send.disabled)return;q.value='';empty?.remove();const el=document.createElement('div');el.className='msg user';el.innerHTML='<div class="who">Você</div><div class="bubble">'+esc(text)+'</div>';thread.appendChild(el);current=document.createElement('div');current.className='msg assistant';current.innerHTML='<div class="who">MIAU</div><div class="running"><span class="dot"></span><span>Iniciando tarefa...</span></div><div class="activity"></div>';thread.appendChild(current);send.disabled=true;stop.disabled=false;bottom();v.postMessage({type:'ask',text})}
send.onclick=ask;stop.onclick=()=>v.postMessage({type:'cancel'});q.addEventListener('keydown',e=>{if(e.key==='Enter'&&!e.shiftKey){e.preventDefault();ask()}});
addEventListener('message',e=>{const m=e.data;if(m.model)md.textContent=m.model;if(!current)return;
 if(m.type==='started'){current.querySelector('.running span:last-child').textContent='MIAU está trabalhando...'}
 if(m.type==='answer'){current.querySelector('.running')?.remove();const b=document.createElement('div');b.className='bubble';b.textContent=m.text;current.insertBefore(b,current.querySelector('.activity'));const a=current.querySelector('.activity');(m.events||[]).forEach(x=>{const d=document.createElement('div');d.className='event';d.textContent='✓ '+(x.message||x);a.appendChild(d)});if(!(m.events||[]).length)a.remove();done()}
 if(m.type==='error'){current.querySelector('.running')?.remove();const b=document.createElement('div');b.className='bubble';b.textContent='Erro: '+m.text;current.appendChild(b);done()}
 if(m.type==='cancelled'){current.querySelector('.running')?.remove();const b=document.createElement('div');b.className='bubble';b.textContent='Tarefa interrompida.';current.appendChild(b);done()}bottom();
});
function done(){send.disabled=false;stop.disabled=true;current=null;q.focus()}
</script>
</body></html>`;
}
