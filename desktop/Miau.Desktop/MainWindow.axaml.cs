using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.Media.Imaging;
using System.Diagnostics;

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
    readonly DiagnosticsService diagnostics = new();
    readonly EvolutionService evolution = new();
    readonly LearningPipelineService learning = new();
    readonly SelfRepairService selfRepair = new();
    TrainingScheduler? training;
    CancellationTokenSource? trainingCts;
    readonly DispatcherTimer trainingTimer = new();
    EvolutionDashboardService? evolutionDashboard;
    CancellationTokenSource? cts;
    CancellationTokenSource? runnerCts;
    string? workspace;
    readonly List<string> attachments = [];
    readonly List<string> executionLog = [];
    readonly DispatcherTimer executionTimer = new() { Interval = TimeSpan.FromMilliseconds(550) };
    DateTimeOffset executionStarted;
    DateTimeOffset lastExecutionPulse;
    bool executionBlink;
    bool executionFinished;
    ExecutionEventType? activeExecution;

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
        training = new TrainingScheduler(agent);
        TrainingToggle.IsChecked = state.TrainingEnabled;
        SelfRepairToggle.IsChecked = state.SelfRepairEnabled;
        ConfigureTrainingTimer();
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
            var imagePath = Path.Combine(AppContext.BaseDirectory, "Assets", "miau-eyes.png");
            Bitmap bitmap;
            if (File.Exists(imagePath)) bitmap = new Bitmap(imagePath);
            else
            {
                var legacyPath = Path.Combine(AppContext.BaseDirectory, "Assets", "miau-logo.base64");
                var bytes = Convert.FromBase64String(File.ReadAllText(legacyPath).Trim());
                using var stream = new MemoryStream(bytes);
                bitmap = new Bitmap(stream);
            }
            SidebarLogo.Source = bitmap;
            WelcomeLogo.Source = bitmap;
        }
        catch (Exception ex)
        {
            StatusText.Text = "Logo não carregada: " + ex.Message;
        }
    }

    void ConfigureTrainingTimer()
    {
        trainingTimer.Stop();
        trainingTimer.Interval = TimeSpan.FromHours(Math.Clamp(state.TrainingIntervalHours, 1, 168));
        trainingTimer.Tick -= TrainingTimerTick;
        trainingTimer.Tick += TrainingTimerTick;
        TrainingStatusText.Text = state.TrainingEnabled ? $"A cada {state.TrainingIntervalHours}h" : "Desativado";
        if (state.TrainingEnabled) trainingTimer.Start();
    }

    async void TrainingTimerTick(object? sender, EventArgs e)
    {
        if (trainingCts is not null || string.IsNullOrWhiteSpace(workspace)) return;
        await RunTrainingCycleCore();
    }

    void TrainingChanged(object? sender, RoutedEventArgs e)
    {
        state.TrainingEnabled = TrainingToggle.IsChecked == true;
        state.Save();
        ConfigureTrainingTimer();
        Activity(state.TrainingEnabled ? "Treino controlado agendado." : "Treino controlado desativado.");
    }

    void SelfRepairChanged(object? sender, RoutedEventArgs e)
    {
        state.SelfRepairEnabled = SelfRepairToggle.IsChecked == true;
        state.Save();
        Activity(state.SelfRepairEnabled ? "Detecção de auto-reparo ativada." : "Auto-reparo desativado.");
    }

    async void RunTrainingCycle(object? sender, RoutedEventArgs e) => await RunTrainingCycleCore(force: true);

    async Task RunTrainingCycleCore(bool force = false)
    {
        if (training is null || trainingCts is not null) return;
        if (!force && !state.TrainingEnabled) return;
        trainingCts = new();
        TrainingStatusText.Text = "Executando…";
        try
        {
            var schedule = new TrainingSchedule(true, state.TrainingIntervalHours, state.TrainingMaxTasksPerCycle);
            var result = await training.RunCycleAsync(workspace ?? Environment.CurrentDirectory, schedule, trainingCts.Token,
                x => Dispatcher.UIThread.Post(() => Activity(x)));
            Activity($"Ciclo de treino: {result.Completed}/{result.Attempted} aprovados; {result.Failed} falhas; {result.Rejected} rejeitados pelo verificador.");
            if (state.SelfRepairEnabled)
            {
                var candidates = await selfRepair.DetectAsync(trainingCts.Token, state.SelfRepairEvidenceThreshold);
                Activity(candidates.Count == 0 ? "Auto-reparo: nenhuma falha recorrente elegível." : $"Auto-reparo: {candidates.Count} candidato(s) aguardando execução segura.");
                foreach (var item in candidates.Take(5))
                {
                    Activity($"RepairJob {item.Id}: {item.Reason} · evidências {item.EvidenceCount}");
                    if (string.IsNullOrWhiteSpace(workspace)) continue;
                    var manifest = Path.Combine(workspace, "benchmarks", "miau1-v0", "cases.json");
                    if (!File.Exists(manifest)) { Activity("Auto-reparo aguardando: benchmark do MIAU não está disponível neste projeto."); continue; }
                    var repair = await selfRepair.RunIsolatedAsync(workspace, item, agent, manifest, trainingCts.Token,
                        x => Dispatcher.UIThread.Post(() => Activity(x)));
                    Activity($"RepairJob {repair.Id}: {(repair.Accepted ? "APROVADO" : "REJEITADO")} · benchmark {repair.BenchmarkBefore:0.0}% → {repair.BenchmarkAfter:0.0}% · build {(repair.BuildPassed ? "OK" : "FALHOU")} · testes {(repair.TestsPassed ? "OK" : "FALHOU")}");
                    if (repair.Accepted) Activity("Correção aprovada ficou isolada; promoção ao código principal continua exigindo revisão explícita.");
                }
            }
        }
        catch (OperationCanceledException) { Activity("Ciclo de treino cancelado."); }
        catch (Exception ex) { Activity("Falha no ciclo de treino: " + DatasetService.Redact(ex.Message)); }
        finally { trainingCts?.Dispose(); trainingCts = null; ConfigureTrainingTimer(); }
    }

    async void ReviewRepairs(object? sender, RoutedEventArgs e)
    {
        var items = await selfRepair.RecentAsync(CancellationToken.None);
        var approved = items.Where(x => x.Accepted && !string.IsNullOrWhiteSpace(x.PatchPath) && File.Exists(x.PatchPath)).ToArray();
        if (approved.Length == 0) { await Message("Nenhum reparo aprovado aguardando revisão."); return; }
        var latest = approved[0];
        var patch = await File.ReadAllTextAsync(latest.PatchPath!);
        var preview = patch.Length > 12000 ? patch[..12000] + "\n[diff truncado]" : patch;
        var window = new Window { Title = $"Revisar {latest.Id}", Width = 900, Height = 700 };
        var panel = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto"), Margin = new Avalonia.Thickness(16) };
        panel.Children.Add(new TextBlock { Text = $"{latest.Id} · benchmark {latest.BenchmarkBefore:0.0}% → {latest.BenchmarkAfter:0.0}% · build {(latest.BuildPassed ? "OK" : "falhou")} · testes {(latest.TestsPassed ? "OK" : "falhou")}", TextWrapping = TextWrapping.Wrap });
        var diff = new TextBox { Text = preview, IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.NoWrap, FontFamily = new FontFamily("Menlo,Consolas,monospace"), FontSize = 11, Margin = new Avalonia.Thickness(0,12) };
        Grid.SetRow(diff,1); panel.Children.Add(diff);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        var close = new Button { Content = "Fechar" }; close.Click += (_,_) => window.Close();
        var promote = new Button { Content = "Promover patch" };
        promote.Click += async (_,_) =>
        {
            if (string.IsNullOrWhiteSpace(workspace)) { await Message("Abra o projeto do MIAU antes de promover."); return; }
            try { var stat = await selfRepair.PromoteAsync(workspace, new RepairRunResult(latest.Id, latest.Accepted, latest.BenchmarkBefore, latest.BenchmarkAfter, latest.BuildPassed, latest.TestsPassed, latest.Reason, latest.PatchPath, latest.BaseCommit), CancellationToken.None); Activity($"RepairJob {latest.Id} promovido para revisão local. {stat}"); await RefreshChanges(); window.Close(); }
            catch (Exception ex) { await Message("Promoção bloqueada: " + DatasetService.Redact(ex.Message)); }
        };
        actions.Children.Add(close); actions.Children.Add(promote); Grid.SetRow(actions,2); panel.Children.Add(actions);
        window.Content = panel; await window.ShowDialog(this);
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
        if (!ExecutionCoordination.Shared.TryAcquire(workspace, ExecutionKind.AutonomousJob, out var autonomousLease)) { Activity("Modo autônomo aguardando: workspace ocupado."); return; }
        using var executionLease = autonomousLease;
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
                            ev => Dispatcher.UIThread.Post(() => Activity(ev)), "autonomous");
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
                executionFinished = false;
                executionStarted = DateTimeOffset.Now;
                lastExecutionPulse = executionStarted;
                executionBlink = true;
                ExecutionStateText.Text = "Em execução";
                ExecutionStateText.Foreground = new SolidColorBrush(Color.Parse("#55D978"));
                ExecutionDot.Fill = new SolidColorBrush(Color.Parse("#55D978"));
                executionTimer.Start();
                break;
            case "failed":
                executionFinished = true;
                executionTimer.Stop();
                ExecutionStateText.Text = "Travado / erro";
                ExecutionStateText.Foreground = new SolidColorBrush(Color.Parse("#FF5B57"));
                ExecutionDot.Fill = new SolidColorBrush(Color.Parse("#FF5B57"));
                ExecutionDot.Opacity = 1;
                break;
            case "done":
                executionFinished = true;
                executionTimer.Stop();
                ExecutionStateText.Text = "Concluído";
                ExecutionStateText.Foreground = new SolidColorBrush(Color.Parse("#55D978"));
                ExecutionDot.Fill = new SolidColorBrush(Color.Parse("#55D978"));
                ExecutionDot.Opacity = 1;
                break;
            case "cancelled":
                executionFinished = true;
                executionTimer.Stop();
                ExecutionStateText.Text = "Cancelado";
                ExecutionStateText.Foreground = new SolidColorBrush(Color.Parse("#E8B84A"));
                ExecutionDot.Fill = new SolidColorBrush(Color.Parse("#E8B84A"));
                ExecutionDot.Opacity = 1;
                break;
            default:
                executionFinished = true;
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
        if (executionFinished) return;
        lastExecutionPulse = DateTimeOffset.Now;
        if (!executionTimer.IsEnabled) SetExecutionState("running");
    }

    void ShowEnginePhase(string message)
    {
        var separator = message.IndexOf(':');
        if (!message.StartsWith("◆ ", StringComparison.Ordinal) || separator < 0) return;
        ExecutionStateText.Text = message[2..separator];
    }

    void UpdateExecutionHeartbeat()
    {
        if (executionFinished) { executionTimer.Stop(); return; }
        var now = DateTimeOffset.Now;
        ExecutionElapsedText.Text = "Processando há " + (now - executionStarted).ToString(@"mm\:ss");
        executionBlink = !executionBlink;
        ExecutionDot.Opacity = executionBlink ? 1 : .28;

        // A red indicator is reserved for a real lack of progress, not a cosmetic pause.
        if (now - lastExecutionPulse > TimeSpan.FromMinutes(2) && activeExecution is null)
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
        var stamped = $"{DateTime.Now:HH:mm:ss}  {text}";
        executionLog.Add(stamped);
        while (executionLog.Count > 500) executionLog.RemoveAt(0);
        var line = new TextBlock
        {
            Text = stamped,
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

    void Timeline(ExecutionEvent ev)
    {
        PulseExecution();
        activeExecution = ev.Type switch
        {
            ExecutionEventType.ModelRequestStarted or ExecutionEventType.ToolStarted or ExecutionEventType.CommandStarted or ExecutionEventType.DiffStarted or ExecutionEventType.BuildStarted or ExecutionEventType.TestsStarted => ev.Type,
            ExecutionEventType.ModelRequestCompleted or ExecutionEventType.ToolCompleted or ExecutionEventType.ToolFailed or ExecutionEventType.CommandCompleted or ExecutionEventType.CommandFailed or ExecutionEventType.DiffCompleted or ExecutionEventType.BuildCompleted or ExecutionEventType.BuildFailed or ExecutionEventType.TestsCompleted or ExecutionEventType.TestsFailed => null,
            _ => activeExecution
        };
        ExecutionStateText.Text = EventStateLabel(ev.Type);
        UpdatePhaseChecklist(ev.Phase);
        var symbol = ev.Type is ExecutionEventType.RecoveryStarted or ExecutionEventType.PolicyRecovery ? "↻" : ev.Success switch { true => "✓", false => "✕", _ => "›" };
        var elapsed = ev.Duration is { } d ? $" · {d.TotalSeconds:0.0}s" : "";
        var narrative = ExecutionNarrative(ev);
        var title = $"{symbol} {narrative}{elapsed}" + (string.IsNullOrWhiteSpace(ev.Target) || ev.Type is ExecutionEventType.ModelRequestStarted or ExecutionEventType.ModelRequestCompleted ? "" : $"\n  {ev.Target}");
        executionLog.Add($"{DateTime.Now:HH:mm:ss}  {title}" + (string.IsNullOrWhiteSpace(ev.Details) ? "" : $"\n{ev.Details}"));
        while (executionLog.Count > 500) executionLog.RemoveAt(0);
        Control item = string.IsNullOrWhiteSpace(ev.Details)
            ? new TextBlock { Text = title, TextWrapping = TextWrapping.Wrap, FontSize = 11 }
            : new Expander
            {
                Header = new TextBlock { Text = title, TextWrapping = TextWrapping.Wrap, FontSize = 11 },
                Content = new TextBox { Text = ev.Details.Length > 8000 ? ev.Details[..8000] + "\n[preview truncado]" : ev.Details, IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.NoWrap, MaxHeight = 180, FontFamily = new FontFamily("Menlo,Consolas,monospace"), FontSize = 10 }
            };
        ActivityFeed.Children.Add(item);
        while (ActivityFeed.Children.Count > 120) ActivityFeed.Children.RemoveAt(0);
        Dispatcher.UIThread.Post(() => ActivityScroller.ScrollToEnd(), DispatcherPriority.Background);
    }

    static string ExecutionNarrative(ExecutionEvent ev) => ev.Type switch
    {
        ExecutionEventType.JobStarted => "Recebi a tarefa e iniciei a análise",
        ExecutionEventType.ModelRequestStarted => "Analisando o próximo passo",
        ExecutionEventType.ModelRequestCompleted => "Defini o próximo passo",
        ExecutionEventType.FileRead => "Li o arquivo necessário",
        ExecutionEventType.FileCreated => "Criei um arquivo",
        ExecutionEventType.FileChanged => "Editei um arquivo",
        ExecutionEventType.DiffStarted => "Conferindo as alterações",
        ExecutionEventType.DiffCompleted => "Alterações conferidas no Git",
        ExecutionEventType.BuildStarted => "Validando o projeto",
        ExecutionEventType.BuildCompleted => "Validação concluída",
        ExecutionEventType.TestsStarted => "Executando os testes",
        ExecutionEventType.TestsCompleted => "Testes concluídos",
        ExecutionEventType.RetryStarted => "Encontrei um problema e vou tentar outra estratégia",
        ExecutionEventType.PolicyRecovery => "Ajustando a estratégia para cumprir os critérios",
        ExecutionEventType.JobCompleted => "Tarefa concluída",
        ExecutionEventType.JobFailed => "A execução foi interrompida por uma falha",
        ExecutionEventType.JobCancelled => "Execução cancelada",
        _ => ev.Description
    };

    static string EventStateLabel(ExecutionEventType type) => type switch
    {
        ExecutionEventType.JobStarted => "Iniciando",
        ExecutionEventType.PhaseChanged => "Em execução",
        ExecutionEventType.ModelRequestStarted => "Modelo processando",
        ExecutionEventType.ModelRequestCompleted => "Modelo respondeu",
        ExecutionEventType.ToolStarted => "Executando ferramenta",
        ExecutionEventType.ToolCompleted => "Ferramenta concluída",
        ExecutionEventType.ToolFailed => "Falha na ferramenta",
        ExecutionEventType.FileRead => "Arquivo lido",
        ExecutionEventType.FileCreated => "Arquivo criado",
        ExecutionEventType.FileChanged => "Arquivo alterado",
        ExecutionEventType.CommandStarted => "Executando comando",
        ExecutionEventType.CommandCompleted => "Comando concluído",
        ExecutionEventType.CommandFailed => "Falha no comando",
        ExecutionEventType.DiffStarted => "Verificando alterações",
        ExecutionEventType.DiffCompleted => "Alterações verificadas",
        ExecutionEventType.BuildStarted => "Validando projeto",
        ExecutionEventType.BuildCompleted => "Validação concluída",
        ExecutionEventType.BuildFailed => "Falha na validação",
        ExecutionEventType.TestsStarted => "Executando testes",
        ExecutionEventType.TestsCompleted => "Testes concluídos",
        ExecutionEventType.TestsFailed => "Falha nos testes",
        ExecutionEventType.RetryStarted => "Tentando novamente",
        ExecutionEventType.CriteriaUpdated => "Critérios atualizados",
        ExecutionEventType.ProgressRecorded => "Progresso comprovado",
        ExecutionEventType.StagnationDetected => "Estagnação detectada",
        ExecutionEventType.RecoveryStarted => "Recuperando execução",
        ExecutionEventType.PolicyRecovery => "Ajustando estratégia",
        ExecutionEventType.ModelTimeout => "Modelo excedeu o tempo",
        ExecutionEventType.JobCompleted => "Concluído",
        ExecutionEventType.JobFailed => "Falhou",
        ExecutionEventType.JobCancelled => "Cancelado",
        _ => "Em execução"
    };

    void UpdatePhaseChecklist(JobPhase phase)
    {
        PhaseUnderstand.Text = Mark(phase, JobPhase.Understanding, "Entender");
        PhaseInspect.Text = Mark(phase, JobPhase.Inspecting, "Inspecionar");
        PhasePlan.Text = Mark(phase, JobPhase.Planning, "Planejar");
        PhaseEdit.Text = Mark(phase, JobPhase.Executing, "Editar");
        PhaseVerify.Text = Mark(phase, JobPhase.Verifying, "Verificar");
        PhaseTest.Text = Mark(phase, JobPhase.Testing, "Testar");
    }

    static string Mark(JobPhase current, JobPhase target, string label) => current > target ? $"✓ {label}" : current == target ? $"● {label}" : $"○ {label}";

    async void CopyExecution(object? sender, RoutedEventArgs e)
    {
        var top = GetTopLevel(this);
        if (top?.Clipboard is null) return;
        var header = $"MIAU execução\nProjeto: {workspace ?? "(nenhum)"}\nModelo: {agent.Model}\nEstado: {ExecutionStateText.Text}\nTempo: {ExecutionElapsedText.Text}\n\n";
        await top.Clipboard.SetTextAsync(header + string.Join("\n", executionLog));
        CopyExecutionButton.Content = "✓";
        await Task.Delay(900);
        CopyExecutionButton.Content = "⧉";
    }

    async void RunDiagnostics(object? sender, RoutedEventArgs e)
    {
        Activity("MIAU Diagnostics");
        foreach (var item in await diagnostics.RunAsync(workspace, agent.Model, CancellationToken.None))
            Activity($"{(item.Success ? "✓" : "✕")} {item.Name}: {item.Detail}");
    }

    async void RunBenchmark(object? sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(workspace)) { await Message("Abra o repositório do MIAU para executar o benchmark."); return; }
        var manifest = Path.Combine(workspace, "benchmarks", "miau1-v0", "cases.json");
        if (!File.Exists(manifest)) { await Message("Manifesto benchmarks/miau1-v0/cases.json não encontrado neste workspace."); return; }
        Activity("Benchmark MIAU1-Coder iniciado em workspaces temporários isolados.");
        try
        {
            var results = await agent.RunBenchmarkAsync(manifest, CancellationToken.None);
            foreach (var item in results)
                Activity($"{(item.Passed ? "✓" : "✕")} {item.Id}: {item.DurationSeconds:0.0}s · tools {item.ToolCalls} · retries {item.Retries} · score {item.QualityScore:0}");
            var passed = results.Count(x => x.Passed);
            Activity($"Benchmark concluído: {passed}/{results.Count} aprovados. Os casos de benchmark não entram no dataset de treinamento.");
        }
        catch (Exception ex) { Activity("Benchmark falhou: " + DatasetService.Redact(ex.Message)); }
    }

    async void OpenEvolutionDashboard(object? sender, RoutedEventArgs e)
    {
        try
        {
            evolutionDashboard ??= new EvolutionDashboardService(evolution, agent.Model);
            evolutionDashboard.Start();
            Process.Start(new ProcessStartInfo(EvolutionDashboardService.Url) { UseShellExecute = true });
            Activity($"Evolution Dashboard aberto em {EvolutionDashboardService.Url}");
        }
        catch (Exception ex) { await Message("Não foi possível abrir o Evolution Dashboard: " + ex.Message); }
    }

    protected override void OnClosed(EventArgs e)
    {
        runnerCts?.Cancel();
        cts?.Cancel();
        trainingCts?.Cancel();
        trainingTimer.Stop();
        if (evolutionDashboard is not null) _ = evolutionDashboard.DisposeAsync();
        base.OnClosed(e);
    }

    async void Send(object? s, RoutedEventArgs e)
    {
        var userPrompt = PromptBox.Text?.Trim();
        if (string.IsNullOrWhiteSpace(userPrompt) && attachments.Count == 0) return;
        userPrompt ??= "Analise os anexos.";
        var prompt = userPrompt;
        if (attachments.Count > 0) prompt += await BuildAttachmentContext();
        if (string.IsNullOrWhiteSpace(workspace)) { await Message("Abra um projeto primeiro."); return; }

        if (!ExecutionCoordination.Shared.TryAcquire(workspace, ExecutionKind.Interactive, out var interactiveLease)) { await Message("Este workspace está ocupado por outra execução."); return; }
        using var executionLease = interactiveLease;
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
        executionLog.Clear();
        SetExecutionState("running");
        Activity($"Iniciando tarefa: {prompt}");
        cts = new();

        var activity = new TextBlock { Text = "● Pensando e usando ferramentas…", Opacity = .65, TextWrapping = TextWrapping.Wrap };
        Thread.Children.Add(activity);
        try
        {
            var recalled = await memory.RecallAsync(workspace, prompt, cts.Token);
            var effectivePrompt = string.IsNullOrWhiteSpace(recalled) ? prompt : $"{prompt}\n\nMEMÓRIA RELEVANTE DESTE PROJETO:\n{recalled}";
            var result = await agent.RunWithEventsAsync(workspace, effectivePrompt, cts.Token,
                ev => Dispatcher.UIThread.Post(() => { activity.Text = ExecutionProgressNarrative.For(ev); Timeline(ev); }));
            activity.Text = "";
            Add("MIAU", result);
            if (!string.IsNullOrWhiteSpace(agent.LastRunResult?.TrainingRecordId)) AddTrainingReview(agent.LastRunResult.TrainingRecordId);
            // The model/tool execution is finished at this point. Mark it complete before
            // bookkeeping (history, memory and diff refresh) so the UI never looks stuck.
            SetExecutionState("done");
            Activity("Execução concluída.");
            await conversations.AppendAsync("MIAU", result, workspace);
            await memory.RememberAsync(workspace, prompt, result, cts.Token);
            Activity("Resultado registrado na memória local.");
            await RefreshChanges();
        }
        catch (OperationCanceledException) { activity.Text = "Tarefa cancelada."; Activity("Tarefa cancelada pelo usuário."); SetExecutionState("cancelled"); }
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

    async void ExportLora(object? sender, RoutedEventArgs e)
    {
        try
        {
            var readiness = await learning.GetReadinessAsync(CancellationToken.None);
            var output = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "MIAU-lora-export-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"));
            var file = await learning.ExportApprovedForLoraAsync(output, CancellationToken.None);
            Activity($"Exportação LoRA criada: {file} · {readiness.Approved} exemplo(s) aprovados.");
            await Message($"Dataset exportado em:\n{file}\n\nExemplos aprovados: {readiness.Approved}. Recomendação para LoRA: {readiness.MinimumRecommended}+ exemplos e ao menos 3 projetos distintos.");
        }
        catch (Exception ex) { await Message("Não foi possível exportar exemplos: " + DatasetService.Redact(ex.Message)); }
    }

    void AddTrainingReview(string taskId)
    {
        var label = new TextBlock { Text = "Revisar exemplo para aprendizado", Classes = { "muted" }, FontSize = 11 };
        var approve = new Button { Content = "Aprovar para treino", Padding = new Avalonia.Thickness(8, 3) };
        var reject = new Button { Content = "Rejeitar", Padding = new Avalonia.Thickness(8, 3) };
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        actions.Children.Add(approve); actions.Children.Add(reject);
        async Task Save(TrainingVerdict verdict)
        {
            await learning.SetVerdictAsync(taskId, verdict, null, CancellationToken.None);
            approve.IsEnabled = false; reject.IsEnabled = false;
            label.Text = verdict == TrainingVerdict.Approved ? "Exemplo aprovado para o dataset de treino." : "Exemplo rejeitado; será usado apenas como evidência de recuperação.";
            Activity(label.Text);
        }
        approve.Click += async (_, _) => await Save(TrainingVerdict.Approved);
        reject.Click += async (_, _) => await Save(TrainingVerdict.Rejected);
        var panel = new StackPanel { Spacing = 5, Margin = new Avalonia.Thickness(0, -3, 0, 8) };
        panel.Children.Add(label); panel.Children.Add(actions); Thread.Children.Add(panel);
        Dispatcher.UIThread.Post(() => Scroller.ScrollToEnd(), DispatcherPriority.Background);
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
