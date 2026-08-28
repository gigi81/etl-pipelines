using Testcontainers.MySql;

namespace EtlPipelines.Samples.Tests.Fixtures;

public sealed class MySqlFixture : DatabaseFixture<MySqlContainer>
{
    protected override MySqlContainer CreateContainer() =>
        new MySqlBuilder(ContainerImages.MySql).WithCommand("--local-infile=1").Build();

    public override string ConnectionString => $"{base.ConnectionString};AllowLoadLocalInfile=true";
}