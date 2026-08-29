using System.Reflection;

namespace EtlPipelines.Extensions.Sql.Scripts;

/// <summary>
/// Reads a script compiled into an assembly as an embedded resource.
/// </summary>
/// <remarks>
/// <para>
/// A script that ships with the application rather than beside it: nothing to copy on deploy, and
/// nothing to go missing between the build and the run.
/// </para>
/// <para>
/// The name is matched leniently, because the logical name of a resource is not the file name and
/// almost nobody remembers the rule — <c>Scripts/create.sql</c> under a root namespace of
/// <c>My.App</c> becomes <c>My.App.Scripts.create.sql</c>. An exact match wins; failing that, a
/// resource whose name ends in <c>.create.sql</c> is taken when exactly one does. Nothing matching,
/// or more than one, and the error lists what the assembly actually holds — which is the question
/// anyone in that position is about to go looking for the answer to.
/// </para>
/// </remarks>
public sealed class EmbeddedSqlScriptSource : ISqlScriptSource
{
    private readonly Assembly _assembly;
    private readonly string _resourceName;

    /// <summary>Reads <paramref name="resourceName"/> out of <paramref name="assembly"/>.</summary>
    /// <param name="assembly">The assembly the script is compiled into.</param>
    /// <param name="resourceName">
    /// The resource's logical name, or the tail of it — <c>create.sql</c> finds
    /// <c>My.App.Scripts.create.sql</c> as long as nothing else ends the same way.
    /// </param>
    public EmbeddedSqlScriptSource(Assembly assembly, string resourceName)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        ArgumentException.ThrowIfNullOrWhiteSpace(resourceName);

        _assembly = assembly;
        _resourceName = resourceName;
    }

    /// <inheritdoc />
    public string Name => _resourceName;

    /// <inheritdoc />
    public ValueTask<ErrorOr<TextReader>> OpenAsync(CancellationToken cancellationToken)
    {
        var names = _assembly.GetManifestResourceNames();

        var resolved = Array.Find(names, name => string.Equals(name, _resourceName, StringComparison.Ordinal))
            ?? Resolve(names);

        if (resolved is null)
        {
            return ValueTask.FromResult<ErrorOr<TextReader>>(Error.Failure(
                "sql.script.missing",
                $"No embedded resource named '{_resourceName}' in {_assembly.GetName().Name}. " +
                $"It holds: {(names.Length == 0 ? "(none)" : string.Join(", ", names))}. " +
                "A resource's logical name is its root namespace and folder path joined with dots - " +
                "and a file is only embedded at all if the project says so, with an EmbeddedResource item."));
        }

        var stream = _assembly.GetManifestResourceStream(resolved);

        return stream is null
            ? ValueTask.FromResult<ErrorOr<TextReader>>(Error.Failure(
                "sql.script.missing",
                $"The embedded resource '{resolved}' in {_assembly.GetName().Name} could not be opened."))
            : ValueTask.FromResult<ErrorOr<TextReader>>(new StreamReader(stream));
    }

    /// <summary>The one resource whose name ends with the one asked for, when there is exactly one.</summary>
    private string? Resolve(string[] names)
    {
        var suffix = $".{_resourceName}";
        string? found = null;

        foreach (var name in names)
        {
            if (!name.EndsWith(suffix, StringComparison.Ordinal))
            {
                continue;
            }

            // Two of them and the guess would be a coin toss, so it is not made.
            if (found is not null)
            {
                return null;
            }

            found = name;
        }

        return found;
    }
}
