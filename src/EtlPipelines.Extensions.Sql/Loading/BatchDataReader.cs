using System.Collections;
using System.Data.Common;

namespace EtlPipelines.Sql.Loading;

/// <summary>
/// Presents one batch of rows as a forward-only <see cref="DbDataReader"/>.
/// </summary>
/// <remarks>
/// This is what lets <see cref="SqlSink{TRow}"/> hand a batch to a provider's bulk loader without
/// copying it into a DataTable first: SqlBulkCopy and MySqlBulkCopy both consume a reader directly,
/// so the rows go from the pooled batch to the wire with nothing materialised in between.
/// </remarks>
/// <typeparam name="TRow">The row type being read.</typeparam>
internal sealed class BatchDataReader<TRow> : DbDataReader
{
    private readonly ReadOnlyMemory<TRow> _batch;
    private readonly string[] _columns;
    private readonly Func<TRow, object?>[] _readers;

    private int _position = -1;

    public BatchDataReader(ReadOnlyMemory<TRow> batch, string[] columns, Func<TRow, object?>[] readers)
    {
        _batch = batch;
        _columns = columns;
        _readers = readers;
    }

    public override int FieldCount => _columns.Length;

    public override bool HasRows => _batch.Length > 0;

    public override bool IsClosed => _position >= _batch.Length;

    public override int Depth => 0;

    public override int RecordsAffected => -1;

    public override object this[int ordinal] => GetValue(ordinal);

    public override object this[string name] => GetValue(GetOrdinal(name));

    public override bool Read()
    {
        if (_position + 1 >= _batch.Length)
        {
            _position = _batch.Length;
            return false;
        }

        _position++;
        return true;
    }

    public override Task<bool> ReadAsync(CancellationToken cancellationToken) =>
        Task.FromResult(Read());

    public override object GetValue(int ordinal) =>
        _readers[ordinal](_batch.Span[_position]) ?? DBNull.Value;

    public override int GetValues(object[] values)
    {
        var count = Math.Min(values.Length, _columns.Length);
        for (var i = 0; i < count; i++)
        {
            values[i] = GetValue(i);
        }

        return count;
    }

    public override bool IsDBNull(int ordinal) => GetValue(ordinal) is DBNull;

    public override string GetName(int ordinal) => _columns[ordinal];

    public override int GetOrdinal(string name)
    {
        var index = Array.FindIndex(_columns, c => string.Equals(c, name, StringComparison.OrdinalIgnoreCase));
        return index >= 0
            ? index
            : throw new IndexOutOfRangeException($"There is no column named '{name}' in this batch.");
    }

    public override Type GetFieldType(int ordinal)
    {
        // The batch is the only source of truth for a column's type, and a null in the first row says
        // nothing about it, so fall back to object rather than guessing.
        for (var i = 0; i < _batch.Length; i++)
        {
            var value = _readers[ordinal](_batch.Span[i]);
            if (value is not null and not DBNull)
            {
                return value.GetType();
            }
        }

        return typeof(object);
    }

    public override string GetDataTypeName(int ordinal) => GetFieldType(ordinal).Name;

    public override bool GetBoolean(int ordinal) => (bool)GetValue(ordinal);

    public override byte GetByte(int ordinal) => (byte)GetValue(ordinal);

    public override char GetChar(int ordinal) => (char)GetValue(ordinal);

    public override DateTime GetDateTime(int ordinal) => (DateTime)GetValue(ordinal);

    public override decimal GetDecimal(int ordinal) => (decimal)GetValue(ordinal);

    public override double GetDouble(int ordinal) => (double)GetValue(ordinal);

    public override float GetFloat(int ordinal) => (float)GetValue(ordinal);

    public override Guid GetGuid(int ordinal) => (Guid)GetValue(ordinal);

    public override short GetInt16(int ordinal) => (short)GetValue(ordinal);

    public override int GetInt32(int ordinal) => (int)GetValue(ordinal);

    public override long GetInt64(int ordinal) => (long)GetValue(ordinal);

    public override string GetString(int ordinal) => (string)GetValue(ordinal);

    public override long GetBytes(int ordinal, long dataOffset, byte[]? buffer, int bufferOffset, int length) =>
        Copy((byte[])GetValue(ordinal), dataOffset, buffer, bufferOffset, length);

    public override long GetChars(int ordinal, long dataOffset, char[]? buffer, int bufferOffset, int length) =>
        Copy(((string)GetValue(ordinal)).ToCharArray(), dataOffset, buffer, bufferOffset, length);

    public override bool NextResult() => false;

    public override IEnumerator GetEnumerator()
    {
        while (Read())
        {
            var values = new object[FieldCount];
            GetValues(values);
            yield return values;
        }
    }

    private static long Copy<T>(T[] source, long dataOffset, T[]? buffer, int bufferOffset, int length)
    {
        if (buffer is null)
        {
            return source.Length;
        }

        var available = (int)Math.Min(source.Length - dataOffset, length);
        Array.Copy(source, dataOffset, buffer, bufferOffset, available);
        return available;
    }
}
