namespace EtlPipelines.Abstractions.Configuration;

/// <summary>What the pipeline does with a row that a transform or sink rejects.</summary>
public enum RowErrorAction
{
    /// <summary>Abort the run on the first rejected row. The safe default.</summary>
    Fail = 0,

    /// <summary>Count the rejection and carry on. The row is discarded.</summary>
    Skip,

    /// <summary>
    /// Count the rejection and hand the row to the registered <see cref="IDeadLetterSink{TRow}"/>.
    /// Falls back to <see cref="Skip"/> when no dead-letter sink is registered for the row type.
    /// </summary>
    DeadLetter,
}
