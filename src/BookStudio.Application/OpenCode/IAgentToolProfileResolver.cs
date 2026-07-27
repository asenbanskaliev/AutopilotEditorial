namespace BookStudio.Application.OpenCode;

public interface IAgentToolProfileResolver
{
    EffectiveAgentToolProfile Resolve(
        AgentToolProfileResolutionRequest request,
        CancellationToken cancellationToken = default);
}
