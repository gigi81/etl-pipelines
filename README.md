# EtlPipelines

[![NuGet Version](https://img.shields.io/nuget/v/EtlPipelines.Core)](https://www.nuget.org/packages/EtlPipelines.Core)
[![GitHub Actions Workflow Status](https://img.shields.io/github/actions/workflow/status/gigi81/etl-pipelines/ci.yml)](https://github.com/gigi81/etl-pipelines/actions)
[![codecov](https://codecov.io/github/gigi81/etl-pipelines/graph/badge.svg?token=C5BRFOYW9G)](https://codecov.io/github/gigi81/etl-pipelines)

A streaming ETL pipeline abstraction for .NET, built so that extract, transform and load actually
overlap rather than run one after another.

## Install

| Package | For |
|---|---|
| `EtlPipelines.Core` | The runtime and the builder. Start here. |
| `EtlPipelines.Csv` | CSV source and sink, built on CsvHelper. |
| `EtlPipelines.Excel` | Excel (`.xlsx`) source and sink, built on MiniExcel. |
| `EtlPipelines.Hosting` | Runs your pipelines as a command line application. |
| `EtlPipelines.Sql` | Source and sink for any ADO.NET provider. |
| `EtlPipelines.Sql.Sqlite` .`SqlServer` .`PostgreSql` .`MySql` .`Oracle` | One per engine: the driver, and that engine's bulk-load fast path. |
| `EtlPipelines.Abstractions` | The contracts alone, for a library that defines ports without referencing the engine. Pulled in by the others. |

## Quick start

```csharp
services.AddEtlPipeline("orders", builder => builder
    .From<DownloadOrders, OrderRow>()
    .Where(o => o.Amount > 0)
    .Through<NormalizeOrders, OrderDto>()
    .To<SqlSink>());

var pipeline = provider.GetRequiredEtlPipeline("orders");
var result = await pipeline.RunAsync(cancellationToken);
// result.Value: RowsRead, RowsWritten, RowsFailed, Elapsed, per-stage detail
```

That is the whole registration. Naming the port types in the pipeline declaration *is* how they are
registered — there is no separate pass adding `IDataSource<OrderRow>` and friends to the container
and then a second one referring back to them. Constructor dependencies are still injected normally.

Each step re-types the builder, so a step whose input does not match the previous step's output is a
compile error rather than a run-time surprise. `To<SqlSink>()` needs only one type argument because
the row type is already known by then; `From` and `Through` need two, since C# cannot infer a row
type from a port type.

## How components are created

Every component goes into the container as **scoped** and **keyed to the pipeline name**, and
`RunAsync` creates one scope per run.

- **Scoped** means each run resolves its own instances and the scope disposes them all when the run
  ends. That is what stateful components need — a source tracks its read position, an aggregate
  accumulates — and it is why two runs, sequential or concurrent, share nothing.
- **Keyed** means two pipelines can use the same component type without collision. An `orders` and an
  `invoices` pipeline can each have their own `SqlSink` registered against `IDataSink<T>`, configured
  differently, and neither overwrites the other.

The run scope is the single owner: nothing else disposes a component, so there is no double disposal
and no leak.

When something else should own the lifetime — a port configured elsewhere, a shared pool — name only
the row type and the container keeps ownership:

```csharp
services.AddScoped<IDataSource<OrderRow>>(sp => /* ... */);

services.AddEtlPipeline("orders", builder => builder
    .From<OrderRow>()               // resolved from the container
    .Through<NormalizeOrders, OrderDto>()
    .To<SqlSink>());
```

## The three ports

Ports live in `EtlPipelines.Abstractions.Ports`; the lifecycle hooks below in `.Lifecycle`, options
in `.Configuration`, results in `.Execution`.

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

Two capabilities are opt-in, probed for rather than baked into every contract so that a port stays a
single-method interface:

- `IAsyncInitializable` — async setup before rows flow.
- `IAsyncCompletable` — flush and commit, called once and **only on success**. Distinct from
  `DisposeAsync`, which also runs on the failure path and cannot report an error. This split is what
  makes a commit-on-success sink, such as the atomic CSV writer below, possible at all.

## Branching

Send the same rows to several destinations at once — archive the raw record while the same rows carry
on into a transform and a load:

```csharp
services.AddEtlPipeline("orders", builder => builder
    .From<DownloadStage, OrderRow>()
    .Branch(
        b1 => b1.To<ArchiveStage>(),
        b2 => b2.Through<TransformStage, OrderDto>()
                .To<UploadStage>()));
```

Every branch sees every row, and **the source is read once** however many destinations there are —
which is the point, versus running two pipelines over the same query and hoping they agree. Each
branch is a full dataflow: it can filter, transform, and even branch again. Every branch must
terminate in `To(...)` or a nested `Branch(...)`, and `Branch` ends the dataflow, so the builder
returns to adding stages afterwards.

Three things worth knowing:

- **Rows are shared, not copied.** Each branch gets its own batch buffer — pooling requires that — but
  the row objects inside are the same instances. A branch that mutates a row changes what its
  siblings see. Treat rows as read-only once they enter a branch; immutable row types (records) make
  this a non-issue. This matches Spark, SSIS Multicast and TPL Dataflow's `BroadcastBlock`.
- **The slowest branch governs.** Rows reach every branch before the next batch is taken, so a branch
  writing to a slow endpoint throttles the others. That is deliberate — the alternative is unbounded
  buffering for whichever branch runs ahead — but it means a slow destination costs you everywhere.
- **A fan-out is still one stage.** It stays a single entry in `PipelineResult.Stages`, with `RowsOut`
  summed across destinations: two branches over 1,000 rows report 1,000 in and 2,000 out.

## CSV files

`EtlPipelines.Csv` is a separate package, so the core runtime takes no CsvHelper dependency.

```csharp
builder.FromCsv<Order>(fileSystem.FileInfo.New("orders.csv"))
       .Select(o => new OrderDto(o.Id, o.Customer, o.Amount * 100))
       .ToCsv(fileSystem.FileInfo.New("out.csv"));
```

Files are named as `IFileInfo` (`System.IO.Abstractions`), not as paths. An `IFileInfo` already
carries the filesystem it belongs to, so the ports never have to be told which one to use. Hand them
`new MockFileSystem().FileInfo.New("orders.csv")` and the entire connector runs in memory.

**The sink writes atomically.** Rows stream into a temporary file beside the target, which is renamed
into place on success. So the target path either does not exist or holds a whole file, and a
downstream job can never pick up a truncated one. A failed run leaves the temp file for inspection
and does not touch a previous good target. Set `WriteAtomically = false` if something needs to watch
the file grow.

**Malformed rows are skipped, not fatal.** One unparseable row costing a ten-million-row load is the
classic CSV complaint, so `CsvSource` skips bad rows, counts them on `MalformedRows`, and hands their
raw text to a registered `IDeadLetterSink<string>` so nothing is lost. Set `SkipMalformedRows = false`
to stop on the first one instead.

> **Known limitation.** Those skips do **not** appear in `PipelineResult.RowsFailed` and do not count
> against `MaxRowErrors`. A transform can reject one row — `TransformResult.RejectedRow` carries it
> into the row-error policy — but `IDataSource.ReadAsync` returns only a count, with no channel for a
> rejected row, so a source cannot reach that machinery. Check `MalformedRows` and the dead-letter
> sink. Closing the gap means giving `IDataSource` a rejection channel mirroring the transform side.

**Culture defaults to invariant**, and that is a correctness decision rather than a preference. Under
a machine-local culture a decimal written as `1.5` reads back as `1,5` on a comma-separator host — and
with a comma delimiter it splits into two fields, shifting every column after it. A data file must
mean the same thing wherever it is processed.

Both option types also expose `HasHeaderRecord`, `Delimiter`, `Encoding`, a `Configure` escape hatch
for the full `CsvConfiguration`, and `ConfigureContext` for registering class maps.

## Excel files

`EtlPipelines.Excel` reads and writes `.xlsx` worksheets through
[MiniExcel](https://github.com/mini-software/MiniExcel) (Apache-2.0). It is a separate package, and it
takes `MiniExcel.OpenXml` rather than the `MiniExcel` meta-package — that one also pulls in a second
CSV implementation, next to the CsvHelper-based one this repo already ships.

```csharp
builder.FromExcel<Order>(fileSystem.FileInfo.New("orders.xlsx"))
       .Select(o => new OrderDto(o.Id, o.Customer, o.Amount * 100))
       .ToExcel(fileSystem.FileInfo.New("out.xlsx"));
```

Files are named as `IFileInfo`, sheets are chosen with `SheetName`, and everything the
[CSV section](#csv-files) says about the atomic write and about malformed rows applies here too,
including the same known limitation about `RowsFailed`. Two things differ.

**The row type needs a parameterless constructor and settable properties.** Columns are matched to it
by name, case-insensitively, and MiniExcel's own `[MiniExcelColumnName]` and `[MiniExcelIgnore]`
attributes are honoured. A positional `record` will not work here, unlike on the CSV side — the
worksheet reader has to construct the row before it can fill it. A column the row type does not
mention is ignored, so a sheet may carry more than one pipeline cares about.

**The atomic write matters more.** An `.xlsx` is a zip archive whose central directory is written
last, so a run that dies part-way leaves not a short-but-readable file but one Excel refuses to open
at all. Rows stream into a temporary file that is renamed into place only on success, so the target
either does not exist or opens.

### Several sources, one workbook

A workbook holds many sheets, and filling them from different sources needs no new builder concept —
`To(...)` hands the pipeline builder back, so each source gets its own stage:

```csharp
var report = fileSystem.FileInfo.New("report.xlsx");

services.AddEtlPipeline("report", b => b
    .FromSql(open, "SELECT ... FROM orders", MapOrder).ToExcelSheet(report, "Orders")
    .FromSql(open, "SELECT ... FROM customers", MapCustomer).ToExcelSheet(report, "Customers")
    .FromCsv<Region>(regionsFile).ToExcelSheet(report, "Regions"));
```

Stages run one after another, so the sheets are written in the order declared, and the workbook is
renamed into place only after the last one — the target either holds every sheet or does not exist.
Row types can differ per sheet, and the sources can be anything, including the SQL and CSV connectors
above. Inserting a sheet costs about the same whether it is the second or the tenth: ten sheets of
50,000 rows measured flat at roughly 115 ms each after the first.

Pass the **same `IFileInfo`** to every `ToExcelSheet` for a workbook — that object is how the sheets
find each other. Sheets of one workbook must also not be split across a `Branch`, because branches
run at the same time and two writers interleaving into one zip archive produce a file that will not
open; starting a second sheet while one is open throws rather than producing it.

Neither end materialises the workbook. The source pulls rows from MiniExcel's asynchronous stream as
the pipeline consumes them, and the sink hands rows to the writer through a bounded channel — so the
sheet is written as it arrives rather than being collected first, and `BufferedRows` is the
back-pressure knob between the pipeline and the disk.

> **Preview dependency.** MiniExcel 2.x is still a preview release, and its API changed substantially
> from 1.x. Pinned deliberately: 2.x is the line with `IAsyncEnumerable` streaming on both sides,
> which is what makes a non-materialising connector possible.

## SQL databases

`EtlPipelines.Sql` works against any ADO.NET provider, and a package per engine adds that engine's
driver and bulk-load path. Reference the one you need — a SQLite job never pulls in the Oracle driver.

Name a database once, and refer to it by that name from then on:

```csharp
builder.Services.AddSqliteConnection("sales");        // ConnectionStrings:sales
builder.Services.AddSqlServerConnection("warehouse"); // ConnectionStrings:warehouse

services.AddEtlPipeline("orders", builder => builder
    .FromSql<Order>("sales", "SELECT Id, Customer, Amount FROM orders")
    .Select(o => o with { Amount = o.Amount * 100 })
    .ToSqlTable<Order>("warehouse", "orders_cents"));
```

The connection string comes from `IConfiguration.GetConnectionString(name)` — the ordinary
`ConnectionStrings` section of `appsettings.json`, the environment, or anywhere else configuration
comes from. Registration puts a **factory** in the container, not a connection: a source holds an open
reader for the whole run and a sink holds an open transaction, so they cannot share one, and each opens
and disposes its own. Two names mean two databases, which is how one pipeline reads from one engine and
writes to another.

There is an overload taking the connection string directly, for a database whose address is only known
at run time, and one taking a `Func<CancellationToken, ValueTask<DbConnection>>` for anything stranger.

**No mapping delegate.** Columns are matched to properties by name, by a parser Dapper compiles once
the query's columns are known — not reflection per row. Pass a delegate to `FromSql` when you want the
mapping under your own control.

The row type wants a **parameterless constructor and settable properties**. A positional record works
only when its constructor parameters match the types the *provider* reports, which is not the same as
the types the record declares: SQLite reports a `DECIMAL` column as `Double`, so
`record Order(long Id, string Customer, decimal Amount)` has no constructor the mapping can use. A
record with settable properties converts per column and is fine.

**A run is one transaction, committed only when it succeeds.** This is the database counterpart of
the file connectors writing through a temporary file: a run that fails part-way leaves the table as
it was, rather than holding some fraction of the rows for a downstream job to read as though the load
had finished. Set `UseTransaction = false` for a load large enough to strain the server's log, and
accept partial writes in exchange.

**Bulk loading comes with the connection.** `AddSqlServerConnection("warehouse")` registers that
engine's `IBulkLoader` under the same name, and the sink writing to `"warehouse"` picks it up:

| Package | Fast path |
|---|---|
| `Sqlite` | none — a prepared INSERT reused inside one transaction, which *is* the fast path for SQLite |
| `SqlServer` | `SqlBulkCopy` |
| `PostgreSql` | binary `COPY` |
| `MySql` | `MySqlBulkCopy`, serving MariaDB too |
| `Oracle` | array binding — one INSERT carrying the whole batch |

Batches reach the loader as a `DbDataReader` over the pooled buffer, so nothing is copied into a
`DataTable` on the way. Set `UseBulkLoader = false` to force the portable INSERT path; bulk loaders
are faster but do not all behave identically to an INSERT, and some bypass triggers.

The loader is keyed to the connection name, so a pipeline writing to two engines gets each one's own
fast path — there is no single registration for the second one to lose.

Two things differ between engines and will bite quietly:

- **Identifiers go in exactly as the row type spells them.** Nothing is quoted, so PostgreSQL folds
  `Id` to `id` and Oracle to `ID`, as they would for any statement. Create tables unquoted and it
  matches; create them quoted and mixed-case and it will not.
- **MySQL and MariaDB need `AllowLoadLocalInfile=true`** on the connection string and `local_infile`
  on the server, because `MySqlBulkCopy` is built on `LOAD DATA LOCAL INFILE`. Oracle binds with `:`
  rather than `@`, which only matters on the INSERT path — see `SqlSinkOptions.ParameterPrefix`.

### Reading from any reader

`DataReaderSource<TRow>` sits underneath `FromSql` and takes any `IDataReader` directly, which is
what to use for a provider with no package here, or for a reader you already have:

```csharp
new DataReaderSource<OrderRow>(async ct => await command.ExecuteReaderAsync(ct))
```

Rows are always **materialised out of the reader**, whether by the automatic mapping or by a delegate
you pass. A reader is a cursor positioned on the current row, not a value — it is the same object every
iteration and its contents change as it advances — so a batch built from the reader itself would hold N
references to one object showing the last row read.

One thing to know about SQLite specifically: it types a *value*, not a column. In a `NUMERIC` column
`3.0` is stored as an integer while `4.5` is stored as a float, so the column reports a different type
from one row to the next. The source rebuilds its mapping when it meets a row the current one cannot
read, which costs nothing on an engine whose column types are stable — every other one here — and one
rebuild per change on a SQLite column that genuinely mixes them.

The reader is opened in `InitializeAsync`, not in the constructor, so a registered source holds no
open cursor between runs — each run opens its own and the run's scope closes it. Where the provider's
reader derives from `DbDataReader` (all of them do), `ReadAsync` is used rather than the blocking
`IDataReader.Read`; the synchronous fallback keeps in-memory and legacy readers working.

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
row costs more than the work. Use `SelectAsync` when a step genuinely awaits something. `RowFilter<TRow>`
is the same idea for a predicate.

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

## Performance

- Bounded channels between stages give **back-pressure**: a slow sink throttles the source instead
  of letting batches pile up. Capacity above one is what makes the stages overlap.
- Batch buffers come from `ArrayPool<T>`, so steady-state throughput does not churn the heap. The
  ownership rule that follows: **a sink must not retain the memory it is given past `WriteAsync`**,
  because the buffer is recycled immediately. Copy out anything that must outlive the call.
- When `TRow` is a class, pooling the array still leaves one object allocation per row. Struct rows
  or reusable row instances are the escape hatch.
- `WithParallelism(n)` does not preserve order, and it is rejected for stateful transforms — parallel
  workers would each drain a separate partial aggregate. Above `WithParallelism(1)` all workers share
  the one instance the run resolved, so a parallel transform must be thread-safe.
- `BatchSize` (default 10,000) and `ChannelCapacity` (default 4) are the two knobs on `PipelineOptions`
  that trade memory for throughput.

## Samples

Runnable programs in [`samples/`](samples):

| Sample | Shows |
|---|---|
| `Samples.CsvToExcel` | CSV in, filter and reshape, workbook out — the ordinary job |
| `Samples.SqlToWorkbook` | three queries becoming three sheets of one workbook |
| `Samples.ExcelToSql` | a hand-filled spreadsheet loaded into a table, bad rows set aside |
| `Samples.CsvToDatabase` | one pipeline, five engines — only the registered connection changes |
| `Samples.Branching` | archiving the raw rows while the same pass builds a report |

Each is a command line application. The verb named after the pipeline puts an input file in place and
then runs it; `--work-dir` says where, and defaults to a new directory under the temp path:

```bash
dotnet run --project samples/Samples.SqlToWorkbook -- report --work-dir ./out
```

`run` and `list` come from `EtlPipelines.Hosting` and are not written by the samples at all — see
below. Each sample is also an integration test: `tests/EtlPipelines.Samples.Tests` runs the pipelines
through their own registration and checks what they left behind, then runs each one again through its
command line to prove the wiring holds. `Samples.CsvToDatabase` is run again against real SQL Server,
PostgreSQL, MySQL and Oracle containers — the same registration, not a copy of it. Samples are
documentation that nothing compiles against, so without that they rot quietly.

## Running as a command line application

`EtlPipelines.Hosting` turns an application that registers pipelines into one you can run:

```csharp
return await new EtlPipelinesHost("Loads the nightly orders file.")
    .ConfigureServices(services => services.AddEtlPipeline("orders", builder => ...))
    .RunAsync(args);
```

That is the whole program. It builds a [.NET generic host](https://learn.microsoft.com/dotnet/core/extensions/generic-host)
— configuration, logging and DI as usual, plus an `IFileSystem` for the file connectors to name their
files against — and gives you two verbs:

```bash
myapp list          # the pipelines this application registered
myapp run orders    # run one, exit 0 on success and 1 on failure
```

Runs report themselves through `Microsoft.Extensions.Logging`. The runtime's own traces are published
to an `ActivitySource` rather than to a logger, so turning the level up surfaces per-stage timings
without an exporter:

```bash
myapp run orders --Logging:LogLevel:Default=Debug
```

Commands of your own go in alongside the built-in ones — the command line is
[Albatross.CommandLine](https://rushuiguan.github.io/commandline/) over `System.CommandLine`, so a
verb is a parameters class and a handler, and the handler is resolved from the container:

```csharp
[Verb<LoadHandler>("load", Description = "Fetches today's file, then loads it.")]
public class LoadParams { }

public class LoadHandler : BaseHandler<LoadParams>
{
    private readonly Downloader _downloader;
    private readonly PipelineRunner _runner;

    public LoadHandler(ParseResult result, LoadParams parameters, Downloader downloader, PipelineRunner runner)
        : base(result, parameters)
    {
        _downloader = downloader;
        _runner = runner;
    }

    public override async Task<int> InvokeAsync(CancellationToken cancellationToken)
    {
        await _downloader.FetchAsync(cancellationToken);
        return await _runner.RunAsync("orders", cancellationToken);
    }
}
```

`PipelineRunner` is the same service the built-in `run` uses. Hand the host the two methods the
source generator wrote into your assembly's `AutoGenerated` namespace and the verb is live:

```csharp
return await new EtlPipelinesHost("Loads the nightly orders file.")
    .AddCommands(host => host.AddCommands())
    .ConfigureServices(services => services.RegisterCommands().AddEtlPipeline("orders", ...))
    .RunAsync(args);
```

## Observability

Traces and metrics are published through `ActivitySource` and `Meter` named `EtlPipelines`, so any
OpenTelemetry exporter picks them up without this library depending on one.
