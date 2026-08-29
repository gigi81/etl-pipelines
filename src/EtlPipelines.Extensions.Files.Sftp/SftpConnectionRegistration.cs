using EtlPipelines.Extensions.Files.Sftp.Configuration;
using EtlPipelines.Extensions.Files.Sftp.Connections;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace EtlPipelines.Extensions.Files.Sftp;

/// <summary>Registers named SFTP connections, and resolves them again when a pipeline is composed.</summary>
/// <remarks>
/// Follows <c>EtlPipelines.Extensions.Sql.ConnectionRegistration</c>'s shape: a connection string is the wrong
/// currency for SFTP (host, port, credentials, host key), so the section is <c>Sftp:&lt;name&gt;</c>
/// rather than <c>ConnectionStrings</c>, but the name still doubles as the configuration key and
/// configuration is still read lazily, the first time a run connects rather than at registration.
/// </remarks>
public static class SftpConnectionRegistration
{
    /// <summary>Registers a named connection whose settings are bound from configuration under <c>Sftp:&lt;name&gt;</c>.</summary>
    public static IServiceCollection AddSftpConnection(
        this IServiceCollection services,
        string name,
        Action<SftpConnectionOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        services.AddKeyedSingleton<ISftpConnectionFactory>(name, (provider, _) =>
            new SftpConnectionFactory(name, () => Bind(provider, name, configure)));

        return services;
    }

    /// <summary>Registers a named connection with a host and user name given here rather than read from configuration.</summary>
    /// <remarks>For a server whose address is only known at run time - a throwaway container in a test.</remarks>
    public static IServiceCollection AddSftpConnection(
        this IServiceCollection services,
        string name,
        string host,
        string userName,
        Action<SftpConnectionOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        ArgumentException.ThrowIfNullOrWhiteSpace(userName);

        services.AddKeyedSingleton<ISftpConnectionFactory>(name, (_, _) =>
            new SftpConnectionFactory(name, () =>
            {
                var options = new SftpConnectionOptions { Host = host, UserName = userName };
                configure?.Invoke(options);
                return options;
            }));

        return services;
    }

    /// <summary>Gets a named connection factory, with a message that says what to do when it is missing.</summary>
    /// <exception cref="InvalidOperationException">Nothing is registered under that name.</exception>
    public static ISftpConnectionFactory GetRequiredSftpConnectionFactory(this IServiceProvider provider, string name)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        return provider.GetKeyedService<ISftpConnectionFactory>(name)
            ?? throw new InvalidOperationException(
                $"No SFTP connection named '{name}' is registered. Call " +
                $"services.AddSftpConnection(\"{name}\") during startup, and put its host and " +
                $"credentials under Sftp:{name}.");
    }

    private static SftpConnectionOptions Bind(IServiceProvider provider, string name, Action<SftpConnectionOptions>? configure)
    {
        var options = new SftpConnectionOptions();

        if (provider.GetService<IConfiguration>() is { } configuration)
        {
            configuration.GetSection($"Sftp:{name}").Bind(options);
        }

        configure?.Invoke(options);
        return options;
    }
}
