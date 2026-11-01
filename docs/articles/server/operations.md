# Operations and reliability

## Live state and the database

The Server holds what is happening - every agent, and every run that is in flight or finished within
the last hour, with its stages and resource samples - in memory (`LiveState`). It is the **only** store:
the gRPC services, `StreamRunProgress`, the REST API and the WebUI all read and change it. The
database is the durable copy, written behind.

**Every change is an event.** An agent registering, a heartbeat, a run created, started, completing a
stage, sampled, or finished: each is an immutable event, stamped with the time it happened. A change is
applied to the in-memory state and appended to an ordered outbox in one step, under one lock, so the
state and what will be written cannot disagree. (No gRPC call waits for, or can be failed by, the
database.)

**The outbox is written in batches.** Every `LiveState:SyncInterval` (5 seconds by default), if
anything happened, the sync service (`LiveStateSync`) takes the outbox and replays its events, in
order, into `Agents`, `Runs`, `StageResults` and `AgentResourceSamples`, in one transaction. Because it
replays events instead of copying the latest state, nothing that happened between two syncs is lost: a
run that is created, started, reports its stages and finishes inside one window still has its start
time, every stage and every sample written, and its completion time is when it completed, not when it
was synced. A heartbeat that an offline mark or a later heartbeat overtakes inside a window is
the one thing not kept, as the row only holds the latest.

**What a restart does.** On startup the state is loaded from the database before the Server accepts
requests - the agents, and the runs in flight or finished within the last hour, with what had been
written of their stages and samples - so runs carry on and keep reporting. A clean stop (`SIGTERM`)
writes everything still waiting first, and loses nothing. A **hard crash loses what happened since the
last sync**, at most `LiveState:SyncInterval` of events: some stages or samples, a status change, or a
run created in that window (its agent's later reports for it are answered `NOT_FOUND`).

**When the database is unwell.** A batch that cannot be written goes back to the front of the outbox
and is tried again at the next sync; the Server keeps serving from memory meanwhile. If it fails three
times running, its events are written one by one, so one event the database refuses is logged and
dropped instead of holding up everything behind it. The outbox is not bounded: a long outage grows it.

**Reading the tables directly.** The database is up to one sync interval behind memory, and does not
hold agents' per-heartbeat history. Anything that must see the present state (the API, gRPC, the
WebUI) reads memory; the *History* list merges the two. `LiveStateSync` has a public `FlushAsync` for
anything that needs the database caught up now (the end-to-end tests call it).

## Agent identity

The first time an agent starts, the Server issues it an id. The agent writes that id to a file,
`agent-id`, in its cache directory (`Agent:CacheDirectory`) and sends it back whenever it registers, so
a restart - a crash and `restart: unless-stopped`, a `docker compose down` and `up`, a rebuilt image - is
the same agent: one entry in the agents list, with its tags, the pipelines assigned to it and its run
history. Without the file (a first start, or a lost volume) it is a new agent, and the old entry stays
`Offline` until it is removed in the WebUI.

In Docker the file lives in the `agent-cache` volume, so keep that volume; `docker compose down
--volumes` deletes it. The agent's *name* is a separate matter: it is the machine name unless
`Agent:Name` is set, and the container id (the machine name in Docker) changes when a container is
recreated - the compose file sets `Agent:Name` for that reason. An agent re-registering also updates its
name, version and host details, and brings back an agent that had been removed (without the tags it was
given).

## Agent liveness

An agent's `Agents.Status` is `Online` or `Offline` (set by this sweep, and back to `Online` by its next
heartbeat) - or `Removed`, which is what removing an agent from the WebUI does. A removed agent is no
longer listed or loaded, but its row stays so the runs it carried out still show where they ran.

Each agent heartbeats every 10 seconds. A background sweep on the Server (`AgentLivenessMonitor`)
runs every `AgentLiveness:PollInterval` (10 seconds by default) and looks for agents whose last
heartbeat is older than `AgentLiveness:Timeout` (30 seconds by default):

- The agent is marked `Offline`.
- Every run that agent still owns in `Queued`, `Dispatched` or `Running` is marked **`AgentLost`**, with
  `CompletedAt` set. Anyone watching the run (`StreamRunProgress`, the WebUI) is told it ended,
  instead of waiting forever.

The sweep checks each run against its agent's heartbeat directly, so a run is caught however its agent
came to be silent. It works on the [live state](#live-state-and-the-database), which knows every
agent's last heartbeat the moment it arrives, so a sweep reads no database; what it changes is written
there with everything else.

An agent that shuts down cleanly closes its `Subscribe` stream and is no longer dispatched to right
away. The heartbeat timeout covers the other case: a process or container that died without saying
anything.

### `AgentLost` runs are never retried

A run that was cut off may have partly applied its load. Whether running it again is safe depends
on whether that pipeline is idempotent, which the Server has no way to know, so it does not guess.
`AgentLost` is a terminal state that someone has to look at. To run the pipeline again, call
`ExecutePipeline`, which creates a new run.

To find them:

```sql
SELECT r."Id", p."Name", r."RequestedAt", r."CompletedAt", a."MachineName"
FROM "Runs" r
JOIN "Pipelines" p ON p."Id" = r."PipelineId"
LEFT JOIN "Agents" a ON a."Id" = r."AgentId"
WHERE r."Status" = 'AgentLost'
ORDER BY r."CompletedAt" DESC;
```

## Agent cache eviction

Every installed package version takes a directory under `Agent:CacheDirectory`. Installing a version,
or running a pipeline from it, touches a `.last-used` marker in that directory. Every
`Agent:CacheEvictionInterval` (30 minutes by default), `CacheEvictor` adds up the cache and, while it
is over `Agent:CacheSizeCapBytes` (5 GiB by default), deletes the least recently used version
directory. A directory that cannot be deleted, for example because a running process has a file
open, is skipped and tried again next time.

Eviction does not tell the Server, and does not need to: a run dispatched to an agent that evicted
its version reinstalls it from the feed first, then runs. See [Known limitations](limitations.md).

## The Server's cache and the key ring

`server-cache` holds only the Data Protection key ring, and it is never swept: deleting keys would
make stored configuration values unrecoverable. It stays a few kilobytes in size. Back it up
together with the database, and restore both together.

## Health checks

| Service | Check |
|---|---|
| `postgres` | `pg_isready` |
| `server` | `curl --http2-prior-knowledge http://localhost:5000/`. The Server answers `/` with a plain-text notice over HTTP/2, which also proves Kestrel is serving h2c. |
| `agent` | None. The agent never listens on a port. Its liveness is the heartbeat above, as seen by the Server. |

## Logs

Everything logs through `Microsoft.Extensions.Logging` to the console, so `docker compose logs -f`
shows it all. A launched pipeline's standard output and error are piped straight through to the
agent's own, so its log lines appear in `docker compose logs agent`, unprefixed. The warnings worth
alerting on are:

- `Marked {AgentCount} agent(s) offline and {RunCount} run(s) AgentLost ...` from the Server.
- `Execution failed for pipeline ...` from an agent. This covers a missing install, a shim that would
  not start, or anything else that stopped the agent from launching the run.
- `Agent liveness sweep failed; will retry on the next tick.` from the Server.
