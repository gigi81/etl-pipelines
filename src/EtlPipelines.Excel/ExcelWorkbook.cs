using System.Collections.Concurrent;
using System.IO.Abstractions;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using MiniExcelLib;
using MiniExcelLib.OpenXml;

namespace EtlPipelines.Excel;

/// <summary>
/// The sheets one workbook is going to be built from, counted while the pipeline is composed.
/// </summary>
/// <remarks>
/// The count is what tells the writer which sheet is the last one, and therefore when the finished
/// workbook may be renamed into place. It is collected at build time because that is the only moment
/// every <c>ToExcelSheet</c> for a file is known — at run time a stage only knows about itself.
/// </remarks>
internal sealed class SheetPlan
{
    private readonly List<string> _sheets = [];

    public IReadOnlyList<string> Sheets => _sheets;

    public int Add(string sheetName)
    {
        lock (_sheets)
        {
            if (_sheets.Contains(sheetName, StringComparer.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"This workbook already has a sheet named '{sheetName}'. Sheet names identify " +
                    "them inside the file, so each must be unique.");
            }

            _sheets.Add(sheetName);
            return _sheets.Count - 1;
        }
    }
}

/// <summary>Finds the plan a file's sheets are being collected into.</summary>
/// <remarks>
/// Keyed on the <see cref="IFileInfo"/> the caller passes, so the plan lives exactly as long as that
/// object does and nothing is left behind globally. Pass the same file object to every
/// <c>ToExcelSheet</c> call for a workbook — which is what writing them one after another naturally
/// does — and they find the same plan.
/// </remarks>
internal static class SheetPlans
{
    private static readonly ConditionalWeakTable<IFileInfo, SheetPlan> Plans = [];

    public static SheetPlan For(IFileInfo file) => Plans.GetOrCreateValue(file);
}

/// <summary>The workbooks one pipeline run is writing.</summary>
/// <remarks>
/// Keyed on the run's own <see cref="IServiceProvider"/> — the scope <c>RunAsync</c> creates — so the
/// sheets of a run share one writer while two runs, sequential or concurrent, share nothing and each
/// build their own file. That is the same isolation every port gets from being registered scoped,
/// reached without the connector needing to register anything in the container.
/// </remarks>
internal static class Workbooks
{
    private static readonly ConditionalWeakTable<IServiceProvider, ConcurrentDictionary<string, ExcelWorkbookWriter>> ByRun = [];

    public static ExcelWorkbookWriter For(IServiceProvider run, IFileInfo file, SheetPlan plan, ExcelSinkOptions options)
    {
        var workbooks = ByRun.GetOrCreateValue(run);

        var writer = workbooks.GetOrAdd(file.FullName, _ => new ExcelWorkbookWriter(file, plan, options));

        if (!ReferenceEquals(writer.Plan, plan))
        {
            throw new InvalidOperationException(
                $"Two different file objects both name '{file.FullName}', so the sheets meant for one " +
                "workbook were counted as two. Pass the same IFileInfo to every ToExcelSheet call for " +
                "a workbook.");
        }

        return writer;
    }
}

/// <summary>
/// Builds one workbook a sheet at a time, and renames it into place once the last sheet is done.
/// </summary>
/// <remarks>
/// <para>
/// Sheets are written strictly one after another, which is what the pipeline already does: each
/// <c>From(...).ToExcelSheet(...)</c> is its own stage, and stages run in sequence. The first sheet
/// creates the workbook and every later one is inserted into it — measured flat, about the same cost
/// per sheet whether it is the second or the tenth.
/// </para>
/// <para>
/// Rows reach each sheet through a bounded channel, for the same reason the single-sheet sink uses
/// one: MiniExcel writes a sheet from a single sequence it pulls from, while a sink is handed one
/// batch at a time.
/// </para>
/// </remarks>
internal sealed class ExcelWorkbookWriter
{
    private readonly IFileInfo _target;
    private readonly ExcelSinkOptions _options;
    private readonly SemaphoreSlim _oneSheetAtATime = new(1, 1);

    private IFileInfo? _writingTo;
    private int _completed;
    private bool _sheetOpen;

    public ExcelWorkbookWriter(IFileInfo target, SheetPlan plan, ExcelSinkOptions options)
    {
        _target = target;
        Plan = plan;
        _options = options;
    }

    public SheetPlan Plan { get; }

    /// <summary>The file being written — the temporary one until the last sheet promotes it.</summary>
    public IFileInfo? WritingTo => _writingTo;

