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
| `Server:PublicUrl` | `http://server:5000` | The address launched pipeline processes call back to, sent to them as `--server-url`. Set it when the Server is reachable at a different address than the one it binds, such as behind a proxy or from another container. If it is empty, the Server's own bound address is used, which only works when agents run on the same machine. |
| `NuGetFeed:Url` | `http://nuget:8080/v3/index.json` | The feed to browse and install from. It is written into the `NuGetFeeds` table on every start, so changing it here is enough. |
| `KeyRing:Path` | `/var/lib/etlpipelines/server-cache/keys` | Where ASP.NET Core Data Protection keeps its key ring. It must be on durable storage (the `server-cache` volume in compose). |
| `AgentLiveness:Timeout` | `00:00:30` | How long an agent can go without a heartbeat before it is marked `Offline` and its active runs `AgentLost`. Agents heartbeat every 10 seconds. |
| `AgentLiveness:PollInterval` | `00:00:10` | How often the liveness sweep runs. |

## Agent

| Key | Default | |
|---|---|---|
| `Agent:ServerUrl` | `http://server:5000` | Where `AgentService` is reachable. |
| `Agent:CacheDirectory` | `/var/lib/etlpipelines/agent-cache` | Where packages are installed, as `<CacheDirectory>/<packageId>/<version>`. |
| `Agent:Tags` | `[]` | Free-form tags the agent registers with. They are recorded but not yet used to choose an agent. |
| `Agent:CacheSizeCapBytes` | `5368709120` (5 GiB) | Once the cache is larger than this, the least recently used package versions are deleted until it fits. |
| `Agent:CacheEvictionInterval` | `00:30:00` | How often the cache is checked against its cap. |

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
