using System.Text.Json;
using CliWrap;
using CliWrap.Buffered;
using EtlPipelines.Server.Database.Entities;
using Microsoft.EntityFrameworkCore;

namespace EtlPipelines.Server.Database.Tests;

/// <summary>
/// The test that matters most in this project: it is the only one that runs <c>dbdeploy deploy</c>
/// against a genuinely empty Postgres container before anything else touches it, proving
/// <c>db/postgres/Server/_Init.sql</c> and
/// <see cref="ServerDbContext"/>'s hand-written mapping actually agree - not merely that each
/// compiles on its own. <see cref="ServerDbContextTests"/> covers everything else (mapping/query
/// logic) against the fast, unrelated SQLite path.
/// </summary>
/// <remarks>
/// Requires <c>dbdeploy</c> on <c>PATH</c> (<c>dotnet tool install --global dbdeploy</c>) in
/// addition to Docker - both are expected of anything that runs <c>[Category("Docker")]</c> tests,
/// the same way Docker itself already is. <c>dbdeploy ci</c> - the verb that proves every rollback
/// actually rolls back - is deliberately not exercised here: it belongs in the CI workflow's own
/// step (see <c>integration-tests.yml</c>), not duplicated inside this suite.
/// </remarks>
[Category("Docker")]
[ClassDataSource<PostgreSqlFixture>(Shared = SharedType.PerAssembly)]
public class ServerDatabaseDockerTests(PostgreSqlFixture fixture)
{
    private PostgreSqlFixture Fixture { get; } = fixture;

    [Test]
    public async Task Dbdeploy_creates_a_schema_ServerDbContext_agrees_with()
    {
        //arrange - copy the real scripts into a scratch directory alongside a dbsettings.json
        // pointed at this test's own container, rather than running dbdeploy against
        // db/postgres/dbsettings.json's own fixed local-dev connection string.
        var work = Directory.CreateTempSubdirectory("EtlPipelines.Server.Database.Tests.");

        try
        {
            await StageScriptsAsync(work.FullName);

            //act
            BufferedCommandResult deploy;
            try
            {
                deploy = await Cli.Wrap("dbdeploy")
                    .WithArguments(["deploy", "--path", work.FullName])
                    .WithValidation(CommandResultValidation.None)
                    .ExecuteBufferedAsync();
            }
            catch (System.ComponentModel.Win32Exception exception)
            {
                throw new InvalidOperationException(
                    "'dbdeploy' could not be started - install it with " +
                    "'dotnet tool install --global dbdeploy' before running [Category(\"Docker\")] tests.",
                    exception);
            }

            //assert - dbdeploy itself succeeded
            deploy.ExitCode.Should().Be(0, $"dbdeploy deploy should succeed: {Tail(deploy)}");

            //assert - and the schema it created is exactly what ServerDbContext's hand-written
            // mapping expects - proven by actually writing and reading a row through it, not just
            // by dbdeploy exiting zero.
            await using var context = CreateContext(Fixture.ConnectionString);
            var packageId = Guid.NewGuid();

            context.Packages.Add(new Package
            {
                Id = packageId,
                NugetPackageId = "EtlPipelines.Samples.CsvToDatabase",
                CreatedAt = DateTime.UtcNow,
            });
            await context.SaveChangesAsync();

            var stored = await context.Packages.SingleAsync(package => package.Id == packageId);
            stored.NugetPackageId.Should().Be("EtlPipelines.Samples.CsvToDatabase");
        }
        finally
        {
            try
            {
                Directory.Delete(work.FullName, recursive: true);
            }
            catch (IOException)
            {
                // A leftover temp directory is not worth failing a test over.
            }
        }
    }

    private async Task StageScriptsAsync(string workDirectory)
    {
        var sourceServerDirectory = Path.Combine(RepositoryPaths.DbPostgresDirectory, "Server");
        var destinationServerDirectory = Directory.CreateDirectory(Path.Combine(workDirectory, "Server"));

        foreach (var script in Directory.GetFiles(sourceServerDirectory))
        {
            File.Copy(script, Path.Combine(destinationServerDirectory.FullName, Path.GetFileName(script)));
        }

        File.Copy(
            Path.Combine(RepositoryPaths.DbPostgresDirectory, "main.csv"),
            Path.Combine(workDirectory, "main.csv"));

        // Same shape as the committed db/postgres/dbsettings.json - only the connection string
        // differs, pointed at this test's own disposable container instead of a fixed local one.
        var settings = JsonSerializer.Serialize(new
        {
            global = new { defaultProvider = "postgreSql", scriptTimeout = 600 },
            databases = new Dictionary<string, object>
            {
                ["Server"] = new { connectionString = Fixture.ConnectionString },
            },
        });

        await File.WriteAllTextAsync(Path.Combine(workDirectory, "dbsettings.json"), settings);
    }

    private static ServerDbContext CreateContext(string connectionString)
    {
        var options = new DbContextOptionsBuilder<ServerDbContext>()
            .UseNpgsql(connectionString)
            .Options;

        return new ServerDbContext(options);
    }

    private static string Tail(BufferedCommandResult result)
    {
        if (!string.IsNullOrWhiteSpace(result.StandardError))
        {
            return result.StandardError.Trim();
        }

        return !string.IsNullOrWhiteSpace(result.StandardOutput) ? result.StandardOutput.Trim() : "(no output)";
    }
}
