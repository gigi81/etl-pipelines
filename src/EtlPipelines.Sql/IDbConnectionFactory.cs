using System.Data.Common;

namespace EtlPipelines.Sql;

/// <summary>
/// Opens connections to one database.
/// </summary>
/// <remarks>
/// <para>
/// A factory rather than a connection, deliberately. A source holds an open reader for the whole run
/// and a sink holds an open transaction for the whole run, so the two cannot share one connection —
/// and a <see cref="DbConnection"/> registered in a container would be shared by every component
/// that asked for it, across concurrent runs. The factory is the thing that is safe to share; the
/// connections it hands out are owned and disposed by whoever asked for one.
/// </para>
/// <para>
/// Register one per database with the provider package's <c>Add…Connection</c> method, which names
/// it — <c>FromSql</c> and <c>ToSqlTable</c> then refer to that name, so a pipeline can read from one
/// database and write to another.
/// </para>
/// </remarks>
public interface IDbConnectionFactory
{
    /// <summary>The name this factory is registered under.</summary>
    string Name { get; }

    /// <summary>Opens a new connection. The caller owns it and disposes it.</summary>
    ValueTask<DbConnection> OpenAsync(CancellationToken cancellationToken);
}

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
