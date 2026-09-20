using Avalonia;
namespace Miau.Desktop;
internal static class Program {
 [STAThread] public static void Main(string[] args) {
  if (args.Length > 0 && args[0].Equals("benchmark", StringComparison.OrdinalIgnoreCase)) { RunBenchmark(args).GetAwaiter().GetResult(); return; }
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
 public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>().UsePlatformDetect().WithInterFont().LogToTrace();
}
