using Avalonia; using Avalonia.Controls.ApplicationLifetimes; using Avalonia.Markup.Xaml;
namespace Miau.Desktop;
public partial class App:Application {
 public override void Initialize()=>AvaloniaXamlLoader.Load(this);
 public override void OnFrameworkInitializationCompleted(){if(ApplicationLifetime is IClassicDesktopStyleApplicationLifetime d)d.MainWindow=new MainWindow();base.OnFrameworkInitializationCompleted();}
}