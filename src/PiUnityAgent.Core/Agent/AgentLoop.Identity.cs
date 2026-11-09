using PiUnityAgent.Ai.Types;

namespace PiUnityAgent.Core.Agent;

public static partial class AgentLoop
{
    /// <summary>请求身份缺省：UA 归属 pi。对应 pi DEFAULT_AGENT_IDENTITY。</summary>
    public static ClientIdentity DefaultAgentIdentity { get; } =
        new() { Attribution = ClientIdentityHelpers.PiAttribution };
}
