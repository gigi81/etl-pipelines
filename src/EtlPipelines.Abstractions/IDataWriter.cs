namespace EtlPipelines.Abstractions;

public interface IDataWriter<TRow> : IAsyncDisposable
{
    Task Initialize(TRow row);
    
    Task<ErrorOr<int>> WriteBatch(ICollection<TRow> rows);
}