namespace EtlPipelines.Sql.Connections;

/// <summary>Settings applied to every connection a named registration opens.</summary>
public class DbConnectionOptions
{
    /// <summary>
    /// Statements run on each connection immediately after it opens, in order, before anything else
    /// uses it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Session settings the engine offers no other way to reach: <c>ALTER SESSION SET
    /// NLS_DATE_FORMAT</c>, a statement timeout, <c>SET ROLE</c>. The provider packages build on this
    /// for the settings they can name — see their <c>CurrentSchema</c>.
    /// </para>
    /// <para>
    /// On every open rather than once at startup, because session state rides the physical connection
    /// and a pool hands those back out. The two engines here differ in opposite directions: ODP.NET
    /// leaves an <c>ALTER SESSION</c> in place on a pooled connection, while Npgsql discards a
    /// <c>SET</c> when the connection returns. Re-applying is what makes both of them predictable.
    /// </para>
    /// <para>
    /// <b>What it cannot make predictable is everyone else.</b> A connection ODP.NET pools keeps
    /// whatever was set on it, so other code opening the <i>same connection string</i> — another
    /// registration, an ORM, a health check — can be handed a connection still pointed somewhere it
    /// does not expect. Give a connection carrying session settings a connection string of its own,
    /// or turn pooling off on it.
    /// </para>
    /// </remarks>
    public IList<string> SessionStatements { get; } = [];
}
