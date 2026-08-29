namespace EtlPipelines.Cli;

/// <summary>What a stage is called when the caller did not say.</summary>
/// <remarks>
/// Collapses whitespace and truncates to a short, report-friendly length - the same rule
/// <c>EtlPipelines.Sql.Stages.SqlCommandStage</c> uses for an unnamed statement, so an unnamed
/// command reads the same way in a run's report regardless of which extension produced it.
/// </remarks>
internal static class CommandName
{
    public static string Truncate(string text, int max = 40)
    {
        var collapsed = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

        return collapsed.Length <= max ? collapsed : $"{collapsed[..(max - 3)]}...";
    }
}
