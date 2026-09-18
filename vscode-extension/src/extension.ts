import * as vscode from 'vscode';
export function activate(context:vscode.ExtensionContext){context.subscriptions.push(vscode.window.registerWebviewViewProvider('miau.chat',new MiauView()));}
export function deactivate(){}
class MiauView implements vscode.WebviewViewProvider{
 resolveWebviewView(view:vscode.WebviewView){view.webview.options={enableScripts:true};view.webview.html=html();view.webview.onDidReceiveMessage(async m=>{
  if(m.type!=='ask')return;const folder=vscode.workspace.workspaceFolders?.[0]?.uri.fsPath;if(!folder)return view.webview.postMessage({type:'error',text:'Abra uma pasta no VS Code.'});
  const cfg=vscode.workspace.getConfiguration('miau'),api=cfg.get<string>('apiUrl')!,model=cfg.get<string>('model')!;
  try{view.webview.postMessage({type:'busy'});const res=await fetch(api+'/agent/run',{method:'POST',headers:{'content-type':'application/json'},body:JSON.stringify({workspace:folder,prompt:m.text,model,maxSteps:30})});const body:any=await res.json();if(!res.ok)throw new Error(body?.detail??JSON.stringify(body));view.webview.postMessage({type:'answer',text:body.answer,events:body.events});}
  catch(e:any){view.webview.postMessage({type:'error',text:e.message??String(e)});}
 });}
}
function html(){return `<!doctype html><html><head><meta charset="utf-8"><style>body{font-family:var(--vscode-font-family);padding:12px}textarea{box-sizing:border-box;width:100%;min-height:120px;background:var(--vscode-input-background);color:var(--vscode-input-foreground);padding:10px}button{width:100%;margin-top:8px;padding:8px}#log{white-space:pre-wrap;margin-top:14px;line-height:1.45}.muted{opacity:.7}</style></head><body><h3>MIAU</h3><div class="muted">Qwen3-Coder · agente local</div><textarea id="q" placeholder="Analise o projeto, implemente e rode os testes"></textarea><button id="go">Executar</button><div id="log"></div><script>const v=acquireVsCodeApi(),q=document.getElementById('q'),g=document.getElementById('go'),l=document.getElementById('log');g.onclick=()=>{if(q.value.trim()){l.textContent='MIAU está trabalhando...';v.postMessage({type:'ask',text:q.value})}};addEventListener('message',e=>{const m=e.data;if(m.type==='answer')l.textContent=m.text+'\\n\\n'+(m.events||[]).map(x=>'• '+x.message).join('\\n');if(m.type==='error')l.textContent='Erro: '+m.text;});</script></body></html>`;}
