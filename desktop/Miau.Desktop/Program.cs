using Avalonia;
namespace Miau.Desktop;
internal static class Program {
 [STAThread] public static void Main(string[] args) {
 if (args.Length > 0 && args[0].Equals("benchmark", StringComparison.OrdinalIgnoreCase)) { RunBenchmark(args).GetAwaiter().GetResult(); return; }
  if (args.Length > 0 && args[0].Equals("vision-e2e", StringComparison.OrdinalIgnoreCase)) { RunVisionE2E(args).GetAwaiter().GetResult(); return; }
  BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
 }
 static async Task RunBenchmark(string[] args) {
  var manifest = args.Length > 1 ? args[1] : Path.Combine(Directory.GetCurrentDirectory(), "benchmarks", "miau1-v0", "cases.json");
  var model = args.Length > 2 ? args[2] : "qwen2.5-coder:7b";
  var results = await new BenchmarkService().RunAsync(manifest, new OllamaModelAdapter(model, requestTimeout: TimeSpan.FromMinutes(5)), CancellationToken.None);
  var benchmarkName = new DirectoryInfo(Path.GetDirectoryName(manifest) ?? "miau1-v1").Name;
  var recoveries = results.Sum(x => x.RecoveryCount); var recovered = results.Count(x => x.Passed && x.RecoveryCount > 0);
  var report = System.Text.Json.JsonSerializer.Serialize(new { benchmark = benchmarkName, model, timestamp = DateTimeOffset.Now, passed = results.Count(x => x.Passed), failed = results.Count(x => !x.Passed), autonomyRate = results.Count == 0 ? 0 : Math.Round(results.Count(x => x.Passed && !x.HumanIntervention) * 100d / results.Count, 1), recoverySuccessRate = recoveries == 0 ? 0 : Math.Round(recovered * 100d / recoveries, 1), finalBrokenBuilds = results.Count(x => x.Passed && !x.ValidationPassed), results }, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
  var output = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MIAU", "benchmarks"); Directory.CreateDirectory(output);
 var file = Path.Combine(output, $"{benchmarkName}-{DateTimeOffset.Now:yyyyMMdd-HHmmss}.json"); await File.WriteAllTextAsync(file, report); Console.WriteLine(report); Console.WriteLine(file);
 }
 static async Task RunVisionE2E(string[] args) {
  var root = args.Length > 1 ? Path.GetFullPath(args[1]) : Path.Combine(Directory.GetCurrentDirectory(), "desktop", "Miau.Desktop.Tests", "Fixtures", "Vision");
  var model = args.Length > 2 ? args[2] : null; var tools = new ToolExecutor(visualInspector: new OllamaVisualInspector(model));
  foreach (var item in new[] { ("footer-broken.html",1440,1200,"Detectar sobreposição ou obstrução do footer"), ("mobile-overflow.html",390,844,"Detectar overflow ou corte horizontal"), ("missing-asset.html",1440,1200,"Detectar asset visualmente ausente ou quebrado"), ("valid-page.html",1440,1200,"Validar layout simples legível") }) {
   var render = await tools.ExecuteAsync(root, new(ToolNames.RenderPage, new() { ["path"]=item.Item1, ["width"]=item.Item2.ToString(), ["height"]=item.Item3.ToString() }, null), false, CancellationToken.None);
   if (!render.Success) { Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new { fixture=item.Item1, error=render.Error })); continue; }
   var inspect = await tools.ExecuteAsync(root, new(ToolNames.InspectVisual, new() { ["screenshot_path"]=render.Metadata["screenshot_path"], ["viewport"]=$"{item.Item2}x{item.Item3}", ["objective"]=item.Item4, ["criteria"]=item.Item4 }, null), false, CancellationToken.None);
   Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new { fixture=item.Item1, screenshotHash=render.Metadata["screenshot_hash"], success=inspect.Success, output=inspect.Output, error=inspect.Error, metadata=inspect.Metadata }));
  }
 }
 public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>().UsePlatformDetect().WithInterFont().LogToTrace();
}
