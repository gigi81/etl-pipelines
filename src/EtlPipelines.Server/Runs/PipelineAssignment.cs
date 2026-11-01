using EtlPipelines.Server.Agents;

namespace EtlPipelines.Server.Runs;

/// <summary>
/// Which agents may run a pipeline: any, when nothing is assigned; otherwise an agent that has any of
/// <see cref="Tags"/> or is one of <see cref="AgentIds"/>.
/// </summary>
/// <remarks>
/// "Any of", not "all of": assigning a pipeline to <c>gpu</c> and <c>eu</c> lets it run on an agent with
/// either, which is how a pipeline that can run in several places is described. Naming an agent is for the
/// one machine that has to run it.
/// </remarks>
/// <param name="Tags">Tags; lower case.</param>
/// <param name="AgentIds">Agents named explicitly.</param>
public sealed record PipelineAssignment(IReadOnlyList<string> Tags, IReadOnlyList<Guid> AgentIds)
{
    /// <summary>Nothing assigned: any agent may run the pipeline.</summary>
    public static PipelineAssignment Any { get; } = new([], []);

    /// <summary>Whether any agent may run the pipeline.</summary>
    public bool IsAny => Tags.Count == 0 && AgentIds.Count == 0;

    /// <summary>Whether the agent <paramref name="agentId"/>, which has <paramref name="agentTags"/>, may run the pipeline.</summary>
    public bool Allows(Guid agentId, IEnumerable<string> agentTags) =>
        IsAny || AgentIds.Contains(agentId) || agentTags.Any(tag => Tags.Contains(tag, TagNames.Comparer));

    /// <summary>The assignment in words, for the message when no agent fits: <c>tagged 'gpu' or 'eu'; agent 3f2a...</c>.</summary>
    public string Describe()
    {
        if (IsAny)
        {
            return "any agent";
        }

        var parts = new List<string>();
        if (Tags.Count > 0)
        {
            parts.Add($"tagged {string.Join(" or ", Tags.Select(tag => $"'{tag}'"))}");
        }

        if (AgentIds.Count > 0)
        {
            parts.Add($"agent {string.Join(", ", AgentIds)}");
        }

        return string.Join("; ", parts);
    }
}
