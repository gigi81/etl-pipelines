# Writing a pipeline package

A pipeline package is an ordinary console application built on `EtlPipelines.Hosting` and packed as
a [.NET tool](https://learn.microsoft.com/dotnet/core/tools/global-tools). No custom format is
involved. An agent installs it with `dotnet tool install` and runs the shim that command generates.

## The project

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <OutputType>Exe</OutputType>
    <PackAsTool>true</PackAsTool>
    <PackageId>Contoso.Pipelines.Orders</PackageId>
    <Description>Loads the nightly orders file.</Description>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="EtlPipelines.Hosting" />
    <PackageReference Include="EtlPipelines.Extensions.Csv" />
    <PackageReference Include="EtlPipelines.Extensions.Sql.PostgreSql" />
  </ItemGroup>

</Project>
```

Do not set a `RuntimeIdentifier`. A tool package is packed portable, and the platform-specific shim
is generated on the machine that installs it, so one package serves Linux, Windows and macOS agents
alike.

## The program

```csharp
return await new EtlPipelinesHost("Loads the nightly orders file.")
    .ConfigureServices(services => services
        .AddPostgreSqlConnection("orders-db")
        .AddEtlPipeline("orders", builder => builder
            .FromCsv<Order>(file)
            .ToSqlTable("orders-db", "orders")))
    .RunAsync(args);
```

That is all a package needs to take part:

- **`list`** prints one registered pipeline name per line. The agent runs it right after installing,
  and the Server records one `Pipelines` row per name. A package can register as many pipelines as it
  likes. `EtlPipelines.Samples.ArchiveToDatabase` registers two.
- **`run <name> --session-id <id> --server-url <url>`** is how the agent launches a run.
  `EtlPipelinesHost` sees both options and turns on the gRPC client: configuration comes from the
  Server, and every stage result and the final run result are reported back. Nothing in the program
  has to opt in.
- **The exit code** (0 for success, 1 for failure) is reported by the agent as well. If the process
  dies before it can report its own result, the exit code is what settles the run.

## Configuration

Name connections, don't hard-code them. `AddPostgreSqlConnection("orders-db")` with no connection
string resolves `ConnectionStrings:orders-db` from `IConfiguration` when the connection first opens.
Inside an agent-launched run, that value comes from the Server's encrypted store:

```bash
grpcurl -plaintext -import-path protos -proto v1/management.proto \
  -d '{"key":"ConnectionStrings:orders-db","value":"Host=db;Database=orders;..."}' \
  localhost:5000 etlpipelines.management.v1.ManagementService/SetConfigurationEntry
```

The same application still runs locally with an `appsettings.json` or environment variables, because
without `--session-id` and `--server-url` nothing about it changes. See
[Configuration](configuration.md#pipeline-configuration-entries) for how entries are scoped.

## Publishing

Pack it and push it to the stack's bagetter feed:

```bash
dotnet pack --configuration Release --output ./packages
dotnet nuget push ./packages/Contoso.Pipelines.Orders.1.0.0.nupkg \
  --source http://localhost:8080/v3/index.json --api-key any --allow-insecure-connections
```

The compose file does not publish bagetter's port either. Add `ports: ["8080:8080"]` to the `nuget`
service in an override file, or push from a container on the `docker_default` network against
`http://nuget:8080/v3/index.json`, the way the `seed` service does.

It then shows up in `ListAvailablePackages` and can be installed with `InstallPackage`.

## Try it locally first

Everything an agent does can be done by hand, which is the quickest way to check a package before
publishing it:

```bash
dotnet tool install --tool-path ./tool-test --add-source ./packages Contoso.Pipelines.Orders
./tool-test/<command> list
./tool-test/<command> run orders --work-dir ./scratch
dotnet tool uninstall --tool-path ./tool-test Contoso.Pipelines.Orders
```

The shim is named after the tool's command name, which defaults to the assembly name. Set
`<ToolCommandName>` in the project to choose it. The agent does not depend on the name: it runs
whichever single executable `dotnet tool install` left in the install directory.

`tests/EtlPipelines.PipelinePackaging.Tests` automates exactly this round trip for
`EtlPipelines.Samples.ArchiveToDatabase`.
