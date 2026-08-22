namespace EtlPipelines.Sql.Configuration;

/// <summary>Settings shared by the stages that hand SQL to the server rather than moving rows.</summary>
public abstract class SqlStageOptions : SqlOptions
{
    /// <summary>
    /// What the stage is called in the run's report, traces and metrics. Defaults to the procedure's
    /// name or the script's file name.
    /// </summary>
    public string? Name { get; set; }
}
