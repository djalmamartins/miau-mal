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
  var report = System.Text.Json.JsonSerializer.Serialize(new { benchmark = "miau1-v0", model, timestamp = DateTimeOffset.Now, passed = results.Count(x => x.Passed), failed = results.Count(x => !x.Passed), results }, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
  var output = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MIAU", "benchmarks"); Directory.CreateDirectory(output);
  var file = Path.Combine(output, $"miau1-v0-{DateTimeOffset.Now:yyyyMMdd-HHmmss}.json"); await File.WriteAllTextAsync(file, report); Console.WriteLine(report); Console.WriteLine(file);
 }
 public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>().UsePlatformDetect().WithInterFont().LogToTrace();
}
