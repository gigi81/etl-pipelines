namespace EtlPipelines.Abstractions.Data;

public interface IDataSource<TRow>
{
    bool Read(out TRow row);
}

public interface IBulkDataSource<TRow>
{
    bool Read(out ICollection<TRow> rows);
}