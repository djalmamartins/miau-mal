using System.Reflection;

if (args.Length == 0 || (args.Length == 1 && args[0] is "--help" or "-h" or "help"))
{
    Console.WriteLine("""
        miau-mal — Inteligência Local.
        Usage: miau [--help | --version]

        Foundation only. No inference is available yet.
        Planned: status, models, run, stop, serve, chat.
        Start the development API with: dotnet run --project src/Miau.Api
        """);
    return 0;
}

if (args.Length == 1 && args[0] == "--version")
{
    Console.WriteLine(Assembly.GetExecutingAssembly()
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion.Split('+')[0]);
    return 0;
}

Console.Error.WriteLine("Unknown or unavailable command. Use --help.");
return 2;
