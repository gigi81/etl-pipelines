namespace EtlPipelines.Core.Runtime;

/// <summary>
/// The service key a pipeline component is registered under.
/// </summary>
/// <remarks>
/// <para>
/// Keying by pipeline name is what lets two pipelines use the same component type without colliding:
/// an <c>orders</c> and an <c>invoices</c> pipeline can each register their own <c>SqlSink</c>
/// against the same <c>IDataSink&lt;T&gt;</c> service type, configured differently, and neither
/// overwrites the other.
/// </para>
/// <para>
/// The ordinal disambiguates within one pipeline, so the same type may appear at several positions —
/// two download steps against different endpoints, say. Role is carried for readable diagnostics
/// only; name and ordinal alone already make the key unique.
/// </para>
/// </remarks>
internal sealed record EtlComponentKey(string Pipeline, int Ordinal, string Role)
{
    public override string ToString() => $"{Pipeline}[{Ordinal}]:{Role}";
}
