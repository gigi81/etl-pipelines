using Testcontainers.MariaDb;

namespace EtlPipelines.Samples.Tests.Fixtures;

/// <summary>MariaDB through the MySQL connector, which is the claim worth testing.</summary>
public sealed class MariaDbFixture : DatabaseFixture<MariaDbContainer>
{
    protected override MariaDbContainer CreateContainer() =>
        new MariaDbBuilder(ContainerImages.MariaDb).WithCommand("--local-infile=1").Build();

    public override string ConnectionString => $"{base.ConnectionString};AllowLoadLocalInfile=true";
}
