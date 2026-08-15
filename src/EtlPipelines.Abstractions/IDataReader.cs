namespace EtlPipelines.Abstractions;

public interface IDataReader<TRow> : IAsyncDisposable
{
    Task Initialize();
    
    Task<ErrorOr<int>> ReadBatch(ICollection<TRow> rows);
    
    bool HasMoreData { get; }
}