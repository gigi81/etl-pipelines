using EtlPipelines.Server.Database.Entities;
using Microsoft.EntityFrameworkCore;

namespace EtlPipelines.Server.Database.Tests;

/// <summary>
/// Fast tests against EF Core's SQLite <c>EnsureCreated()</c> provider - mapping/query logic only.
/// See <see cref="SqliteServerDbContext"/>'s own remarks for why this is not a substitute for
/// proving <c>db</c>'s dbdeploy scripts are correct.
/// </summary>
[Category("Server")]
public class ServerDbContextTests
{
    [Test]
    public async Task A_package_version_can_register_more_than_one_pipeline()
    {
        //arrange
        await using var context = SqliteServerDbContext.Create();

        var package = new Package
        {
            Id = Guid.NewGuid(),
            NugetPackageId = "EtlPipelines.Samples.ArchiveToDatabase",
            CreatedAt = DateTime.UtcNow,
        };
        var version = new PackageVersion
        {
            Id = Guid.NewGuid(),
            PackageId = package.Id,
            Version = "1.0.0",
            InstalledAt = DateTime.UtcNow,
            Status = "Installed",
        };

        context.Packages.Add(package);
        context.PackageVersions.Add(version);
        context.Pipelines.AddRange(
            new Pipeline { Id = Guid.NewGuid(), PackageVersionId = version.Id, Name = "build-feed", CreatedAt = DateTime.UtcNow },
            new Pipeline { Id = Guid.NewGuid(), PackageVersionId = version.Id, Name = "archive", CreatedAt = DateTime.UtcNow });

        //act
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();

        //assert - EtlPipelines.Samples.ArchiveToDatabase itself proves one package can register
        // more than one pipeline; Pipelines is a child of PackageVersions for exactly this reason.
        var names = await context.Pipelines
            .Where(pipeline => pipeline.PackageVersionId == version.Id)
            .Select(pipeline => pipeline.Name)
            .ToListAsync();

        names.Should().BeEquivalentTo(["build-feed", "archive"]);

        var storedVersion = await context.PackageVersions.SingleAsync(v => v.Id == version.Id);
        storedVersion.Status.Should().Be("Installed");
    }

    [Test]
    public async Task A_package_cannot_have_the_same_version_installed_twice()
    {
        //arrange
        await using var context = SqliteServerDbContext.Create();
        var packageId = Guid.NewGuid();

        context.Packages.Add(new Package { Id = packageId, NugetPackageId = "EtlPipelines.Samples.CsvToDatabase", CreatedAt = DateTime.UtcNow });
        context.PackageVersions.Add(new PackageVersion
        {
            Id = Guid.NewGuid(),
            PackageId = packageId,
            Version = "1.0.0",
            InstalledAt = DateTime.UtcNow,
            Status = "Installed",
        });
        await context.SaveChangesAsync();

        context.PackageVersions.Add(new PackageVersion
        {
            Id = Guid.NewGuid(),
            PackageId = packageId,
            Version = "1.0.0",
            InstalledAt = DateTime.UtcNow,
            Status = "Installed",
        });

        //act
        var act = () => context.SaveChangesAsync();

        //assert - UX_PackageVersions_PackageId_Version
        await act.Should().ThrowAsync<DbUpdateException>();
    }

    [Test]
    public async Task An_agent_s_tags_round_trip_as_a_primitive_collection()
    {
        //arrange
        await using var context = SqliteServerDbContext.Create();
        var agentId = Guid.NewGuid();

        context.Agents.Add(new Agent
        {
            Id = agentId,
            MachineName = "agent-01",
            Tags = ["linux", "gpu"],
            Version = "1.0.0",
            Status = "Online",
            LastHeartbeatAt = DateTime.UtcNow,
        });

        //act
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();
        var reloaded = await context.Agents.SingleAsync(agent => agent.Id == agentId);

        //assert
        reloaded.Tags.Should().BeEquivalentTo(["linux", "gpu"]);
        reloaded.Status.Should().Be("Online");
    }

