namespace EtlPipelines.Server.Database;

/// <summary>
/// Placeholder. Phase 3 of SERVER.md's server/agent plan ("EtlPipelines.Server.Database") adds
/// the real EF Core <c>DbContext</c> and hand-written <c>IEntityTypeConfiguration&lt;T&gt;</c>
/// classes here, mapped onto the schema <c>db/postgres</c>'s <c>dbdeploy</c> scripts own - never
/// <c>dotnet ef migrations</c>.
/// </summary>
/// <remarks>
/// Phase 2 only stands this project up so it participates in the solution and builds clean under
/// <c>TreatWarningsAsErrors=true</c> - see that phase's own opening line, "no network code yet."
/// Remove this type once Phase 3 gives the project real content.
/// </remarks>
public static class AssemblyMarker
{
    /// <summary>The SERVER.md phase that gives this project its real content.</summary>
    public const int RealContentLandsInPhase = 3;
}
