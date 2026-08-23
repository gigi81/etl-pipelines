using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using System.Data.Common;

namespace EtlPipelines.Sql;

/// <summary>
/// Registers named database connections, and resolves them again when a pipeline is composed.
/// </summary>
/// <remarks>
/// Each provider package has its own <c>Add…Connection</c> built on these, so an application names a
/// connection once and the pipeline refers to it by that name from then on.
/// </remarks>
public static class ConnectionRegistration
{
    /// <summary>
    /// Registers a named connection whose connection string comes from <see cref="IConfiguration"/>.
    /// </summary>
    /// <param name="services">The container.</param>
    /// <param name="name">
    /// The name of the connection. Read from the <c>ConnectionStrings</c> section — a connection
    /// named <c>sales</c> is <c>ConnectionStrings:sales</c>, which is what
    /// <see cref="ConfigurationExtensions.GetConnectionString"/> looks up.
    /// </param>
    /// <param name="open">Opens a connection to the given connection string.</param>
    /// <param name="options">Statements to run on each connection once it is open.</param>
    /// <remarks>
    /// Resolved lazily: the configuration is read the first time a run opens a connection, not at
    /// registration, so an application can register its pipelines before configuration is complete.
    /// </remarks>
    public static IServiceCollection AddDbConnection(
        this IServiceCollection services,
        string name,
        Func<string, CancellationToken, ValueTask<DbConnection>> open,
        DbConnectionOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(open);

        services.AddKeyedSingleton<IDbConnectionFactory>(name, (provider, _) =>
            WithSession(
                new DelegateDbConnectionFactory(name, cancellationToken =>
                    open(ConnectionString(provider, name), cancellationToken)),
                options));

        return services;
    }

    /// <summary>
    /// Registers a named connection with a connection string given here rather than read from
    /// configuration.
    /// </summary>
    /// <remarks>
    /// For a database whose address is only known at run time — a throwaway container in a test, or
    /// a tenant's database chosen while the application is running.
    /// </remarks>
    public static IServiceCollection AddDbConnection(
        this IServiceCollection services,
        string name,
        string connectionString,
        Func<string, CancellationToken, ValueTask<DbConnection>> open,
        DbConnectionOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        ArgumentNullException.ThrowIfNull(open);

        services.AddKeyedSingleton<IDbConnectionFactory>(name, (_, _) =>
            WithSession(
                new DelegateDbConnectionFactory(
                    name,
                    cancellationToken => open(connectionString, cancellationToken)),
                options));

        return services;
    }

    /// <summary>Gets a named connection factory, with a message that says what to do when it is missing.</summary>
    /// <exception cref="InvalidOperationException">Nothing is registered under that name.</exception>
    public static IDbConnectionFactory GetRequiredDbConnectionFactory(this IServiceProvider provider, string name)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        return provider.GetKeyedService<IDbConnectionFactory>(name)
            ?? throw new InvalidOperationException(
                $"No database connection named '{name}' is registered. Call one of the provider " +
                $"packages' registration methods during startup - services.AddSqliteConnection(\"{name}\"), " +
                $"AddSqlServerConnection, AddPostgreSqlConnection, AddMySqlConnection or " +
                $"AddOracleConnection - and put its connection string under ConnectionStrings:{name}.");
    }

    /// <summary>Wraps the factory only when there is something to run, so the common case pays nothing.</summary>
    private static IDbConnectionFactory WithSession(IDbConnectionFactory inner, DbConnectionOptions? options) =>
        options is { SessionStatements.Count: > 0 }
            ? new SessionDbConnectionFactory(inner, options.SessionStatements)
            : inner;

    private static string ConnectionString(IServiceProvider provider, string name)
    {
        if (provider.GetService<IConfiguration>() is not { } configuration)
        {
            throw new InvalidOperationException(
                $"The connection named '{name}' takes its connection string from configuration, but no " +
                "IConfiguration is registered. Either build the application with the generic host, " +
                "which registers one, or register the connection with the overload that takes the " +
                "connection string directly.");
        }

        return configuration.GetConnectionString(name)
            ?? throw new InvalidOperationException(
                $"No connection string named '{name}' was found in configuration. Add it under " +
                $"ConnectionStrings:{name} - in appsettings.json that is a \"{name}\" entry of the " +
                "\"ConnectionStrings\" object.");
    }
}
