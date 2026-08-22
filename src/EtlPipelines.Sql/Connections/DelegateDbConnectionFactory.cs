using System.Data.Common;

namespace EtlPipelines.Sql.Connections;

/// <summary>An <see cref="IDbConnectionFactory"/> built from a delegate.</summary>
/// <remarks>
/// The adapter for a connection that does not come from configuration — a database whose address is
/// only known at run time, which is what a test using a throwaway container has.
/// </remarks>
public sealed class DelegateDbConnectionFactory : IDbConnectionFactory
{
    private readonly Func<CancellationToken, ValueTask<DbConnection>> _open;

    /// <summary>Wraps <paramref name="open"/> under <paramref name="name"/>.</summary>
    public DelegateDbConnectionFactory(string name, Func<CancellationToken, ValueTask<DbConnection>> open)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(open);

        Name = name;
        _open = open;
    }

    /// <inheritdoc />
    public string Name { get; }

    /// <inheritdoc />
    public ValueTask<DbConnection> OpenAsync(CancellationToken cancellationToken) => _open(cancellationToken);
}
