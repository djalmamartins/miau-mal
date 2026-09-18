using System.Reflection;
using Miau.Agent;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.ConfigureKestrel(options =>
{
    options.Configure(new ConfigurationBuilder().Build());
    options.Listen(System.Net.IPAddress.Loopback, 11435);
});
builder.Services.AddHttpClient<OllamaAgent>(client =>
{
    client.BaseAddress = new Uri(builder.Configuration["Miau:OllamaUrl"] ?? "http://127.0.0.1:11434");
    client.Timeout = TimeSpan.FromMinutes(30);
});
var app = builder.Build();
var version = typeof(Program).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion.Split('+')[0];
app.MapGet("/health", () => Results.Ok(new { status = "ok", version }));
app.MapPost("/agent/run", async (AgentRequest request, OllamaAgent agent, CancellationToken ct) =>
{
    try { return Results.Ok(await agent.RunAsync(request, ct)); }
    catch (OperationCanceledException) { return Results.StatusCode(499); }
    catch (Exception ex) { return Results.Problem(ex.Message); }
});
app.Run();
public partial class Program;
