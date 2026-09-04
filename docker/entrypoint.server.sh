#!/bin/sh
set -e

# db/dbsettings.json (committed) targets 127.0.0.1 - a developer running dbdeploy locally, or
# db.yml's own CI Postgres service container. Inside this compose network Postgres is reachable
# as "postgres" instead - the same host appsettings.json's own ConnectionStrings:Server already
# carries for the app itself - regenerated here the same way
# tests/EtlPipelines.Server.Tests/SchemaDeployer.cs's own tests already do (a fresh dbsettings.json
# written right before invoking dbdeploy) rather than shipping a second, easily-drifting copy of
# the same connection string in the image.
cat > /app/db/dbsettings.json <<'EOF'
{
  "global": { "defaultProvider": "postgreSql", "scriptTimeout": 600 },
  "databases": {
    "server": { "connectionString": "Host=postgres;Database=etlpipelines_server;Username=postgres;Password=postgres" }
  }
}
EOF

# Postgres is already accepting connections by the time this runs (docker-compose.yml's own
# depends_on: postgres: condition: service_healthy) - a fresh postgres-data volume is still a
# genuinely empty database until this actually runs once, though; an already-deployed one (every
# start after the first) is a fast no-op for dbdeploy to check, not a repeated schema rebuild.
dbdeploy deploy --path /app/db

exec dotnet EtlPipelines.Server.dll
