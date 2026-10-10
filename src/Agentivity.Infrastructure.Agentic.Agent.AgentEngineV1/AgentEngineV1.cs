using Agentivity.Infrastructure.Agentic.Agent;
using Agentivity.Infrastructure.Agentic.Agent.ModeRuntime.Native;
using Agentivity.Infrastructure.Agentic.Agent.ModeRuntime.ReAct;

namespace Agentivity.Infrastructure.Agentic.AgentEngineV1;

/// <summary>
/// Default agentic engine exposing all built-in mode runtimes.
/// </summary>
public sealed class AgentEngine : IAgentEngine
{
    public const string EngineId = "agentic-engine-v1";

    private readonly IAgenticModeRuntime[] _modeRuntimes;
    private readonly string[] _supportedModes;

    public AgentEngine()
    {
        _modeRuntimes = [new ToolsModeRuntime(), new ConversationalModeRuntime(), new ReactModeRuntime()];
        _supportedModes = _modeRuntimes.Select(modeRuntime => modeRuntime.ModeId).ToArray();
    }

    public string Id => EngineId;

    public IReadOnlyCollection<IAgenticModeRuntime> ModeRuntimes => _modeRuntimes;

    public IReadOnlyCollection<string> SupportedModes => _supportedModes;

    public bool CanHandle(string agentMode)
    {
        if (string.IsNullOrWhiteSpace(agentMode))
        {
            return false;
        }

        foreach (var supportedMode in _supportedModes)
        {
            if (string.Equals(supportedMode, agentMode, System.StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    public bool TryGetModeRuntime(string agentMode, out IAgenticModeRuntime? modeRuntime)
    {
        modeRuntime = null;
        if (string.IsNullOrWhiteSpace(agentMode))
        {
            return false;
        }

        foreach (var modeRuntimeCandidate in _modeRuntimes)
        {
            if (string.Equals(modeRuntimeCandidate.ModeId, agentMode, System.StringComparison.OrdinalIgnoreCase))
            {
                modeRuntime = modeRuntimeCandidate;
                return true;
            }
        }

        return false;
    }
}
