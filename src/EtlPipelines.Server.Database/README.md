# EtlPipelines.Server.Database

`ServerDbContext.cs` and every type under `Entities/` are generated - reverse-engineered from a
real, already-deployed database with `dotnet ef dbcontext scaffold`, not hand-written. `dbdeploy`
owns the schema (`db/`, deployed via `dbdeploy deploy`, never `dotnet ef migrations`);
this project's C# model is a mechanical reflection of whatever that schema actually is, kept in
sync by re-running the same command below rather than by hand-editing generated files.

## Regenerating

1. From the repository root, deploy the schema to a real Postgres the connection string below can
   reach - for a throwaway local one:
   ```bash
   docker run -d --name etlpipelines-scaffold-postgres \
     -p 5432:5432 \
     -e POSTGRES_USER=postgres -e POSTGRES_PASSWORD=postgres -e POSTGRES_DB=etlpipelines_server \
     postgres:15.1
   dbdeploy deploy --path db
   ```
2. From this directory, regenerate:
   ```bash
   dotnet ef dbcontext scaffold \
     "Host=127.0.0.1;Username=postgres;Password=postgres;Database=etlpipelines_server" \
     Npgsql.EntityFrameworkCore.PostgreSQL \
     --context ServerDbContext \
     --output-dir Entities \
     --context-dir . \
     --namespace EtlPipelines.Server.Database.Entities \
     --context-namespace EtlPipelines.Server.Database \
     --no-onconfiguring \
     --force \
     --table Packages --table PackageVersions --table Pipelines --table Agents --table Runs \
     --table StageResults --table AgentResourceSamples --table ConfigurationEntries --table NuGetFeeds
   ```
   The explicit `--table` list matters: without it, scaffolding also reverse-engineers dbdeploy's
   own bookkeeping table (`__migrations`) into an `Entities/Migration.cs` this project has no use
   for.
3. Tear down the throwaway container: `docker rm -f etlpipelines-scaffold-postgres`.

Any hand-written model customization scaffolding itself can't infer (a value conversion, for
example) goes in `ServerDbContext.Customizations.cs`'s `OnModelCreatingPartial` - the one seam the
scaffold command leaves alone on every re-run - never in `ServerDbContext.cs` or `Entities/*.cs`
directly, since both are overwritten wholesale by step 2 above.
