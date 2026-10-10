namespace Agentivity.Infrastructure.Agentic.Agent;

public interface IAgentEngine
{
    string Id { get; }

    IReadOnlyCollection<IAgenticModeRuntime> ModeRuntimes { get; }

    IReadOnlyCollection<string> SupportedModes { get; }

    bool CanHandle(string agentMode);

    bool TryGetModeRuntime(string agentMode, out IAgenticModeRuntime? modeRuntime);
}
