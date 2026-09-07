using System.Reflection;

var builder = WebApplication.CreateBuilder(args);
// Runtime endpoint configuration is deferred to v0.3; the foundation binds only loopback.
builder.WebHost.ConfigureKestrel(options =>
{
    options.Configure(new ConfigurationBuilder().Build());
    options.Listen(System.Net.IPAddress.Loopback, 11435);
});
var app = builder.Build();
var version = typeof(Program).Assembly
    .GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion.Split('+')[0];
app.MapGet("/health", () => Results.Ok(new { status = "ok", version }));
app.Run();

public partial class Program;
