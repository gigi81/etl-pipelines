# Configuration

The Server and the Agent are ordinary .NET generic hosts. Every setting below can come from
`appsettings.json`, from an environment variable (use `__` in place of `:`, e.g.
`AgentLiveness__Timeout=00:01:00`), or from a command line argument (`--AgentLiveness:Timeout 00:01:00`).
The defaults in each project's `appsettings.json` are the ones the compose network needs.

## Server

| Key | Default | |
|---|---|---|
| `ConnectionStrings:Server` | `Host=postgres;Database=etlpipelines_server;Username=postgres;Password=postgres` | The Postgres database dbdeploy deployed the schema into. |
| `Server:Port` | `5000` | The port Kestrel listens on, on every interface, over cleartext HTTP/2 only. `0` picks a free port. |
| `Server:HttpPort` | `5080` (`0` when `Server:Port` is `0`) | The port of the second endpoint, plain HTTP/1.1, which serves the [WebUI and REST API](webui.md) to browsers. A browser cannot use the HTTP/2-only gRPC port. |
| `Server:PublicUrl` | `http://server:5000` | The address launched pipeline processes call back to, sent to them as `--server-url`. Set it when the Server is reachable at a different address than the one it binds, such as behind a proxy or from another container. If it is empty, the Server's own bound address is used, which only works when agents run on the same machine. |
| `NuGetFeed:Url` | `http://nuget:8080/v3/index.json` | The feed to browse and install from. It is written into the `NuGetFeeds` table on every start, so changing it here is enough. |
| `NuGetFeed:ApiKey` | `any` | The API key sent to the feed when a package is uploaded through the WebUI. The bundled bagetter accepts any key; a feed that checks one needs its real key here. |
| `KeyRing:Path` | `/var/lib/etlpipelines/server-cache/keys` | Where ASP.NET Core Data Protection keeps its key ring. It must be on durable storage (the `server-cache` volume in compose). |
| `LiveState:SyncInterval` | `00:00:05` | How often what has happened (and only if something has) is written to the database, in one batch. The most a hard crash can lose. See [Live state and the database](operations.md#live-state-and-the-database). |
| `AgentLiveness:Timeout` | `00:00:30` | How long an agent can go without a heartbeat before it is marked `Offline` and its active runs `AgentLost`. Agents heartbeat every 10 seconds. Also how long after the Server starts before the first sweep, so agents can reconnect first. |
| `AgentLiveness:PollInterval` | `00:00:10` | How often the liveness sweep runs. |

## Agent

| Key | Default | |
|---|---|---|
| `Agent:ServerUrl` | `http://server:5000` | Where `AgentService` is reachable. |
| `Agent:CacheDirectory` | `/var/lib/etlpipelines/agent-cache` | Where packages are installed, as `<CacheDirectory>/<packageId>/<version>`. It also holds the file `agent-id`, the id the Server gave this agent, so a restart registers as the same agent: see [Operations](operations.md#agent-identity). |
| `Agent:Name` | the machine name | How the WebUI names this agent. In Docker the machine name is the container id, which changes whenever the container is recreated; set a name to keep it stable (the compose file sets `docker-agent`). |
| `Agent:Tags` | `[]` | Free-form tags the agent registers with, such as `linux` or `gpu`. A pipeline can be assigned to agents by tag (see [the WebUI](webui.md#which-agent-runs-a-pipeline)). More tags can be given to an agent in the WebUI; these are reported again at every start. |
| `Agent:MaxConcurrentRuns` | `4` | How many pipeline runs the agent carries out at once; further ones wait for a slot. Installs are not counted. |
| `Agent:HeartbeatInterval` | `00:00:10` | How often the agent tells the Server it is alive. |
| `Agent:ReconnectInitialDelay` | `00:00:01` | The pause before reconnecting after losing the Server; it doubles at each failed attempt. |
| `Agent:ReconnectMaxDelay` | `00:00:30` | The longest pause between attempts to reconnect. |
| `Agent:ReportRetryDelay` | `00:00:01` | The pause before trying again to tell the Server a run started or ended, when it cannot be reached; it doubles up to 30 seconds, for up to 8 attempts. |
| `Agent:CacheSizeCapBytes` | `5368709120` (5 GiB) | Once the cache is larger than this, the least recently used package versions are deleted until it fits. |
| `Agent:CacheEvictionInterval` | `00:30:00` | How often the cache is checked against its cap. |
| `Agent:ResourceSampleInterval` | `00:00:15` | How often a running pipeline process's CPU and working set are sampled and reported to the Server. |

## Pipeline configuration entries

Pipelines get their connection strings, SFTP credentials and any other settings from the Server
rather than from files shipped inside the package. An entry is a flat, colon-separated
`IConfiguration` key and a value:

| Key | Read by |
|---|---|
| `ConnectionStrings:<name>` | `IConfiguration.GetConnectionString("<name>")`, which the SQL connectors use when a connection opens. |
| `Sftp:<name>:Host`, `Sftp:<name>:UserName`, `Sftp:<name>:Password`, ... | The SFTP connector's `Sftp:<name>` section. |
| anything else | Whatever your own code binds or reads. |

Set entries with `ManagementService.SetConfigurationEntry`. Setting an existing key replaces its value.
Values are encrypted with ASP.NET Core Data Protection before they are written to
`ConfigurationEntries.EncryptedValue`, and decrypted only when a run asks for them.

When an agent launches a run, the process calls `GetConfiguration` before anything else. Every entry
is then loaded into the process's `IConfiguration`, on top of whatever `appsettings.json` the package
carries. The connectors read their settings lazily, when a connection first opens, so no connector
needs to know where its configuration came from.

> [!IMPORTANT]
> Entries are **global**: every run receives every entry. There is no per-pipeline or per-package
> scoping yet, so give entries names that do not collide across pipelines, and remember that any
> installed package can read all of them.

> [!WARNING]
> Data Protection with a key ring on a volume is **not production-grade secret management**. It keeps
> values out of the database in plain text, but anyone who can read both the database and the
> `server-cache` volume can decrypt them. It is meant to be swapped for a real key management service
> later.

## Pipeline process options

`EtlPipelinesHost` adds two options to every pipeline application:

| Option | |
|---|---|
| `--session-id <id>` | The run's id, issued by the Server. |
| `--server-url <url>` | Where `PipelineExecutionService` is reachable. |

The gRPC client is turned on only when **both** are given. It pulls configuration at startup and
reports every stage and the final result. With neither, the application runs exactly as it does
locally. Agents always pass both, and you never need to.
