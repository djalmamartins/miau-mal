using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace Miau.Desktop;

public partial class MainWindow : Window
{
    readonly AgentService agent = new();
    CancellationTokenSource? cts;
    string? workspace;

    public MainWindow() { InitializeComponent(); }

    async void OpenWorkspace(object? s, RoutedEventArgs e)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new() { Title = "Abrir projeto", AllowMultiple = false });
        if (folders.Count > 0)
        {
            workspace = folders[0].Path.LocalPath;
            WorkspaceText.Text = workspace;
            await RefreshChanges();
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
        cts = new();

        var activity = new TextBlock { Text = "● Pensando e usando ferramentas…", Opacity = .65, TextWrapping = TextWrapping.Wrap };
        Thread.Children.Add(activity);
        try
        {
            var result = await agent.RunAsync(workspace, prompt, cts.Token,
                ev => Dispatcher.UIThread.Post(() => activity.Text = ev));
            activity.Text = "";
            Add("MIAU", result);
            await RefreshChanges();
        }
        catch (OperationCanceledException) { activity.Text = "Tarefa interrompida."; }
        catch (Exception ex) { activity.Text = "Erro: " + ex.Message; }
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