using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.Media.Imaging;

namespace Miau.Desktop;

public partial class MainWindow : Window
{
    readonly AgentService agent = new();
    readonly ProjectService projects = new();
    readonly AppState state = AppState.Load();
    readonly JobRunner runner = new();
    readonly GitHubJobService jobs = new();
    readonly TaskReportService reports = new();
    CancellationTokenSource? cts;
    CancellationTokenSource? runnerCts;
    string? workspace;

    public MainWindow()
    {
        InitializeComponent();
        LoadBrand();
        AgentIdText.Text = state.AgentId;
        runner.StatusChanged += s => Dispatcher.UIThread.Post(() => { CurrentJobText.Text = s; Activity(s); });
        RestoreWorkspace();
    }

    async void RestoreWorkspace()
    {
        if (!string.IsNullOrWhiteSpace(state.LastWorkspace) && Directory.Exists(state.LastWorkspace))
            await SetWorkspace(state.LastWorkspace);
    }

    async Task SetWorkspace(string path)
    {
        workspace = path;
        state.RememberProject(path);
        var info = await projects.InspectAsync(path);
        WorkspaceText.Text = info.IsGit && !string.IsNullOrWhiteSpace(info.Branch)
            ? $"{info.Name}\n{info.Branch}"
            : info.Name;
        await RefreshChanges();
    }

