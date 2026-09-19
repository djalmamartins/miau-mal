namespace Miau.Desktop;

public interface IPromptProvider { string GetSystemPrompt(string workspace, JobRequirements requirements); }

public sealed class VersionedPromptProvider : IPromptProvider
{
    readonly string path;
    public VersionedPromptProvider(string? path = null) => this.path = path ?? Path.Combine(AppContext.BaseDirectory, "Prompts", "miau1-coder-v0.txt");
    public string GetSystemPrompt(string workspace, JobRequirements requirements)
    {
        var template = File.ReadAllText(path);
        return template.Replace("{{WORKSPACE}}", workspace).Replace("{{READ_ONLY}}", requirements.ReadOnly.ToString()).Replace("{{REQUIRES_CHANGE}}", requirements.RequiresChange.ToString());
    }
}
