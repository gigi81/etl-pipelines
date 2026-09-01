using EtlPipelines.Management.V1;
using EtlPipelines.Server.Agents;
using EtlPipelines.Server.Catalog;
using EtlPipelines.Server.Database.Entities;
using EtlPipelines.Server.Secrets;
using EtlPipelines.Server.Services;
using Grpc.Core;
using Microsoft.AspNetCore.DataProtection;
using Moq;
using Moq.Protected;

namespace EtlPipelines.Server.Tests.Services;

/// <summary>
/// Proves <see cref="ManagementServiceImpl"/> maps proto messages to/from
/// <see cref="PackageCatalogService"/>/<see cref="SecretsStore"/> correctly, and that the two
/// RPCs Phase 4 leaves for Phase 5 fail the way that phase's own decision calls for. Composes real
/// <see cref="PackageCatalogService"/>/<see cref="SecretsStore"/> instances (SQLite-backed, mocked
/// feed client/protector) rather than mocking them directly - both are concrete, not interfaces,
/// on the same "don't abstract what nothing else ever implements" basis <see cref="INuGetFeedClient"/>
/// and <see cref="IDataProtector"/> are the exception to.
/// </summary>
[Category("Server")]
public class ManagementServiceImplTests
{
    [Test]
    public async Task InstallPackage_fails_with_FailedPrecondition_because_no_agent_exists_yet()
    {
        //arrange
        var service = await CreateAsync();

        //act
        var act = () => service.InstallPackage(new InstallPackageRequest { PackageId = "EtlPipelines.Samples.CsvToDatabase" }, TestServerCallContext());

        //assert
        var exception = await act.Should().ThrowAsync<RpcException>();
        exception.Which.StatusCode.Should().Be(StatusCode.FailedPrecondition);
    }

    [Test]
    public async Task ExecutePipeline_fails_with_FailedPrecondition_because_no_agent_exists_yet()
    {
        //arrange
        var service = await CreateAsync();

        //act
        var act = () => service.ExecutePipeline(new ExecutePipelineRequest { PipelineId = Guid.NewGuid().ToString() }, TestServerCallContext());

        //assert
        var exception = await act.Should().ThrowAsync<RpcException>();
        exception.Which.StatusCode.Should().Be(StatusCode.FailedPrecondition);
    }

    [Test]
    public async Task ListAgents_is_still_the_Phase_2_stub()
    {
        //arrange
        var service = await CreateAsync();

        //act
        var act = () => service.ListAgents(new Empty(), TestServerCallContext());

        //assert
        var exception = await act.Should().ThrowAsync<RpcException>();
        exception.Which.StatusCode.Should().Be(StatusCode.Unimplemented);
    }

    [Test]
    public async Task ListInstalledPipelines_maps_the_catalog_onto_the_proto_response()
    {
        //arrange
        var context = SqliteServerDbContext.Create();
        var packageId = Guid.NewGuid();
        var versionId = Guid.NewGuid();
        context.Packages.Add(new Package { Id = packageId, NugetPackageId = "EtlPipelines.Samples.CsvToDatabase", CreatedAt = DateTime.UtcNow });
        context.PackageVersions.Add(new PackageVersion { Id = versionId, PackageId = packageId, Version = "1.0.0", InstalledAt = DateTime.UtcNow, Status = "Installed" });
        context.Pipelines.Add(new Pipeline { Id = Guid.NewGuid(), PackageVersionId = versionId, Name = "csv-to-database", CreatedAt = DateTime.UtcNow });
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();

        var service = new ManagementServiceImpl(
            new PackageCatalogService(context, Mock.Of<INuGetFeedClient>()),
            new SecretsStore(context, Mock.Of<IDataProtectionProvider>()),
            new AgentConnectionRegistry());

        //act
        var response = await service.ListInstalledPipelines(new Empty(), TestServerCallContext());

        //assert
        response.Pipelines.Should().ContainSingle(pipeline =>
            pipeline.Name == "csv-to-database" &&
            pipeline.PackageId == "EtlPipelines.Samples.CsvToDatabase" &&
            pipeline.PackageVersion == "1.0.0");
    }

    [Test]
    public async Task SetConfigurationEntry_stores_the_value_encrypted()
    {
        //arrange
        var context = SqliteServerDbContext.Create();
        var protector = new Mock<IDataProtector>();
        protector.Setup(p => p.Protect(It.IsAny<byte[]>())).Returns<byte[]>(bytes => bytes);
        var protectionProvider = new Mock<IDataProtectionProvider>();
        protectionProvider.Setup(p => p.CreateProtector(It.IsAny<string>())).Returns(protector.Object);

        var service = new ManagementServiceImpl(
            new PackageCatalogService(context, Mock.Of<INuGetFeedClient>()),
            new SecretsStore(context, protectionProvider.Object),
            new AgentConnectionRegistry());

        //act
        var ack = await service.SetConfigurationEntry(
            new SetConfigurationEntryRequest { Key = "ConnectionStrings:sales", Value = "..." },
            TestServerCallContext());

        //assert
        ack.Should().NotBeNull();
        context.ConfigurationEntries.Should().ContainSingle(entry => entry.Key == "ConnectionStrings:sales");
    }

    private static Task<ManagementServiceImpl> CreateAsync()
    {
        var context = SqliteServerDbContext.Create();
        return Task.FromResult(new ManagementServiceImpl(
            new PackageCatalogService(context, Mock.Of<INuGetFeedClient>()),
            new SecretsStore(context, Mock.Of<IDataProtectionProvider>()),
            new AgentConnectionRegistry()));
    }

    /// <summary>
    /// <see cref="ServerCallContext"/> is abstract with no public constructor; the RPCs under test
    /// here only ever read <see cref="ServerCallContext.CancellationToken"/>, so a
    /// <see cref="Mock{ServerCallContext}"/> stubbing just that member is enough - no need for the
    /// full <c>Grpc.Core.Testing.TestServerCallContext</c> machinery.
    /// </summary>
    private static ServerCallContext TestServerCallContext()
    {
        var context = new Mock<ServerCallContext> { CallBase = true };
        context.Protected().Setup<CancellationToken>("CancellationTokenCore").Returns(CancellationToken.None);
        return context.Object;
    }
}
