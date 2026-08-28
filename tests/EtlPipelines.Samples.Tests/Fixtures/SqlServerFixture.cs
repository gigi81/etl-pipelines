using Testcontainers.MsSql;

namespace EtlPipelines.Samples.Tests.Fixtures;

public sealed class SqlServerFixture : DatabaseFixture<MsSqlContainer>
{
    protected override MsSqlContainer CreateContainer() =>
        new MsSqlBuilder(ContainerImages.SqlServer).Build();
}