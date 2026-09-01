using System.Collections.Concurrent;
using System.Text.Json;
using CliWrap;
using CliWrap.Buffered;

namespace EtlPipelines.Server.Tests;

/// <summary>
/// Runs <c>dbdeploy deploy</c> against a Postgres connection string exactly once, no matter how
/// many <c>[Category("Docker")]</c> test classes in this project ask for it against the same
/// <c>[ClassDataSource&lt;PostgreSqlFixture&gt;(Shared = SharedType.PerAssembly)]</c> container - a
/// second <c>deploy</c> against an already-deployed database fails outright ("relation
/// \"Packages\" already exists"), unlike a real <c>dbdeploy</c> run where re-deploying an
/// up-to-date database is a normal no-op (there is nothing left in <c>main.csv</c> to apply a
/// second time; the failure here is specific to re-staging the very same <c>_Init</c> step against
/// a database that has already recorded it as deployed within one temporary <c>--path</c>).
/// </summary>
internal static class SchemaDeployer
{
    // Lazy<Task>, not a bare Task, keyed in the dictionary - ConcurrentDictionary.GetOrAdd does
    // not itself guarantee its value factory runs only once under concurrent calls for the same
    // key (several callers can race into invoking it before the first one publishes a value,
    // discarding all losers' results but not before each one's side effects already ran) -
    // confirmed the hard way, by this exact race actually reaching a real Postgres and re-running
    // `dbdeploy deploy` against an already-deployed database. Lazy<Task>'s own thread safety is
    // what closes that gap: constructing a Lazy is cheap and can happen more than once here
    // harmlessly, but only the one instance GetOrAdd actually stores ever has its factory invoked,
    // no matter how many threads call .Value on it.
    private static readonly ConcurrentDictionary<string, Lazy<Task>> DeployTasks = new();

    /// <summary>Deploys <c>db/server/_Init.sql</c> against <paramref name="connectionString"/>, once per distinct connection string.</summary>
    public static Task EnsureDeployedAsync(string connectionString) =>
        DeployTasks.GetOrAdd(connectionString, cs => new Lazy<Task>(() => DeploySchemaAsync(cs))).Value;

    private static async Task DeploySchemaAsync(string connectionString)
    {
        var work = Directory.CreateTempSubdirectory("EtlPipelines.Server.Tests.");

        try
        {
            await StageScriptsAsync(work.FullName, connectionString);

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

            if (deploy.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    $"dbdeploy deploy failed: {(string.IsNullOrWhiteSpace(deploy.StandardError) ? deploy.StandardOutput : deploy.StandardError)}");
            }
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

    private static async Task StageScriptsAsync(string workDirectory, string connectionString)
    {
        var sourceServerDirectory = Path.Combine(RepositoryPaths.DbDirectory, "server");
        var destinationServerDirectory = Directory.CreateDirectory(Path.Combine(workDirectory, "server"));

        foreach (var script in Directory.GetFiles(sourceServerDirectory))
        {
            File.Copy(script, Path.Combine(destinationServerDirectory.FullName, Path.GetFileName(script)));
        }

        File.Copy(
            Path.Combine(RepositoryPaths.DbDirectory, "main.csv"),
            Path.Combine(workDirectory, "main.csv"));

        var settings = JsonSerializer.Serialize(new
        {
            global = new { defaultProvider = "postgreSql", scriptTimeout = 600 },
            databases = new Dictionary<string, object>
            {
                ["server"] = new { connectionString },
            },
        });

        await File.WriteAllTextAsync(Path.Combine(workDirectory, "dbsettings.json"), settings);
    }
}
