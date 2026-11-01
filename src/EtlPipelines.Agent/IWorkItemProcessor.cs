using EtlPipelines.AgentExecution.V1;

namespace EtlPipelines.Agent;

/// <summary>What <see cref="AgentRegistration"/> hands the work items it receives to.</summary>
public interface IWorkItemProcessor
{
    /// <summary>Starts <paramref name="workItem"/> in the background and returns at once. <paramref name="cancellationToken"/> is the agent's own lifetime, not the connection's.</summary>
    void Process(string agentId, WorkItem workItem, CancellationToken cancellationToken);

    /// <summary>Completes when every work item started so far has finished or been cancelled.</summary>
    Task WhenIdleAsync();
}
