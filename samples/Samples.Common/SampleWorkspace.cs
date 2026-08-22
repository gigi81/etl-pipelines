using System.CommandLine;
using System.IO.Abstractions;
using EtlPipelines.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace EtlPipelines.Samples.Common;

/// <summary>
/// The directory a sample reads from and writes into.
/// </summary>
/// <remarks>
/// Not a type of its own: the directory is an <see cref="IDirectoryInfo"/>, registered under
/// <see cref="Key"/> and injected wherever it is wanted. A wrapper around it would only re-expose
/// what <c>IDirectoryInfo</c> and its <c>File</c> extension already offer.
/// <para>
/// Keyed because <c>IDirectoryInfo</c> is far too general a type to register unkeyed — a sample that
/// later wanted a second directory, or a library that registered one of its own, would silently take
/// over. The key says which directory is meant.
/// </para>
/// </remarks>
public static class SampleWorkspace
{
    /// <summary>The key the working directory is registered and resolved under.</summary>
    public const string Key = "workspace";

    /// <summary>The option every sample takes, so its output can be put somewhere you can find it.</summary>
    /// <remarks>
    /// Recursive, so it applies to every verb rather than being repeated on each one. Read while the
    /// container is being composed rather than injected from it: the pipeline's own registration
    /// needs the file paths, and that runs before there is a provider to resolve anything from.
    /// </remarks>
    public static Option<string?> WorkDirOption { get; } = new("--work-dir")
    {
        Description = "Directory to read and write in. Defaults to a new directory under the temp path.",
        Recursive = true,
    };

    /// <summary>
    /// Adds <c>--work-dir</c>, then registers the directory it names along with everything the sample
    /// needs to be handed it.
    /// </summary>
    /// <param name="host">The application being built.</param>
    /// <param name="name">Appears in the default directory name, so a leftover can be traced back.</param>
    /// <param name="configure">
    /// Registers the sample's own services. Given the directory directly, because composing a
    /// pipeline needs the file paths before there is a provider to resolve them from.
    /// </param>
    public static EtlPipelinesHost UseSampleWorkspace(
        this EtlPipelinesHost host,
        string name,
        Action<IServiceCollection, IDirectoryInfo> configure)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        // On the root command rather than on each verb's parameters class, so that it is declared
        // once and can be read from the parse result while services are being registered.
        host.AddCommands(cli => cli.CommandBuilder.RootCommand.Add(WorkDirOption));

        return host.ConfigureServices((result, services) =>
        {
            var directory = Create(host.FileSystem, result, name);

            services.AddKeyedSingleton<IDirectoryInfo>(Key, directory);
            configure(services, directory);
        });
    }

    private static IDirectoryInfo Create(IFileSystem fileSystem, ParseResult result, string name)
    {
        var path = result.GetValue(WorkDirOption)
            ?? fileSystem.Path.Combine(
                fileSystem.Path.GetTempPath(),
                $"etl-sample-{name}-{Guid.NewGuid():N}");

        var directory = fileSystem.DirectoryInfo.New(path);
        directory.Create();

        return directory;
    }
}
