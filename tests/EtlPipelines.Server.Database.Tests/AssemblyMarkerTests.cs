namespace EtlPipelines.Server.Database.Tests;

/// <summary>
/// Phase 2 smoke test: this project has no real content yet (see
/// <see cref="EtlPipelines.Server.Database.AssemblyMarker"/>'s own remarks), so all there is to
/// prove is that it exists, builds, and participates in the solution.
/// </summary>
[Category("Server")]
public class AssemblyMarkerTests
{
    [Test]
    public async Task The_project_builds_and_its_placeholder_type_exists()
    {
        var phase = AssemblyMarker.RealContentLandsInPhase;

        await Assert.That(phase).IsEqualTo(3);
    }
}
