using System.Text.RegularExpressions;
using EtlPipelines.Server.Contracts;
using EtlPipelines.Server.Secrets;
using Microsoft.AspNetCore.Http.HttpResults;

namespace EtlPipelines.Server.Api;

/// <summary>
/// The configuration routes of the REST API: the entries (connection strings, credentials) that
/// pipelines read as <c>IConfiguration</c>. They can be listed by key, set and removed - and never
/// read back: a value is a secret, encrypted at rest, and only a running pipeline is handed it.
/// </summary>
internal static partial class ConfigurationEndpoints
{
    /// <summary>Maps the <c>/configuration</c> routes onto <paramref name="api"/>.</summary>
    public static void Map(RouteGroupBuilder api)
    {
        var configuration = api.MapGroup("/configuration");

        configuration.MapGet("/", ListAsync);
        configuration.MapPut("/", SetAsync);
        configuration.MapDelete("/", DeleteAsync);
    }

    // The colon-path an IConfiguration key is: no spaces, no surprises.
    [GeneratedRegex(@"^[A-Za-z0-9_.\-]+(:[A-Za-z0-9_.\-]+)*$")]
    private static partial Regex ValidKey();

    private static async Task<Ok<List<ConfigurationKeyDto>>> ListAsync(SecretsStore secrets, CancellationToken cancellationToken)
    {
        var keys = await secrets.ListKeysAsync(cancellationToken).ConfigureAwait(false);
        return TypedResults.Ok(keys.Select(key => new ConfigurationKeyDto(key.Key, key.UpdatedAt)).ToList());
    }

    private static async Task<Results<NoContent, ProblemHttpResult>> SetAsync(
        ConfigurationEntryDto entry, SecretsStore secrets, CancellationToken cancellationToken)
    {
        var key = entry.Key?.Trim() ?? string.Empty;
        if (key.Length is 0 or > 200 || !ValidKey().IsMatch(key))
        {
            return TypedResults.Problem(
                detail: "A key is a colon-separated path of letters, digits, '.', '-' and '_', such as ConnectionStrings:sales.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        if (string.IsNullOrEmpty(entry.Value))
        {
            return TypedResults.Problem(detail: "A value is required.", statusCode: StatusCodes.Status400BadRequest);
        }

        await secrets.SetAsync(key, entry.Value, cancellationToken).ConfigureAwait(false);
        return TypedResults.NoContent();
    }

    private static async Task<Results<NoContent, NotFound>> DeleteAsync(string key, SecretsStore secrets, CancellationToken cancellationToken) =>
        await secrets.DeleteAsync(key, cancellationToken).ConfigureAwait(false) ? TypedResults.NoContent() : TypedResults.NotFound();
}
