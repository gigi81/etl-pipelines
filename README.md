# EtlPipelines

A streaming ETL pipeline abstraction for .NET, built so that extract, transform and load actually
overlap rather than run one after another.

## The shape of it

```csharp
services.AddEtlPipeline("orders", builder => builder
    .From<OrderRow>()               // resolves IDataSource<OrderRow>
    .Where(o => o.Amount > 0)
    .Through<OrderDto>()            // resolves IDataTransform<OrderRow, OrderDto>
    .To());                         // resolves IDataSink<OrderDto>

var result = await factory.Get("orders").RunAsync(cancellationToken);
// result.Value: RowsRead, RowsWritten, RowsFailed, Elapsed, per-stage detail
```

Each step re-types the builder, so a step whose input does not match the previous step's output is a
compile error rather than a run-time surprise.

## Three ports

| Port | Contract |
|---|---|
| `IDataSource<TRow>` | `ReadAsync(Memory<TRow> buffer, ct)` — fills the buffer, returns the count, `0` means end of data |
| `IDataTransform<TIn,TOut>` | `TransformAsync(input, output, ct)` — returns rows *consumed* and rows *produced* |
| `IDataSink<TRow>` | `WriteAsync(ReadOnlyMemory<TRow> batch, ct)` |

The source contract mirrors `Stream.ReadAsync`, so it is already familiar and needs no `HasMoreData`
flag with its undefined states.

Reporting consumed and produced separately is what lets one contract carry every cardinality — 1:1
maps, filters, within-batch reduction, and 1:many expansion that overflows the output buffer. The
runtime re-offers whatever was not consumed.

## Writing a transform

You rarely implement `IDataTransform` yourself. Write per-row code and the framework supplies the
batching loop:

```csharp
public sealed class ToCents : RowTransform<OrderRow, OrderDto>
{
    protected override ErrorOr<OrderDto> Transform(in OrderRow row) =>
        row.Amount < 0
            ? Error.Validation("order.negative", $"Order {row.Id} is negative.")
            : new OrderDto(row.Id, row.Customer, row.Amount * 100);
}
```

`Transform` is synchronous on purpose: most transforms are pure CPU, and an async state machine per
row costs more than the work. Use `SelectAsync` when a step genuinely awaits something.

## Aggregation

Aggregation spans batch boundaries — a `GROUP BY` over millions of rows arriving in ten-thousand-row
chunks accumulates through every batch and emits only after the last one. That is what
`IDrainable<TOut>` is for: after input ends the runtime calls `DrainAsync` until it returns zero,
deliberately the same contract as reading from a source.

```csharp
.GroupBy(
    s => s.Region,
    _ => 0,
    (total, s) => total + s.Amount,
    (region, total) => new RegionTotal(region, total),
    inputIsSortedByKey: true)
```

`inputIsSortedByKey` is the performance lever. Left `false`, every key is hashed and the stage is
**fully blocking**: nothing reaches the sink until all input has arrived, and memory is O(distinct
keys). Set `true` — usually just an `ORDER BY` away in the source query — and each group is emitted
as the key changes, so memory is O(one group) and rows keep flowing.

For very large sorts and aggregations, doing the work in the source database beats pulling rows out
to aggregate in process. This is a pipeline, not a query engine.

## Bad rows

```csharp
.WithOptions(o =>
{
    o.OnRowError = RowErrorAction.DeadLetter;   // or Fail (default) / Skip
    o.MaxRowErrors = 1000;                      // 0 means unlimited
})
```

Rejected rows go to a registered `IDeadLetterSink<TRow>`. Expected data-level failures return
`ErrorOr`; genuine infrastructure faults stay exceptions and are caught at the stage boundary.

## Performance notes

- Bounded channels between stages give **back-pressure**: a slow sink throttles the source instead
  of letting batches pile up. Capacity above one is what makes the stages overlap.
- Batch buffers come from `ArrayPool<T>`, so steady-state throughput does not churn the heap. The
  ownership rule that follows: **a sink must not retain the memory it is given past `WriteAsync`**,
  because the buffer is recycled immediately. Copy out anything that must outlive the call.
- When `TRow` is a class, pooling the array still leaves one object allocation per row. Struct rows
  or reusable row instances are the escape hatch.
- `WithParallelism(n)` applies to the preceding transform. It does not preserve order, and it is
  rejected for stateful transforms — parallel workers would each drain a separate partial aggregate.

## Opt-in capabilities

The runtime probes for these rather than baking them into every contract, so a stage stays a
single-method interface:

- `IAsyncInitializable` — async setup before rows flow.
- `IAsyncCompletable` — flush and commit, called once, **only on success**. Distinct from
  `DisposeAsync`, which also runs on the failure path and cannot report an error.

## Observability

Traces and metrics are published through `ActivitySource` and `Meter` named `EtlPipelines`, so any
OpenTelemetry exporter picks them up without this library depending on one.
