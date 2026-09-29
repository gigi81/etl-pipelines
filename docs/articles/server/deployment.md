# Deploying with Docker Compose

`docker/docker-compose.yml` brings up the whole stack from a fresh clone. It needs nothing installed
except Docker.

```bash
cd docker
docker compose up --build -d
```

## What starts

| Service | Image | Notes |
|---|---|---|
| `postgres` | `postgres:15.1` | Holds the Server's database, `etlpipelines_server`. Health-checked with `pg_isready`. |
| `nuget` | `bagetter/bagetter` | The package feed, `http://nuget:8080/v3/index.json` inside the network. |
| `server` | built from `docker/Dockerfile.server` | Starts once `postgres` is healthy. Its entrypoint runs `dbdeploy deploy` before starting the app, so an empty database gets its schema without any manual step, and an existing one is left as it is. Health-checked over HTTP/2 on port 5000. |
| `agent` | built from `docker/Dockerfile.agent` | Starts once `server` is healthy. It ships the full .NET SDK, because installing a package means running `dotnet tool install` at run time. |
| `seed` | built from `docker/Dockerfile.seed` | A one-shot job. It packs the six samples and pushes them to `nuget`, retrying until the feed answers, then exits. |

All long-running services use `restart: unless-stopped`.

### Volumes

| Volume | Mounted at | Holds |
|---|---|---|
| `postgres-data` | `/var/lib/postgresql/data` | The database. |
| `nuget-data` | `/data` | bagetter's packages. |
| `server-cache` | `/var/lib/etlpipelines/server-cache` | The Data Protection key ring (`keys/`). **Back this volume up.** Without it, no stored configuration value can be decrypted again. |
| `agent-cache` | `/var/lib/etlpipelines/agent-cache` | Installed tool packages, one directory per package version. |

`docker compose down` keeps the volumes. `docker compose down --volumes` deletes them, and with them
the key ring.

## Published images

On every push to `main` that passes its own `docker compose up` check, the `docker.yml` workflow
publishes the Server and Agent images to GitHub Container Registry:

```text
ghcr.io/gigi81/etl-pipelines/server:latest   (and :<commit sha>)
ghcr.io/gigi81/etl-pipelines/agent:latest    (and :<commit sha>)
```

To run published images instead of building locally, replace the `build:` sections with
`image:` references in a compose override file. Tag by commit SHA if you need a deployment that
never changes under you.

## Talking to the Server

The Server speaks **gRPC over cleartext HTTP/2** (h2c) on port 5000. There is no TLS and no HTTP/1.1
fallback. Server reflection is not enabled, so clients need the `.proto` files from `protos/`.

The compose file does not publish port 5000 to the host. Either run a client inside the compose
network:

```bash
# from the repository root
docker run --rm --network docker_default -v "$PWD/protos:/protos" fullstorydev/grpcurl \
  -plaintext -import-path /protos -proto v1/management.proto \
  server:5000 etlpipelines.management.v1.ManagementService/ListAvailablePackages
```

or publish the port with an override file next to `docker-compose.yml`:

```yaml
# docker/docker-compose.override.yml, picked up automatically by `docker compose`
services:
  server:
    ports:
      - "5000:5000"
```

and point `grpcurl -plaintext -import-path protos -proto v1/management.proto localhost:5000 ...` at
it.

> [!WARNING]
> Publishing the port exposes a Server with **no authentication** to whatever can reach the host.
> Do this only on a trusted network.

## A first run, end to end

The examples below use the port-publishing override above and run from the repository root.

```bash
alias mgmt='grpcurl -plaintext -import-path protos -proto v1/management.proto'
SVC=etlpipelines.management.v1.ManagementService

# 1. What is on the feed? The seed job pushed the six samples.
mgmt localhost:5000 $SVC/ListAvailablePackages

# 2. Install one. Omit "version" to take the latest stable version on the feed.
mgmt -d '{"package_id":"EtlPipelines.Samples.CsvToDatabase","version":"<version from step 1>"}' \
  localhost:5000 $SVC/InstallPackage

# 3. Optional: configuration for the pipeline. The samples need none (they write to a SQLite
#    file in their own work directory), but a pipeline reading GetConnectionString("sales") gets
#    its value like this. Values are encrypted at rest.
mgmt -d '{"key":"ConnectionStrings:sales","value":"<connection string>"}' \
  localhost:5000 $SVC/SetConfigurationEntry

# 4. Find the pipeline's id, run it, and watch it.
mgmt localhost:5000 $SVC/ListInstalledPipelines
mgmt -d '{"pipeline_id":"<id>"}' localhost:5000 $SVC/ExecutePipeline
mgmt -d '{"run_id":"<run id>"}' localhost:5000 $SVC/StreamRunProgress
```

> [!NOTE]
> Nerdbank.GitVersioning stamps packages built from `main` or a `v*` tag with a stable version, and
> packages built from any other branch with a prerelease one (`1.0.42-g1a2b3c4d`). Without a version,
> `InstallPackage` only considers stable versions, so a stack built from a feature branch needs the
> version passed explicitly.

`docker compose logs -f server agent` shows the Server dispatching, the agent installing and
launching, and the pipeline process's own log output.

## Scaling out agents

Every agent registers itself and receives work over its own stream, so more agents is just more
replicas:

```bash
docker compose up -d --scale agent=3
```

Each replica gets its own agent id but, as written, shares the one `agent-cache` volume. Before
scaling out, read the notes on multiple agents in [Known limitations](limitations.md).
