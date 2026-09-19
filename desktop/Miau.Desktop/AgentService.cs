using System.Diagnostics; using System.Net.Http.Json; using System.Text.Json; using System.Text.RegularExpressions;
namespace Miau.Desktop;
public sealed class AgentService {
 readonly HttpClient http=new(){BaseAddress=new Uri("http://127.0.0.1:11434"),Timeout=Timeout.InfiniteTimeSpan};
 static readonly string[] Allowed={"list_files","read_file","write_file","search","run_command","git_status","git_diff"};
 public async Task<string> RunAsync(string root,string prompt,CancellationToken ct,Action<string> progress){
  var messages=new List<object>{new{role="system",content=SystemPrompt},new{role="user",content=prompt}};
  for(var step=0;step<30;step++){
   progress($"● Etapa {step+1}: consultando qwen2.5-coder:7b…");
   using var r=await http.PostAsJsonAsync("/api/chat",new{model="qwen2.5-coder:7b",messages,stream=false,options=new{temperature=.1}},ct);r.EnsureSuccessStatusCode();
   using var doc=JsonDocument.Parse(await r.Content.ReadAsStringAsync(ct));var content=doc.RootElement.GetProperty("message").GetProperty("content").GetString()??"";
   var call=ParseCall(content);if(call is null)return content;
   progress($"● {call.Value.name}");var output=await Execute(root,call.Value.name,call.Value.args,ct);
   messages.Add(new{role="assistant",content});messages.Add(new{role="user",content=$"TOOL RESULT ({call.Value.name}):\n{Trim(output)}\nContinue. If finished, answer normally without JSON."});
  } return "Limite de etapas atingido.";
 }
 static (string name,JsonElement args)? ParseCall(string s){var m=Regex.Match(s,@"\{[\s\S]*\}");if(!m.Success)return null;try{using var d=JsonDocument.Parse(m.Value);var x=d.RootElement;if(!x.TryGetProperty("name",out var n)||!x.TryGetProperty("arguments",out var a))return null;var name=n.GetString();if(name is null||!Allowed.Contains(name))return null;return(name,a.Clone());}catch{return null;}}
 static async Task<string> Execute(string root,string name,JsonElement a,CancellationToken ct){
  string Arg(string n,string d="")=>a.TryGetProperty(n,out var x)?x.GetString()??d:d;
  string Safe(string p){var full=Path.GetFullPath(Path.Combine(root,p));var rr=Path.GetFullPath(root)+Path.DirectorySeparatorChar;if(full!=Path.GetFullPath(root)&&!full.StartsWith(rr))throw new InvalidOperationException("Caminho fora do projeto.");return full;}
  return name switch{
   "list_files"=>string.Join("\n",Directory.EnumerateFileSystemEntries(Safe(Arg("path","."))).Take(300).Select(Path.GetFileName)),
   "read_file"=>await File.ReadAllTextAsync(Safe(Arg("path")),ct),
   "write_file"=>await Write(Safe(Arg("path")),Arg("content"),ct),
   "search"=>string.Join("\n",Directory.EnumerateFiles(root,"*",SearchOption.AllDirectories).Where(p=>!p.Contains("/.git/")&&!p.Contains("/bin/")&&!p.Contains("/obj/")&&!p.Contains("/node_modules/")).SelectMany(p=>Find(p,Arg("query"))).Take(200)),
   "git_status"=>await Cmd(root,"git status --short --branch",ct),
   "git_diff"=>await Cmd(root,"git diff",ct),
   "run_command"=>await Cmd(root,Arg("command"),ct),
   _=>"Ferramenta desconhecida"
  };
 }
 static async Task<string> Write(string p,string c,CancellationToken ct){Directory.CreateDirectory(Path.GetDirectoryName(p)!);await File.WriteAllTextAsync(p,c,ct);return $"Arquivo salvo: {p}";}
 static IEnumerable<string> Find(string p,string q){IEnumerable<string> lines;try{lines=File.ReadLines(p);}catch{yield break;}var i=0;foreach(var l in lines){i++;if(l.Contains(q,StringComparison.OrdinalIgnoreCase))yield return $"{p}:{i}: {l}";}}
 static async Task<string> Cmd(string root,string command,CancellationToken ct){var psi=new ProcessStartInfo("/bin/zsh",$"-lc \"{command.Replace("\"","\\\"")}\""){WorkingDirectory=root,RedirectStandardOutput=true,RedirectStandardError=true,UseShellExecute=false};if(OperatingSystem.IsWindows()){psi.FileName="cmd.exe";psi.Arguments="/c "+command;}using var p=Process.Start(psi)!;var o=p.StandardOutput.ReadToEndAsync(ct);var e=p.StandardError.ReadToEndAsync(ct);await p.WaitForExitAsync(ct);return(await o)+"\n"+(await e);}
 static string Trim(string s)=>s.Length>30000?s[..30000]+"\n[truncado]":s;
 const string SystemPrompt="""Você é MIAU, um agente local de programação. Trabalhe somente no projeto aberto. Para usar uma ferramenta, responda APENAS JSON: {"name":"tool","arguments":{...}}. Ferramentas: list_files(path), read_file(path), write_file(path,content), search(query), run_command(command), git_status(), git_diff(). Inspecione antes de editar, faça mudanças pequenas, rode testes quando apropriado. Nunca faça commit, push, reset, clean ou delete sem pedido explícito. Quando terminar, responda em português com resumo e testes executados.""";
}