using System.Data.Common;

namespace EtlPipelines.Extensions.Sql.Connections;

/// <summary>
/// Runs a fixed set of statements on every connection another factory opens.
/// </summary>
/// <remarks>
/// A decorator rather than a setting on one factory, so it composes over a hand-written
/// <see cref="IDbConnectionFactory"/> as readily as over the built-in one — and so that what it does
/// can be tested without a database.
/// </remarks>
public sealed class SessionDbConnectionFactory : IDbConnectionFactory
{
    private readonly IDbConnectionFactory _inner;
    private readonly string[] _statements;

    /// <summary>Wraps <paramref name="inner"/>, running <paramref name="statements"/> after each open.</summary>
    public SessionDbConnectionFactory(IDbConnectionFactory inner, IEnumerable<string> statements)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(statements);

        _inner = inner;

        // Copied, so that a list the caller goes on editing cannot change what an open run does.
        _statements = [.. statements];
    }

    /// <inheritdoc />
    public string Name => _inner.Name;

    /// <summary>The statements this factory runs, in order.</summary>
    /// <remarks>
    /// Exposed because "which schema is this connection actually pointed at" is a question worth
    /// being able to answer from a log line or a test, without opening a connection to find out.
    /// </remarks>
    public IReadOnlyList<string> SessionStatements => _statements;

    /// <inheritdoc />
    public async ValueTask<DbConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = await _inner.OpenAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            foreach (var statement in _statements)
            {
                await using var command = connection.CreateCommand();
                command.CommandText = statement;

                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch
        {
            // A connection whose session could not be set up is not one to hand back: it would do
            // the caller's work against the wrong schema, or with the wrong settings, and say nothing.
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        return connection;
    }
}
