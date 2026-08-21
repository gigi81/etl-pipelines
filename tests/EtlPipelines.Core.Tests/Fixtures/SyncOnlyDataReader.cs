using System.Data;

namespace EtlPipelines.Core.Tests.Fixtures;

/// <summary>
/// A bare <see cref="IDataReader"/> that does not derive from <c>DbDataReader</c>, so it exercises
/// the synchronous fallback in <c>DataReaderSource</c>.
/// </summary>
/// <remarks>
/// Only the members the source actually touches are implemented; the rest of the very wide ADO.NET
/// surface throws, which keeps the fixture honest about what is being tested.
/// </remarks>
public sealed class SyncOnlyDataReader(IReadOnlyList<(int Id, string Name)> rows) : IDataReader
{
    private int _position = -1;

    /// <summary>How many times the synchronous <see cref="Read"/> path was taken.</summary>
    public int SyncReads { get; private set; }

    public bool IsClosed { get; private set; }

    public bool Read()
    {
        SyncReads++;
        return ++_position < rows.Count;
    }

    public int FieldCount => 2;

    public string GetName(int i) => i switch
    {
        0 => "Id",
        1 => "Name",
        _ => throw new IndexOutOfRangeException(nameof(i)),
    };

    public int GetOrdinal(string name) => name switch
    {
        "Id" => 0,
        "Name" => 1,
        _ => throw new IndexOutOfRangeException(nameof(name)),
    };

    public int GetInt32(int i) => i == 0
        ? rows[_position].Id
        : throw new InvalidCastException($"Column {i} is not an int.");

    public string GetString(int i) => i == 1
        ? rows[_position].Name
        : throw new InvalidCastException($"Column {i} is not a string.");

    public object GetValue(int i) => i == 0 ? rows[_position].Id : rows[_position].Name;

    public bool IsDBNull(int i) => false;

    public void Close() => IsClosed = true;

    public void Dispose() => Close();

    // Not exercised by DataReaderSource.
    public object this[int i] => GetValue(i);
    public object this[string name] => GetValue(GetOrdinal(name));
    public int Depth => throw new NotSupportedException();
    public int RecordsAffected => throw new NotSupportedException();
    public DataTable? GetSchemaTable() => throw new NotSupportedException();
    public bool NextResult() => false;
    public bool GetBoolean(int i) => throw new NotSupportedException();
    public byte GetByte(int i) => throw new NotSupportedException();
    public long GetBytes(int i, long o, byte[]? buffer, int bo, int length) => throw new NotSupportedException();
    public char GetChar(int i) => throw new NotSupportedException();
    public long GetChars(int i, long o, char[]? buffer, int bo, int length) => throw new NotSupportedException();
    public IDataReader GetData(int i) => throw new NotSupportedException();
    public string GetDataTypeName(int i) => throw new NotSupportedException();
    public DateTime GetDateTime(int i) => throw new NotSupportedException();
    public decimal GetDecimal(int i) => throw new NotSupportedException();
    public double GetDouble(int i) => throw new NotSupportedException();
    public Type GetFieldType(int i) => throw new NotSupportedException();
    public float GetFloat(int i) => throw new NotSupportedException();
    public Guid GetGuid(int i) => throw new NotSupportedException();
    public short GetInt16(int i) => throw new NotSupportedException();
    public long GetInt64(int i) => throw new NotSupportedException();
    public int GetValues(object[] values) => throw new NotSupportedException();
}
