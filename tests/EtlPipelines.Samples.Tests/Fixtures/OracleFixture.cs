using Testcontainers.Oracle;

namespace EtlPipelines.Samples.Tests.Fixtures;

public sealed class OracleFixture : DatabaseFixture<OracleContainer>
{
    protected override OracleContainer CreateContainer() =>
        new OracleBuilder(ContainerImages.Oracle).Build();
}