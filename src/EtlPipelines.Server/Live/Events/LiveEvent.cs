namespace EtlPipelines.Server.Live.Events;

/// <summary>
/// Something that happened to an agent or a run, and when. Every change to <see cref="LiveState"/> is
/// one of these: it is applied to the in-memory state and appended to the outbox in one step, and
/// the sync service later replays the outbox, in order, into the database - so memory and database
/// are two projections of the same ordered events, and nothing that happened in between two syncs is
/// lost (a run that starts and finishes inside one sync window still gets its start time, its
/// stages and its samples written).
/// </summary>
/// <param name="AtUtc">When it happened, by the server's clock - what the database records, not when it was synced.</param>
public abstract record LiveEvent(DateTime AtUtc);
