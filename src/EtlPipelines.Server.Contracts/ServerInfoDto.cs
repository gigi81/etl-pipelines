namespace EtlPipelines.Server.Contracts;

/// <summary>What <c>GET /api/info</c> says about the server.</summary>
/// <param name="Version">The version it was built as, e.g. <c>0.2.0.5+01d5a8c</c>: the version, the commits since it was set, and the commit.</param>
public sealed record ServerInfoDto(string Version);
