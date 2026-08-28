using Testcontainers.PostgreSql;

namespace EtlPipelines.Samples.Tests.Fixtures;

public sealed class PostgreSqlFixture : DatabaseFixture<PostgreSqlContainer>
{
    protected override PostgreSqlContainer CreateContainer() =>
        new PostgreSqlBuilder(ContainerImages.PostgreSql).Build();
}