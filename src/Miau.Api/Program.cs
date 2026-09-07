var builder = WebApplication.CreateBuilder(args);
// Explicit binding keeps generic ASPNETCORE_URLS overrides from exposing the host.
builder.WebHost.ConfigureKestrel(options => options.Listen(System.Net.IPAddress.Loopback, 11435));
var app = builder.Build();
app.Run();

public partial class Program;