    [Test]
    public async Task A_run_is_queued_with_no_agent_until_one_claims_it()
    {
        //arrange
        await using var context = await SeedPipelineAsync();
        var pipelineId = await context.Pipelines.Select(pipeline => pipeline.Id).SingleAsync();
        var runId = Guid.NewGuid();

        context.Runs.Add(new Run
        {
            Id = runId,
            PipelineId = pipelineId,
            Status = "Queued",
            RequestedAt = DateTime.UtcNow,
        });

        //act
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();
        var run = await context.Runs.SingleAsync(r => r.Id == runId);

        //assert
        run.AgentId.Should().BeNull();
        run.Status.Should().Be("Queued");
    }

    [Test]
    public async Task Two_stage_results_for_the_same_run_cannot_share_a_sequence_number()
    {
        //arrange
        await using var context = await SeedPipelineAsync();
        var pipelineId = await context.Pipelines.Select(pipeline => pipeline.Id).SingleAsync();
        var runId = Guid.NewGuid();

        context.Runs.Add(new Run { Id = runId, PipelineId = pipelineId, Status = "Running", RequestedAt = DateTime.UtcNow });
        context.StageResults.Add(new StageResult
        {
            Id = Guid.NewGuid(), RunId = runId, Sequence = 0, Name = "seed",
            RowsIn = 0, RowsOut = 10, RowsFailed = 0, ElapsedMs = 5,
        });
        await context.SaveChangesAsync();

        context.StageResults.Add(new StageResult
        {
            Id = Guid.NewGuid(), RunId = runId, Sequence = 0, Name = "seed-again",
            RowsIn = 0, RowsOut = 10, RowsFailed = 0, ElapsedMs = 5,
        });

        //act
        var act = () => context.SaveChangesAsync();

        //assert - UX_StageResults_RunId_Sequence
        await act.Should().ThrowAsync<DbUpdateException>();
    }

    [Test]
    public async Task A_configuration_entry_s_encrypted_value_round_trips_as_opaque_bytes()
    {
        //arrange
        await using var context = SqliteServerDbContext.Create();
        byte[] encrypted = [1, 2, 3, 4, 5];

        context.ConfigurationEntries.Add(new ConfigurationEntry
        {
            Id = Guid.NewGuid(),
            Key = "ConnectionStrings:sales",
            EncryptedValue = encrypted,
            UpdatedAt = DateTime.UtcNow,
        });

        //act
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();
        var reloaded = await context.ConfigurationEntries.SingleAsync(entry => entry.Key == "ConnectionStrings:sales");

        //assert - never decrypted here or anywhere in this project; round-tripped as opaque bytes,
        // exactly as SecretsStore (Phase 4) hands it over.
        reloaded.EncryptedValue.Should().Equal(encrypted);
    }

    [Test]
    public async Task A_configuration_key_cannot_be_set_twice()
    {
        //arrange
        await using var context = SqliteServerDbContext.Create();

        context.ConfigurationEntries.Add(new ConfigurationEntry
        {
            Id = Guid.NewGuid(), Key = "Sftp:vendor:Host", EncryptedValue = [1], UpdatedAt = DateTime.UtcNow,
        });
        await context.SaveChangesAsync();

        context.ConfigurationEntries.Add(new ConfigurationEntry
        {
            Id = Guid.NewGuid(), Key = "Sftp:vendor:Host", EncryptedValue = [2], UpdatedAt = DateTime.UtcNow,
        });

        //act
        var act = () => context.SaveChangesAsync();

        //assert - UX_ConfigurationEntries_Key
        await act.Should().ThrowAsync<DbUpdateException>();
    }

    /// <summary>A package, one installed version, and one pipeline - the fixture every Run/StageResult test needs in place first.</summary>
    private static async Task<SqliteServerDbContext> SeedPipelineAsync()
    {
        var context = SqliteServerDbContext.Create();
        var packageId = Guid.NewGuid();
        var versionId = Guid.NewGuid();

        context.Packages.Add(new Package { Id = packageId, NugetPackageId = "EtlPipelines.Samples.CsvToDatabase", CreatedAt = DateTime.UtcNow });
        context.PackageVersions.Add(new PackageVersion
        {
            Id = versionId, PackageId = packageId, Version = "1.0.0",
            InstalledAt = DateTime.UtcNow, Status = "Installed",
        });
        context.Pipelines.Add(new Pipeline { Id = Guid.NewGuid(), PackageVersionId = versionId, Name = "csv-to-database", CreatedAt = DateTime.UtcNow });

        await context.SaveChangesAsync();
        return context;
    }
}
