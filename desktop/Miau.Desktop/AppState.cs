using System.Text.Json;

namespace Miau.Desktop;

public sealed class AppState
{
    public string? LastWorkspace { get; set; }
    public List<string> RecentProjects { get; set; } = [];
    public string Model { get; set; } = "qwen2.5-coder:7b";
    public string? VisionModel { get; set; }
    public string AgentId { get; set; } = $"{Environment.MachineName}-{(OperatingSystem.IsMacOS() ? "MAC" : OperatingSystem.IsWindows() ? "WIN" : "LOCAL")}";
    public bool AutonomousMode { get; set; }
    public int NextTaskDelaySeconds { get; set; } = 120;
    public bool TrainingEnabled { get; set; }
    public int TrainingIntervalHours { get; set; } = 24;
    public int TrainingMaxTasksPerCycle { get; set; } = 3;
    public bool SelfRepairEnabled { get; set; }
    public int SelfRepairEvidenceThreshold { get; set; } = 3;

    static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MIAU", "state.json");

    public static AppState Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var loaded = JsonSerializer.Deserialize<AppState>(File.ReadAllText(FilePath)) ?? new();
                // Migrate the former 6-hour default without overriding explicit custom schedules.
                if (loaded.TrainingIntervalHours == 6)
                {
                    loaded.TrainingIntervalHours = 24;
                    loaded.Save();
                }
                return loaded;
            }
        }
        catch { }
        return new();
    }

    public void RememberProject(string path)
    {
        LastWorkspace = path;
        RecentProjects.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
        RecentProjects.Insert(0, path);
        if (RecentProjects.Count > 12) RecentProjects.RemoveRange(12, RecentProjects.Count - 12);
        Save();
    }

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }
}
