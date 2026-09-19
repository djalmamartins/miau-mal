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
    readonly MemoryService memory = new();
    readonly ConversationService conversations = new();
    CancellationTokenSource? cts;
    CancellationTokenSource? runnerCts;
    string? workspace;
    readonly List<string> attachments = [];
    readonly DispatcherTimer executionTimer = new() { Interval = TimeSpan.FromMilliseconds(550) };
    DateTimeOffset executionStarted;
    DateTimeOffset lastExecutionPulse;
    bool executionBlink;

    public MainWindow()
    {
        InitializeComponent();
        LoadBrand();
        agent.Model = state.Model;
        StatusText.Text = $"● {state.Model} (local)";
        AgentIdText.Text = state.AgentId;
        runner.StatusChanged += s => Dispatcher.UIThread.Post(() => { CurrentJobText.Text = s; Activity(s); });
        executionTimer.Tick += (_, _) => UpdateExecutionHeartbeat();
        SetExecutionState("idle");
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
        conversations.NewSession();
        Thread.Children.Clear();
        PromptBox.Text = "";
        Welcome.IsVisible = true;
        Scroller.IsVisible = false;
    }

    void Stop(object? s, RoutedEventArgs e) => cts?.Cancel();

    void ShowConversations(object? s, RoutedEventArgs e)
    {
        Welcome.IsVisible = false; Scroller.IsVisible = true; Thread.Children.Clear();
        var recent = conversations.Recent().ToArray();
        if (recent.Length == 0) { Add("MIAU", "Nenhuma conversa salva. Até agora eu estava falando sozinho, o que explica muita coisa."); return; }
        foreach (var item in recent)
        {
            var b = new Button { Content = $"{item.At:dd/MM HH:mm}  {item.Preview}", HorizontalContentAlignment = HorizontalAlignment.Left, MaxWidth = 780 };
            var id = item.Id;
            b.Click += (_, _) => { Thread.Children.Clear(); foreach (var m in conversations.Read(id)) Add(m.Role, m.Text); };
            Thread.Children.Add(b);
        }
    }

    async void AddAttachment(object? s, RoutedEventArgs e) => await PickAttachments(false);
    async void AddLibrary(object? s, RoutedEventArgs e) => await PickAttachments(true);

    async Task PickAttachments(bool multiple)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new() { Title = multiple ? "Adicionar à conversa" : "Adicionar arquivo ou imagem", AllowMultiple = multiple });
        foreach (var file in files)
        {
            var path = file.Path.LocalPath;
            if (!attachments.Contains(path, StringComparer.OrdinalIgnoreCase)) attachments.Add(path);
        }
        AttachmentText.Text = string.Join("  •  ", attachments.Select(Path.GetFileName));
        AttachmentText.IsVisible = attachments.Count > 0;
    }

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
                        var recalled = await memory.RecallAsync(workspace, job.Prompt, token);
                        var effective = string.IsNullOrWhiteSpace(recalled) ? job.Prompt : $"{job.Prompt}\n\nMEMÓRIA RELEVANTE DESTE PROJETO:\n{recalled}";
                        result = await agent.RunAsync(workspace, effective, token,
                            ev => Dispatcher.UIThread.Post(() => Activity(ev)));
                        await memory.RememberAsync(workspace, job.Prompt, result, token);
                        var delivery = await jobs.DeliverAsync(workspace, job, result, token);
                        var report = await reports.CreateAsync(workspace, job, state.AgentId, started, "Concluído", $"{result}\n\nCommit: {delivery.Commit}\nPR: {delivery.PullRequestUrl}", token);
                        Activity($"Relatório gerado: {report}");
                        await jobs.CompleteAsync(workspace, job, state.AgentId, $"MIAU concluiu a tarefa.\n\n{result}\n\nCommit: {delivery.Commit}\nPR: {delivery.PullRequestUrl}", token);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        var report = await reports.CreateAsync(workspace, job, state.AgentId, started, "Falhou", ex.Message, token);
                        Activity($"Relatório de falha: {report}");
                        await jobs.FailAsync(workspace, job, state.AgentId, $"MIAU falhou na tarefa.\n\n{ex.Message}\n\nRelatório local: {report}", token);
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

    void SetExecutionState(string stateName)
    {
        switch (stateName)
        {
            case "running":
                executionStarted = DateTimeOffset.Now;
                lastExecutionPulse = executionStarted;
                executionBlink = true;
                ExecutionStateText.Text = "Em execução";
                ExecutionStateText.Foreground = new SolidColorBrush(Color.Parse("#55D978"));
                ExecutionDot.Fill = new SolidColorBrush(Color.Parse("#55D978"));
                executionTimer.Start();
                break;
            case "failed":
                executionTimer.Stop();
                ExecutionStateText.Text = "Travado / erro";
                ExecutionStateText.Foreground = new SolidColorBrush(Color.Parse("#FF5B57"));
                ExecutionDot.Fill = new SolidColorBrush(Color.Parse("#FF5B57"));
                ExecutionDot.Opacity = 1;
                break;
            case "done":
                executionTimer.Stop();
                ExecutionStateText.Text = "Concluído";
                ExecutionStateText.Foreground = new SolidColorBrush(Color.Parse("#55D978"));
                ExecutionDot.Fill = new SolidColorBrush(Color.Parse("#55D978"));
                ExecutionDot.Opacity = 1;
                break;
            default:
                executionTimer.Stop();
                ExecutionStateText.Text = "Aguardando";
                ExecutionStateText.Foreground = new SolidColorBrush(Color.Parse("#969DA5"));
                ExecutionDot.Fill = new SolidColorBrush(Color.Parse("#7D858D"));
                ExecutionDot.Opacity = 1;
                ExecutionElapsedText.Text = "";
                break;
        }
    }

    void PulseExecution()
    {
        lastExecutionPulse = DateTimeOffset.Now;
        if (!executionTimer.IsEnabled) SetExecutionState("running");
    }

    void UpdateExecutionHeartbeat()
    {
        var now = DateTimeOffset.Now;
        ExecutionElapsedText.Text = (now - executionStarted).ToString(@"mm\:ss");
        executionBlink = !executionBlink;
        ExecutionDot.Opacity = executionBlink ? 1 : .28;

        // A red indicator is reserved for a real lack of progress, not a cosmetic pause.
        if (now - lastExecutionPulse > TimeSpan.FromMinutes(2))
        {
            executionTimer.Stop();
            ExecutionStateText.Text = "Sem resposta";
            ExecutionStateText.Foreground = new SolidColorBrush(Color.Parse("#FF5B57"));
            ExecutionDot.Fill = new SolidColorBrush(Color.Parse("#FF5B57"));
            ExecutionDot.Opacity = 1;
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
        Dispatcher.UIThread.Post(() => ActivityScroller.ScrollToEnd(), DispatcherPriority.Background);
    }

    async void Send(object? s, RoutedEventArgs e)
    {
        var userPrompt = PromptBox.Text?.Trim();
        if (string.IsNullOrWhiteSpace(userPrompt) && attachments.Count == 0) return;
        userPrompt ??= "Analise os anexos.";
        var prompt = userPrompt;
        if (attachments.Count > 0) prompt += await BuildAttachmentContext();
        if (string.IsNullOrWhiteSpace(workspace)) { await Message("Abra um projeto primeiro."); return; }

        Welcome.IsVisible = false;
        Scroller.IsVisible = true;
        Add("Você", userPrompt + (attachments.Count > 0 ? $"\n📎 {string.Join(", ", attachments.Select(Path.GetFileName))}" : ""));
        await conversations.AppendAsync("Você", userPrompt, workspace);
        PromptBox.Text = "";
        attachments.Clear();
        AttachmentText.Text = "";
        AttachmentText.IsVisible = false;
        SendButton.IsVisible = false;
        StopButton.IsVisible = true;
        StatusText.Text = "MIAU trabalhando…";
        SetExecutionState("running");
        Activity($"Iniciando tarefa: {prompt}");
        cts = new();

        var activity = new TextBlock { Text = "● Pensando e usando ferramentas…", Opacity = .65, TextWrapping = TextWrapping.Wrap };
        Thread.Children.Add(activity);
        try
        {
            var recalled = await memory.RecallAsync(workspace, prompt, cts.Token);
            var effectivePrompt = string.IsNullOrWhiteSpace(recalled) ? prompt : $"{prompt}\n\nMEMÓRIA RELEVANTE DESTE PROJETO:\n{recalled}";
            var result = await agent.RunAsync(workspace, effectivePrompt, cts.Token,
                ev => Dispatcher.UIThread.Post(() =>
                {
                    PulseExecution();
                    activity.Text = ev;
                    Activity(ev);
                }));
            activity.Text = "";
            Add("MIAU", result);
            // The model/tool execution is finished at this point. Mark it complete before
            // bookkeeping (history, memory and diff refresh) so the UI never looks stuck.
            SetExecutionState("done");
            Activity("Execução concluída.");
            await conversations.AppendAsync("MIAU", result, workspace);
            await memory.RememberAsync(workspace, prompt, result, cts.Token);
            Activity("Resultado registrado na memória local.");
            await RefreshChanges();
        }
        catch (OperationCanceledException) { activity.Text = "Tarefa interrompida."; Activity("Tarefa interrompida."); SetExecutionState("idle"); }
        catch (Exception ex) { activity.Text = "Erro: " + ex.Message; Activity("ERRO: " + ex.Message); SetExecutionState("failed"); }
        finally
        {
            SendButton.IsVisible = true;
            StopButton.IsVisible = false;
            StatusText.Text = "Ollama local";
            cts?.Dispose();
            cts = null;
        }
    }

    async Task<string> BuildAttachmentContext()
    {
        var parts = new List<string> { "\n\nANEXOS FORNECIDOS PELO USUÁRIO:" };
        foreach (var path in attachments)
        {
            var ext = Path.GetExtension(path).ToLowerInvariant();
            if (new[] { ".txt", ".md", ".json", ".cs", ".js", ".ts", ".tsx", ".jsx", ".css", ".html", ".xml", ".yml", ".yaml", ".sql", ".py" }.Contains(ext))
            {
                try
                {
                    var text = await File.ReadAllTextAsync(path);
                    if (text.Length > 12000) text = text[..12000] + "\n[anexo truncado]";
                    parts.Add($"\n--- {Path.GetFileName(path)} ---\n{text}");
                }
                catch (Exception ex) { parts.Add($"\n- {Path.GetFileName(path)}: não foi possível ler ({ex.Message})"); }
            }
            else parts.Add($"\n- {Path.GetFileName(path)} ({ext}): arquivo anexado; conteúdo binário/visual não convertido para texto nesta versão.");
        }
        return string.Join("", parts);
    }

    void Add(string who, string text)
    {
        var mine = who == "Você";
        var body = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, MaxWidth = 720 };
        var copy = new Button
        {
            Content = "⧉",
            Padding = new Avalonia.Thickness(6, 2),
            Background = Brushes.Transparent
        };
        ToolTip.SetTip(copy, "Copiar");
        copy.Click += async (_, _) =>
        {
            var top = GetTopLevel(this);
            if (top?.Clipboard is not null) await top.Clipboard.SetTextAsync(text);
        };

        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        header.Children.Add(new TextBlock { Text = who, FontWeight = FontWeight.SemiBold, Opacity = .82 });
        Grid.SetColumn(copy, 1);
        header.Children.Add(copy);

        var stack = new StackPanel { Spacing = 7 };
        stack.Children.Add(header);
        stack.Children.Add(body);

        var bubble = new Border
        {
            Child = stack,
            Background = new SolidColorBrush(Color.Parse(mine ? "#1A2026" : "#101418")),
            BorderBrush = new SolidColorBrush(Color.Parse(mine ? "#303841" : "#242B31")),
            BorderThickness = new Avalonia.Thickness(1),
            CornerRadius = new Avalonia.CornerRadius(12),
            Padding = new Avalonia.Thickness(14, 10),
            MaxWidth = 780,
            HorizontalAlignment = mine ? HorizontalAlignment.Right : HorizontalAlignment.Left
        };
        Thread.Children.Add(bubble);
        Dispatcher.UIThread.Post(() => Scroller.ScrollToEnd(), DispatcherPriority.Background);
    }

    async Task Message(string text)
    {
        var w = new Window { Title = "MIAU", Width = 380, Height = 140, Content = new TextBlock { Text = text, Margin = new Avalonia.Thickness(20), TextWrapping = TextWrapping.Wrap } };
        await w.ShowDialog(this);
    }
}