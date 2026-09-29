# Operations and reliability

## Agent liveness

Each agent heartbeats every 10 seconds. A background sweep on the Server (`AgentLivenessMonitor`)
runs every `AgentLiveness:PollInterval` (10 seconds by default) and looks for agents whose last
heartbeat is older than `AgentLiveness:Timeout` (30 seconds by default):

- The agent is marked `Offline`.
- Every run that agent still owns in `Queued`, `Dispatched` or `Running` is marked **`AgentLost`**, with
  `CompletedAt` set. A `RunCompleted { status: AGENT_LOST }` event is published, so anyone watching
  `StreamRunProgress` sees the run end instead of waiting forever.

The sweep checks each run against its agent's heartbeat directly, so a run left behind by an earlier
sweep that failed partway through is still picked up by the next one. A failed sweep, such as a
database blip, is logged and retried on the next tick.

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

Eviction does not tell the Server. A version evicted from an agent is still listed as installed, and
a run dispatched to that agent fails with exit code `-1` ("not installed in this agent's cache").
Install the version again to put it back. See [Known limitations](limitations.md).

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