    /// <summary>Opens a sheet and returns the channel its rows are published to.</summary>
    public async ValueTask<SheetWriter<TRow>> BeginSheetAsync<TRow>(
        string sheetName,
        bool printHeader,
        CancellationToken cancellationToken)
    {
        // Not a lock: two sheets open at once means two ToExcelSheet calls are running concurrently,
        // which only happens inside a Branch - and two writers interleaving into one zip archive
        // produce a file that will not open. Better to say so than to corrupt it.
        if (!await _oneSheetAtATime.WaitAsync(TimeSpan.Zero, cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException(
                $"The sheet '{sheetName}' was started while another sheet of the same workbook was " +
                "still being written. Sheets are written one after another, so give each one its own " +
                "From(...).ToExcelSheet(...) stage rather than putting them in branches, which run at " +
                "the same time.");
        }

        _sheetOpen = true;

        try
        {
            var first = _completed == 0;

            if (first)
            {
                _writingTo = _options.WriteAtomically
                    ? _target.Directory.File($"{Guid.NewGuid():N}.tmp")
                    : _target;

                _writingTo.Directory?.Create();
            }

            var stream = first
                ? _writingTo!.Create()
                : _writingTo!.Open(FileMode.Open, FileAccess.ReadWrite);

            return new SheetWriter<TRow>(this, stream, sheetName, printHeader, first, _options, cancellationToken);
        }
        catch
        {
            _sheetOpen = false;
            _oneSheetAtATime.Release();
            throw;
        }
    }

    /// <summary>Records a finished sheet, and promotes the workbook once the last one lands.</summary>
    public void SheetCompleted()
    {
        if (_sheetOpen)
        {
            _sheetOpen = false;
            _oneSheetAtATime.Release();
        }

        _completed++;

        if (_completed < Plan.Sheets.Count)
        {
            return;
        }

        // Every sheet the pipeline promised has been written, so the workbook is whole. Until this
        // moment the target path does not exist at all.
        if (_options.WriteAtomically && _writingTo is not null && !ReferenceEquals(_writingTo, _target))
        {
            _writingTo.MoveTo(_target.FullName, overwrite: true);
            _writingTo = _target;
        }
    }

    /// <summary>Releases a sheet that did not finish, without promoting anything.</summary>
    public void SheetAbandoned()
    {
        if (_sheetOpen)
        {
            _sheetOpen = false;
            _oneSheetAtATime.Release();
        }
    }
}

/// <summary>One open sheet: the channel rows are written to, and the task filling it.</summary>
internal sealed class SheetWriter<TRow>
{
    private readonly ExcelWorkbookWriter _workbook;
    private readonly Stream _stream;
    private readonly Channel<TRow> _channel;
    private readonly Task _writing;
    private readonly CancellationTokenSource _cancellation;

    public SheetWriter(
        ExcelWorkbookWriter workbook,
        Stream stream,
        string sheetName,
        bool printHeader,
        bool first,
        ExcelSinkOptions options,
        CancellationToken cancellationToken)
    {
        _workbook = workbook;
        _stream = stream;

        _channel = Channel.CreateBounded<TRow>(new BoundedChannelOptions(options.BufferedRows)
        {
            SingleReader = true,
            SingleWriter = true,
            FullMode = BoundedChannelFullMode.Wait,
        });

        _cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        var exporter = MiniExcel.Exporters.GetOpenXmlExporter();
        var rows = _channel.Reader.ReadAllAsync(_cancellation.Token);
        var configuration = options.CreateConfiguration();

        // MiniExcel refuses to insert a sheet into a workbook that was not written in fast mode, and
        // says so only when the insert is attempted. It is set for the first sheet too, so the
        // workbook every later sheet is inserted into was written the way they need it.
        configuration.FastMode = true;

        // The first sheet creates the workbook; the rest are inserted into the one already there.
        _writing = first
            ? exporter.ExportAsync(stream, rows, printHeader, sheetName, configuration,
                cancellationToken: _cancellation.Token)
            : exporter.InsertSheetAsync(stream, rows, sheetName, printHeader,
                configuration: configuration, cancellationToken: _cancellation.Token);

        // Without this, a sheet whose writer failed leaves nobody reading the channel, and the next
        // write that finds the buffer full would wait for a reader that is never coming back.
        _ = _writing.ContinueWith(
            task => _channel.Writer.TryComplete(task.Exception),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    public ChannelWriter<TRow> Rows => _channel.Writer;

    public Task Writing => _writing;

    /// <summary>Closes the sheet and tells the workbook it finished.</summary>
    public async ValueTask CompleteAsync()
    {
        _channel.Writer.TryComplete();
        await _writing.ConfigureAwait(false);
        await _stream.DisposeAsync().ConfigureAwait(false);

        _workbook.SheetCompleted();
        _cancellation.Dispose();
    }

    /// <summary>Gives up on the sheet without promoting the workbook.</summary>
    public async ValueTask AbandonAsync()
    {
        _channel.Writer.TryComplete();
        await _cancellation.CancelAsync().ConfigureAwait(false);

        try
        {
            await _writing.ConfigureAwait(false);
        }
        catch
        {
            // Disposal reports nothing; the run's own error is the one that matters.
        }

        await _stream.DisposeAsync().ConfigureAwait(false);

        _workbook.SheetAbandoned();
        _cancellation.Dispose();
    }
}
