namespace Miau.Agent;
public sealed record AgentRequest(string Workspace, string Prompt, string Model = "qwen3-coder:30b", int MaxSteps = 20);
public sealed record AgentEvent(string Kind, string Message);
public sealed record AgentResult(string Answer, IReadOnlyList<AgentEvent> Events);
