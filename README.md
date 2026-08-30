# EtlPipelines

[![NuGet Version](https://img.shields.io/nuget/v/EtlPipelines.Core)](https://www.nuget.org/packages/EtlPipelines.Core)
[![GitHub Actions Workflow Status](https://img.shields.io/github/actions/workflow/status/gigi81/etl-pipelines/ci.yml)](https://github.com/gigi81/etl-pipelines/actions)
[![codecov](https://codecov.io/github/gigi81/etl-pipelines/graph/badge.svg?token=C5BRFOYW9G)](https://codecov.io/github/gigi81/etl-pipelines)

A streaming ETL pipeline abstraction for .NET, built so that extract, transform and load actually
overlap rather than run one after another.

## Install

| Package | For |
|---|---|
| `EtlPipelines` | Meta-package: pulls in the runtime, the abstractions and every connector below. The easy way to get everything without naming each package. |
| `EtlPipelines.Core` | The runtime and the builder. Reference this alone to pick connectors one at a time instead. |
| `EtlPipelines.Extensions.Cli` | Runs external commands and PowerShell scripts as pipeline stages, built on CliWrap. |
| `EtlPipelines.Extensions.Csv` | CSV source and sink, built on CsvHelper. |
| `EtlPipelines.Extensions.Excel` | Excel (`.xlsx`) source and sink, built on MiniExcel. |
| `EtlPipelines.Extensions.Files` | Copy, move, compress and extract files as pipeline stages. No third-party dependency. |
| `EtlPipelines.Extensions.Files.Http` | Downloads files over HTTP. |
| `EtlPipelines.Extensions.Files.Sftp` | Uploads and downloads files over SFTP, built on SSH.NET. |
| `EtlPipelines.Extensions.Json` | JSON source and sink - JSON Lines or a single array - built on System.Text.Json. |
| `EtlPipelines.Hosting` | Runs your pipelines as a command line application. |
| `EtlPipelines.Extensions.Sql` | Source and sink for any ADO.NET provider. |
| `EtlPipelines.Extensions.Sql.Sqlite` .`SqlServer` .`PostgreSql` .`MySql` .`Oracle` | One per engine: the driver, and that engine's bulk-load fast path. |
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

## Running stages in parallel

`Branch` fans *one already-flowing stream* out to several destinations. `Parallel` is its coarse-stage
counterpart: several **independent** chains, each reading and writing something of its own, run at the
same time instead of one after another:

```csharp
services.AddEtlPipeline("archive", builder => builder
    .ExtractArchive(archive, extracted)
    .Parallel(
        b => b.TruncateTable("db", "customers").FromCsv<Row>(extracted.File("customers.csv")).ToSqlTable("db", "customers"),
        b => b.TruncateTable("db", "products").FromCsv<Row>(extracted.File("products.csv")).ToSqlTable("db", "products"),
        b => b.TruncateTable("db", "regions").FromCsv<Row>(extracted.File("regions.csv")).ToSqlTable("db", "regions")));
```

Five independent extract-and-load chains against five different tables — the motivating case — have no
reason to make the fifth wait on the first four just because they happen to be declared in the same
pipeline. Each branch is composed exactly like the main chain, including a dataflow of its own, and
runs its own stages in order; it is the branches that overlap with each other, not the stages inside
one of them.

Two things worth knowing:

- **One branch failing does not cancel the others.** Every branch that is already running keeps going
  to completion — cutting one off mid-write would trade a slow run for a half-written table, which is
  worse. Once every branch has finished, the block fails if any of them did, and reports the first
  error; whatever the other branches moved is still counted, the same way a failed dataflow stage still
  reports what it moved before it died.
- **The block is still one entry in `PipelineResult.Stages`**, named `Parallel(N)`, with row counts
  summed across branches and elapsed time the block's own wall clock rather than the sum of its
  branches. Each branch's own stages are still traced individually — nothing is lost, only rolled up
  in the run's own report.

## CSV files

`EtlPipelines.Extensions.Csv` is a separate package, so the core runtime takes no CsvHelper dependency.

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

## JSON files

`EtlPipelines.Extensions.Json` reads and writes JSON files - either JSON Lines (NDJSON, one record per
line) or a single JSON array - through `System.Text.Json`. It is a separate package with no
third-party dependency at all: System.Text.Json ships in the shared framework.

```csharp
builder.FromJson<Order>(fileSystem.FileInfo.New("orders.json"))
       .Select(o => new OrderDto(o.Id, o.Customer, o.Amount * 100))
       .ToJson(fileSystem.FileInfo.New("out.json"));
```

Files are named as `IFileInfo`, and everything the [CSV section](#csv-files) says about files as
`IFileInfo` and about the atomic write applies here too - for `JsonFormat.Array`, atomicity also keeps
a downstream reader from ever seeing a file missing its closing `]`. Two things differ.

**The format is chosen with `Format`, and it changes how a malformed row is handled.** JSON Lines (the
default) treats each line as a recovery boundary, exactly like a CSV row: `JsonSource` skips a line it
cannot parse, counts it on `MalformedRows`, and hands the raw text to a registered
`IDeadLetterSink<string>`, subject to the same `RowsFailed` limitation the CSV section calls out.
`Format = JsonFormat.Array` has no such boundary - there is no way to skip past one broken element and
keep parsing the rest of the array - so a malformed element always fails the run there, whatever
`SkipMalformedRows` says.

**Property naming defaults to camelCase**, from `JsonSerializerDefaults.Web`, because that is what a
JSON file produced outside .NET almost always looks like; matching on read is case-insensitive, so a
file this library wrote itself round-trips regardless of casing. `SerializerOptions` is a plain,
mutable `JsonSerializerOptions` instance seeded from those defaults - deliberately not the shared
`JsonSerializerOptions.Web` singleton, which comes back already read-only - so it can be reconfigured
freely: naming policy, converters, indentation.

## Excel files

`EtlPipelines.Extensions.Excel` reads and writes `.xlsx` worksheets through
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

## Files

Every real ETL job starts before the first row: a vendor drops a `.tar.gz` on an SFTP server, a
partner publishes a CSV at a URL, a finance team writes to a network share. `EtlPipelines.Extensions.Files`
covers copy, move, compress and extract with no third-party dependency — zip, tar and gzip are all in
the .NET shared framework. `EtlPipelines.Extensions.Files.Http` and `EtlPipelines.Extensions.Files.Sftp` are separate
packages for the same reason the SQL provider packages are: reference the one you need.

```csharp
services.AddHttpClient("vendor");
services.AddSftpConnection("vendor", o => o.HostKeyFingerprints.Add("SHA256:..."));

services.AddEtlPipeline("nightly", b => b
    .DownloadFromSftp("vendor", "/out", "*.tar.gz", inbox)
    .ExtractArchive(inbox.File("orders.tar.gz"), extracted)
    .FromCsv<Order>(extracted.File("orders.csv"))
    .ToSqlTable("warehouse", "staging.orders"));
```

These are coarse stages, not ports — `AddStage`-level steps like `RunSql`, not a source or a sink in a
dataflow — so they chain on `IPipelineBuilder` the same way `RunSqlScript` does, and files are named
as `IFileInfo`/`IDirectoryInfo` for the same reason every other connector names them that way: the
file already carries the filesystem it belongs to, so a test passes `mockFileSystem.FileInfo.New(...)`
and everything downstream follows.

**A file stage is not transactional.** Copy, move, download and extract all fail fast and keep what
already succeeded — a multi-file stage that fails on file 3 of 5 leaves the first two written, not
rolled back, because deleting them on the failure path would be a second destructive act on top of
the first. A move deletes its source only once the destination is complete, so a failed multi-file
move can be rerun.

**Writes are atomic by default.** Every file a stage produces streams into a temporary sibling of its
target and is renamed into place only once it is complete — the same pattern `CsvSink` and `ExcelSink`
use, generalised. `OverwritePolicy` controls what happens when a target already exists: `Overwrite`
(the default), `Skip` to resume a drain without redoing work, or `Fail` to refuse the whole batch
before a single byte is written.

### Selecting files

A stage works on one named file, or every file a pattern matches in a directory:

```csharp
b.CopyFile(inbox.File("orders.csv"), archive.File("orders.csv"));
b.CopyFiles(inbox, "*.csv", archive);
```

Selection is resolved when the stage **runs**, not when the pipeline is composed, so a pattern can
match files an earlier stage in the same run just produced. Matches are sorted — by name, by
`LastWriteTime` or by `Length`, ascending or descending — so a rerun does the same work in the same
order and "file 3 of 5" in an error message is a reproducible address. Matching nothing is not an
error by default; `MinimumFiles` opts a stage into treating an empty inbox as one.

> **Known limitation.** The pattern language is `*` and `?` only, plus a `Recursive` flag for
> subdirectories — there is no `**` and no brace sets. That needs
> `Microsoft.Extensions.FileSystemGlobbing`, a dependency this package deliberately does not take.

### Copying and moving

```csharp
b.CopyFiles(inbox, "*.csv", archive, o => o.Overwrite = OverwritePolicy.Skip);
b.MoveFiles(inbox, "*.csv", processed);
```

A same-volume move is already an atomic rename, so it is not wrapped in a copy by default — that
would add a full data copy to the operation people choose because it is cheap. Set
`FileMoveOptions.WriteAtomically = true` for a destination on a network share (see below), where
`File.Move` falls back to copy-then-delete regardless and this buys back the atomicity a plain rename
would have given it on one volume.

### Archives

```csharp
b.CompressFiles(inbox, "*.csv", archive.File("orders.tar.gz"));
b.ExtractArchive(archive.File("orders.tar.gz"), extracted);
```

Zip, tar, gzip and tar.gz, detected from the archive's extension or set explicitly with
`ExtractOptions.Format`/`CompressOptions.Format`. Extraction runs entirely through `IFileInfo.OpenRead`
and `IFileSystem`, never through `ZipFile`/`TarFile`, which are path-based and would silently reach
past whatever filesystem the pipeline is composed against.

**Every entry is checked before any entry is written.** An archive entry named `../../etc/passwd`, or
a tar symbolic link aimed outside the extraction directory, is the "Zip Slip" vulnerability — present
in every format here, because none of them constrain what an entry may call itself. A validation pass
over the whole archive runs first; an unsafe entry refuses the extraction rather than writing
everything before it and stopping partway. `ExtractOptions.MaxEntries` (10,000 by default) and
`MaxTotalBytes` (unlimited by default) are a zip-bomb guard on top, belt-and-braces rather than a
substitute for trusting the source of the archive.

### Downloading over HTTP

```csharp
services.AddHttpClient("vendor", c => c.BaseAddress = new Uri("https://vendor.example/"));

b.DownloadFromHttp("vendor", new Uri("https://vendor.example/orders.csv"), inbox.File("orders.csv"));
```

The named client comes from `IHttpClientFactory` — headers, auth, a base address and any retry policy
belong on `services.AddHttpClient("vendor", ...)`, which this package takes no dependency on and adds
nothing on top of. Downloads stream with `HttpCompletionOption.ResponseHeadersRead`; the default
buffers the whole body into memory first, which for a large file is the problem streaming exists to
avoid. A failed status is reported with the URL and the reason phrase, not `EnsureSuccessStatusCode`'s
exception, which loses both.

### SFTP

```csharp
services.AddSftpConnection("vendor", o =>
{
    o.HostKeyFingerprints.Add("SHA256:47DEQpj8HBSa+/TImW+5JCeuQeRkm5NMpJWZG3hSuFU");
    o.PrivateKeyPath = "/etc/etl/vendor.key";
});

b.DownloadFromSftp("vendor", "/out", "*.csv", inbox);
b.UploadToSftp("vendor", outbox, "*.csv", "/in");
```

Settings live under `Sftp:<name>` rather than `ConnectionStrings`, read lazily the first time a run
connects — the same lazy-configuration convention `AddSqliteConnection` and friends follow. The test
seam is SSH.NET's own `ISftpClient`, not a wrapper of it.

**Host keys are verified by default, not trusted.** With no fingerprint configured and
`AcceptAnyHostKey` left `false`, a connection is refused rather than silently accepted — accepting any
host key by default would make every SFTP pipeline here trivially machine-in-the-middleable. The
refusal names exactly what was presented:

```
The SFTP connection 'vendor' has no expected host key. sftp.vendor.com:22 presented
'SHA256:47DEQpj8HBSa+/TImW+5JCeuQeRkm5NMpJWZG3hSuFU'. If that is the right server, add it under
Sftp:vendor:HostKeyFingerprints:0. To skip the check, set Sftp:vendor:AcceptAnyHostKey to true.
```

Paste that fingerprint into configuration, or compare it against `ssh-keygen -lf` on the server's own
key — both the bare form SSH.NET reports and the `SHA256:`-prefixed form `ssh-keygen` prints are
accepted. `~/.ssh/known_hosts` is not read; fingerprints come from configuration only.

### Network shares

A UNC path is just a path — `fileSystem.DirectoryInfo.New(@"\\nas\feeds\inbox")` and everything
downstream follows, no different option or code path needed.

**Authentication is the operating system's job, not this library's.** On Windows, run the process as
an account that has rights on the share, or establish the session with `net use` before the process
starts. On Linux and in containers, mount the share. No API here takes a share password, and none
ever will.

**A move to a share is a copy and a delete, not a rename**, whatever `WriteAtomically` says: `File.Move`
falls back to copy-then-delete across volumes, and a share is always a different volume. Set
`WriteAtomically = true` when something is watching the destination, so it never sees a partial file.

> **Known limitation.** `PipelineContext.Items`, which every file stage publishes its output list
> into, cannot feed `FromCsv<T>` — a dataflow's source is bound when the pipeline is composed, before
> a pattern has matched anything. When a dataflow must consume a dynamically discovered file, extract
> or download it to a *known* path first and name that path, the way the example at the top of this
> section does.

## Command line

`EtlPipelines.Extensions.Cli`, built on [CliWrap](https://github.com/Tyrrrz/CliWrap), runs an
external program as a stage — the same `AddStage`-level shape `RunSql` uses, moving no rows through
the framework because the work happens in a process outside it:

```csharp
services.AddEtlPipeline("nightly", b => b
    .RunCommand("tar", ["-czf", archive.FullName, "-C", staging.FullName, "."]));
```

A non-zero exit code fails the stage, with the process's own stderr (or stdout, if it wrote nothing
to stderr) carried in the error. `CliCommandOptions` adjusts the working directory, environment
variables, a timeout, which exit codes count as success, and — through `Configure` — anything else
CliWrap's own `Command` exposes.

### PowerShell scripts

Two convenience methods run a `.ps1` file with `-File`, so its arguments bind to the script's own
`param()` block the way running it from a shell would, rather than being pasted into a `-Command`
string:

```csharp
// script.ps1:
//   param($Environment)
//   Write-Output "Deploying to $Environment"

services.AddEtlPipeline("deploy", b => b
    .RunPowerShellScript(scripts.File("script.ps1"), ["production"]));
```

`RunPowerShellScript` runs `pwsh` — PowerShell 7+, cross-platform — and is the one to reach for by
default. `RunWindowsPowerShellScript` runs the legacy, Windows-only `powershell.exe` instead, for a
script that specifically needs Windows PowerShell 5.1's behaviour; on any other platform it fails
immediately, at run time, with a clear "requires Windows" error rather than attempting to start a
program that was never going to be there. Neither method installs or detects PowerShell — both
assume it is already on the machine, the same way `RunCommand` assumes its own target executable is.

## SQL databases

`EtlPipelines.Extensions.Sql` works against any ADO.NET provider, and a package per engine adds that engine's
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

### Procedures and scripts

Not every step of a job moves rows through this process. A procedure that reshapes what was just
loaded, or a script that creates the staging tables, is work better done next to the data — and both
are stages:

```csharp
services.AddEtlPipeline("orders", builder => builder
    .RunSql("warehouse", "TRUNCATE TABLE orders_staging")
    .FromCsv<Order>(file)
    .ToSqlTable("warehouse", "orders_staging")
    .RunStoredProcedure("warehouse", "dbo.MergeOrders")
    .RunSqlScript("warehouse", fileSystem.FileInfo.New("rebuild-indexes.sql")));
```

| | |
|---|---|
| `RunSql` | one statement, for the one-liner that does not warrant a file |
| `RunStoredProcedure` | a procedure by name |
| `RunSqlScript` | a file, split into batches the way the engine needs |
| `RunEmbeddedSqlScript` | the same, for a script compiled into the assembly |

`RunSql` and `RunStoredProcedure` send **one command as given** — nothing is split on `GO` or on a
changed delimiter. Several statements, or anything with a procedure body in it, belong in a file.
Bind values through `options.Configure` rather than building the string:

```csharp
.RunSql("warehouse", "DELETE FROM orders WHERE Batch = @batch", options =>
    options.Configure = command => command.Parameters.Add(
        new SqlParameter("@batch", batchId)))
```

All three report no rows, so they stay out of the run's `RowsRead` and `RowsWritten` — see above.
None opens a transaction: a procedure that wants one generally manages its own, and wrapping one
from outside behaves differently on every engine.

**A script file is not a statement**, and this is where engines disagree most. Each provider package
registers the parser its engine needs, under the connection's name:

| Engine | Batches split on |
|---|---|
| `SqlServer` | a line of nothing but `GO` — a client convention the server has never heard of |
| `MySql` | the current delimiter, which `DELIMITER $$` lets a script change so a procedure body can hold semicolons |
| `Oracle` | `;` or `/`, whichever the file uses — keeping the `;` that closes an `END;` |
| `PostgreSql`, `Sqlite` | nothing: both take the whole file in one command |

Ported from [dbdeploy](https://github.com/gigi81/dbdeploy), where they have been in service a while.
Oracle wants one terminator per file: a script closing PL/SQL with `/` is read as using `/`
throughout, so close every statement the same way.

**A script can ship inside the assembly** instead of beside it — nothing to copy on deploy, nothing
to go missing between the build and the run:

```csharp
.RunEmbeddedSqlScript("warehouse", typeof(Program).Assembly, "create-staging.sql")
```

Mark the file as an `EmbeddedResource` in its project. The name is matched leniently, because a
resource's logical name is its root namespace and folder path joined with dots and almost nobody
remembers that: an exact match wins, and failing that a resource whose name *ends* in
`.create-staging.sql` is taken when exactly one does. When nothing matches — or two do — the error
lists what the assembly actually holds.

Both forms are resolved when the stage runs rather than when the pipeline is composed, so a script
fetched by an earlier stage works. `RunSqlScript` also takes an `ISqlScriptSource` if the script
comes from somewhere else entirely.

### Reaching another schema

A job does not always own the schema it works in. Oracle makes this sharpest — a schema *is* a user,
so connecting as `ETL` finds only `ETL`'s tables — but the same question comes up everywhere:

```csharp
services.AddOracleConnection("warehouse", options => options.CurrentSchema = "HR");
```

| Engine | What it issues |
|---|---|
| `Oracle` | `ALTER SESSION SET CURRENT_SCHEMA` |
| `PostgreSql` | `SET search_path` |
| `MySql` | `USE` — a schema *is* a database |

It reaches **every unqualified name in the run**, including the SQL you wrote for `FromSql`,
`RunSql` and the script stages. Nothing else could: `ToSqlTable` takes a table name the library
controls, so `"HR.TRADES"` has always worked there, but your own SELECT is yours.

There is no `CurrentSchema` on SQL Server or SQLite, because neither has a session-level equivalent —
SQL Server's default schema belongs to the database user, set with `ALTER USER … WITH DEFAULT_SCHEMA`.
A property that silently did nothing on one engine would be worse than its absence. Both take the
general form, which every provider has:

```csharp
services.AddSqlServerConnection("warehouse", options =>
    options.SessionStatements.Add("SET LOCK_TIMEOUT 5000"));
```

Statements run on **every** connection as it opens, which is what makes them survive pooling.

**Two things to know**, and the second is the one that bites:

- **It changes name resolution, not privileges.** The session still runs as the connecting user, who
  still needs grants on the other schema's objects. Oracle's `USER` goes on reporting whoever
  connected. Read `CurrentSchema` as "look here first", never as "become this user".
- **Oracle keeps session state on a pooled connection.** Other code opening the *same connection
  string* — another registration, an ORM, a health check — can be handed a connection still pointed
  at your schema. Npgsql and MySqlConnector both reset on return, so this is Oracle's alone. Give a
  connection carrying session settings a connection string of its own, or turn pooling off on it.

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

Runnable programs in [`src/`](src), named `EtlPipelines.Samples.*`:

| Sample | Shows |
|---|---|
| `EtlPipelines.Samples.CsvToExcel` | CSV in, filter and reshape, workbook out — the ordinary job |
| `EtlPipelines.Samples.SqlToWorkbook` | three queries becoming three sheets of one workbook |
| `EtlPipelines.Samples.ExcelToSql` | a hand-filled spreadsheet loaded into a table, bad rows set aside |
| `EtlPipelines.Samples.CsvToDatabase` | one pipeline, five engines — only the registered connection changes |
| `EtlPipelines.Samples.Branching` | archiving the raw rows while the same pass builds a report |
| `EtlPipelines.Samples.ArchiveToDatabase` | a vendor's zip, built by its own pipeline, extracted and loaded into five tables by another |

Each is a command line application, and none of them writes a command: `run` and `list` come from
`EtlPipelines.Hosting`. `--work-dir` says where to work, and defaults to a new directory under the
temp path:

```bash
dotnet run --project src/EtlPipelines.Samples.SqlToWorkbook -- run report --work-dir ./out
```

A sample has no input until it makes one, and that is **a stage of the pipeline** rather than
something done to it beforehand — which is what `AddStage` is for, and what a real job's download or
staging-table step would be:

```csharp
services.AddEtlPipeline(Name, builder => builder
    .AddStage<SalesData>()              // writes the file this job reads
    .FromCsv<SalesRow>(directory.File(InputFile))
    ...
```

Each sample is three files: `Pipeline.cs` registers it, `SeedStage.cs` is the stage that puts the
input in place, and `Program.cs` builds the host. `Samples.ArchiveToDatabase` is the one exception
worth knowing about: its input is a zip a vendor would have sent, not a file this job would ever
write itself, so `SeedStage` runs in a **separate registered pipeline** — `build-feed` — rather than
as an early stage of the job that reads it. `run` with no name runs every registered pipeline in the
order they were registered, so `build-feed` still runs before `archive` without either needing to
know about the other; named individually, `run archive` before `run build-feed` fails, on purpose.

Each sample is also an integration test:
`tests/EtlPipelines.Samples.Tests` runs the pipelines through their own registration and checks what
they left behind, then runs each one again through its command line to prove the wiring holds.
`Samples.CsvToDatabase` is run again against real SQL Server, PostgreSQL, MySQL and Oracle containers
— the same registration, not a copy of it. Samples are documentation that nothing compiles against,
so without that they rot quietly.

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
myapp run           # run every one of them, stopping at the first failure
myapp               # the same: an application asked for nothing runs its pipelines
```

Steps that move no rows — fetching the file, swapping a staging table into place — are stages too, so
a whole job stays one pipeline with one report:

```csharp
services.AddEtlPipeline("orders", builder => builder
    .AddStage<DownloadOrders>()
    .FromCsv<Order>(file)
    .To<SqlSink>());
```

Such a stage reports no rows, and the run's `RowsRead` and `RowsWritten` skip past it to the stages
that moved some — so opening a pipeline with one does not make it report that it read nothing.

Runs report themselves through `Microsoft.Extensions.Logging`. The runtime's own traces are published
to an `ActivitySource` rather than to a logger, so turning the level up surfaces per-stage timings
without an exporter:

```bash
myapp run orders --verbose
```

`--verbose` is recursive on the root command, so it parses before the verb as readily as after it.
`Logging__LogLevel__Default=Debug` in the environment does the same thing for anything finer-grained.

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

Traces and metrics are published through an `ActivitySource` and a `Meter` both named
`EtlPipelines`, so any OpenTelemetry exporter picks them up without this library depending on one.
Every span, instrument and tag name is declared in `EtlDiagnostics` and nowhere else.

| Instrument | |
|---|---|
| `etl.rows.in` / `etl.rows.out` / `etl.rows.failed` | rows a stage consumed, produced and rejected |
| `etl.stage.duration` | stage wall-clock time |
| `etl.pipeline.duration` | run wall-clock time |
| `etl.pipeline.runs` | runs completed |

Tagged with `etl.pipeline`, `etl.stage` and `etl.outcome`. **Rows are counted whether the stage
succeeded or not** — one that died after half a million rows still consumed them, and a counter that
only moved on success would report a failed load as having done nothing at all. `etl.outcome` is what
tells the two apart. Spans carry `etl.run.id`, and the row counts under the same names the
instruments use; a failed span is marked `ActivityStatusCode.Error` with the error's description.

Names are dot-separated throughout, following OpenTelemetry's attribute naming.

Every stage is traced this way, coarse ones included — a file download or an SQL script gets a span
and its row counters (zero, since it moves no rows through the framework) exactly like a dataflow
stage does. The span is opened by the pipeline's run loop, once per stage, so a connector package
never has to depend on the runtime to participate.