    void LoadBrand()
    {
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "Assets", "miau-logo.base64");
            var bytes = Convert.FromBase64String(File.ReadAllText(path).Trim());
            using var stream = new MemoryStream(bytes);
            var bitmap = new Bitmap(stream);
            SidebarLogo.Source = bitmap;
            WelcomeLogo.Source = bitmap;
        }
        catch (Exception ex)
        {
            StatusText.Text = "Logo não carregada: " + ex.Message;
        }
    }

    async void OpenWorkspace(object? s, RoutedEventArgs e)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new() { Title = "Abrir projeto", AllowMultiple = false });
        if (folders.Count > 0)
        {
            await SetWorkspace(folders[0].Path.LocalPath);
        }
    }

    void NewTask(object? s, RoutedEventArgs e)
    {
        Thread.Children.Clear();
        PromptBox.Text = "";
        Welcome.IsVisible = true;
        Scroller.IsVisible = false;
    }

    void Stop(object? s, RoutedEventArgs e) => cts?.Cancel();

    async void AutonomousChanged(object? s, RoutedEventArgs e)
    {
        if (AutonomousToggle.IsChecked == true)
        {
            if (string.IsNullOrWhiteSpace(workspace))
            {
                AutonomousToggle.IsChecked = false;
                await Message("Abra um projeto antes de ativar o modo autônomo.");
                return;
            }
            state.AutonomousMode = true;
            state.Save();
            StopAfterTaskCheck.IsVisible = true;
            runnerCts = new();
            _ = RunAutonomousAsync(runnerCts.Token);
        }
        else
        {
            state.AutonomousMode = false;
            state.Save();
            StopAfterTaskCheck.IsVisible = false;
            runnerCts?.Cancel();
        }
    }

    async Task RunAutonomousAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(workspace)) return;
        try
        {
            await runner.RunAsync(
                token => jobs.AcquireNextAsync(workspace, state.AgentId, token),
                async (job, token) =>
                {
                    var started = DateTimeOffset.Now;
                    runner.StopAfterCurrentTask = StopAfterTaskCheck.IsChecked == true;
                    await jobs.PrepareBranchAsync(workspace, job, token);
                    string result = "";
                    try
                    {
                        result = await agent.RunAsync(workspace, job.Prompt, token,
                            ev => Dispatcher.UIThread.Post(() => Activity(ev)));
                        var report = await reports.CreateAsync(workspace, job, state.AgentId, started, "Concluído", result, token);
                        Activity($"Relatório gerado: {report}");
                        await jobs.CompleteAsync(workspace, job, state.AgentId, token);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        var report = await reports.CreateAsync(workspace, job, state.AgentId, started, "Falhou", ex.Message, token);
                        Activity($"Relatório de falha: {report}");
                        await jobs.FailAsync(workspace, job, state.AgentId, token);
                        throw;
                    }
                    await Dispatcher.UIThread.InvokeAsync(RefreshChanges);
                },
                TimeSpan.FromSeconds(Math.Max(10, state.NextTaskDelaySeconds)), ct);
        }
        catch (OperationCanceledException) { Activity("Modo autônomo interrompido."); }
        catch (Exception ex) { Activity("ERRO NO MODO AUTÔNOMO: " + ex.Message); }
        finally
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                AutonomousToggle.IsChecked = false;
                StopAfterTaskCheck.IsVisible = false;
            });
        }
    }

    async void RefreshDiff(object? s, RoutedEventArgs e) => await RefreshChanges();

    async Task RefreshChanges()
    {
        if (string.IsNullOrWhiteSpace(workspace)) return;
        try
        {
            var status = await agent.GetGitStatusAsync(workspace, CancellationToken.None);
            var diff = await agent.GetGitDiffAsync(workspace, CancellationToken.None);
            ChangeSummary.Text = string.IsNullOrWhiteSpace(status) ? "Working tree limpo" : status.Trim();
            DiffBox.Text = string.IsNullOrWhiteSpace(diff) ? "Sem alterações rastreadas para exibir." : diff;
        }
        catch (Exception ex)
        {
            ChangeSummary.Text = "Não foi possível ler o Git";
            DiffBox.Text = ex.Message;
        }
    }

    void Activity(string text)
    {
        var line = new TextBlock
        {
            Text = $"{DateTime.Now:HH:mm:ss}  {text}",
            TextWrapping = TextWrapping.Wrap,
            FontFamily = new FontFamily("Menlo,Consolas,monospace"),
            FontSize = 11,
            Opacity = .82
        };
        ActivityFeed.Children.Add(line);
        while (ActivityFeed.Children.Count > 120)
            ActivityFeed.Children.RemoveAt(0);
    }

    async void Send(object? s, RoutedEventArgs e)
    {
        var prompt = PromptBox.Text?.Trim();
        if (string.IsNullOrWhiteSpace(prompt)) return;
        if (string.IsNullOrWhiteSpace(workspace)) { await Message("Abra um projeto primeiro."); return; }

        Welcome.IsVisible = false;
        Scroller.IsVisible = true;
        Add("Você", prompt);
        PromptBox.Text = "";
        SendButton.IsEnabled = false;
        StopButton.IsEnabled = true;
        StatusText.Text = "MIAU trabalhando…";
        Activity($"Iniciando tarefa: {prompt}");
        cts = new();

        var activity = new TextBlock { Text = "● Pensando e usando ferramentas…", Opacity = .65, TextWrapping = TextWrapping.Wrap };
        Thread.Children.Add(activity);
        try
        {
            var result = await agent.RunAsync(workspace, prompt, cts.Token,
                ev => Dispatcher.UIThread.Post(() =>
                {
                    activity.Text = ev;
                    Activity(ev);
                }));
            activity.Text = "";
            Add("MIAU", result);
            Activity("Tarefa concluída.");
            await RefreshChanges();
        }
        catch (OperationCanceledException) { activity.Text = "Tarefa interrompida."; Activity("Tarefa interrompida."); }
        catch (Exception ex) { activity.Text = "Erro: " + ex.Message; Activity("ERRO: " + ex.Message); }
        finally
        {
            SendButton.IsEnabled = true;
            StopButton.IsEnabled = false;
            StatusText.Text = "Ollama local";
            cts?.Dispose();
            cts = null;
        }
    }

    void Add(string who, string text)
    {
        Thread.Children.Add(new TextBlock { Text = who, FontWeight = FontWeight.SemiBold });
        Thread.Children.Add(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, MaxWidth = 820, HorizontalAlignment = HorizontalAlignment.Left });
    }

    async Task Message(string text)
    {
        var w = new Window { Title = "MIAU", Width = 380, Height = 140, Content = new TextBlock { Text = text, Margin = new Avalonia.Thickness(20), TextWrapping = TextWrapping.Wrap } };
        await w.ShowDialog(this);
    }
}