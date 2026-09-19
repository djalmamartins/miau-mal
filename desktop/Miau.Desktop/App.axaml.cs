using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Media.Imaging;

namespace Miau.Desktop;

public partial class App : Application
{
    TrayIcon? tray;
    MainWindow? main;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            main = new MainWindow();
            desktop.MainWindow = main;
            SetupTray(desktop);
        }
        base.OnFrameworkInitializationCompleted();
    }

    void SetupTray(IClassicDesktopStyleApplicationLifetime desktop)
    {
        try
        {
            var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "miau-tray.png");
            if (!File.Exists(iconPath)) return;

            var menu = new NativeMenu();
            var open = new NativeMenuItem("Abrir MIAU");
            open.Click += (_, _) => ShowMain();
            menu.Add(open);
            menu.Add(new NativeMenuItemSeparator());
            var quit = new NativeMenuItem("Sair");
            quit.Click += (_, _) => desktop.Shutdown();
            menu.Add(quit);

            tray = new TrayIcon
            {
                Icon = new WindowIcon(new Bitmap(iconPath)),
                ToolTipText = "MIAU • agente local",
                Menu = menu,
                IsVisible = true
            };
            tray.Clicked += (_, _) => ShowMain();
        }
        catch { /* tray must never prevent MIAU from starting */ }
    }

    void ShowMain()
    {
        if (main is null) return;
        main.Show();
        main.Activate();
    }
}
