## Acrchitecture
Add the following projects:
- EtlPipelines.Server (the GRPC server)
- EtlPipelines.Server.Database (the Entity Framework database layer used by the server, using postgresql)
- EtlPipelines.Agent (the agent that will execute the pipelines in a separate process)
- EtlPipelines.GrpcClient (the GRPC client to communicate with the server, used by the pipelines processes)
- EtlPipelines.Agent.GrpcClient (the GRPC client to communicate with the server, used by the agents)

## Description
The server is a GRPC server that handles ETL (Extract, Transform, Load) processes.
It provides endpoints for clients to retrieve configuration but also for reporting back pipeline execution metrics, status and results.
The server is built using .NET Core and utilizes Entity Framework for database interactions.

The single pipelines are published as NuGet packages that are self-contained and can be run in isolation.
For example the samples projects are an example of how to use the pipelines in a standalone manner.
The server:
- connects to the nuget server to download the nuget pipelines packages
- stores configuration items like database connection strings, secrets, sftp details, etc. in a secure manner
- is responsible for orchestrating the execution of the pipelines and managing their lifecycle
- delegates actual execution of the pipelines processes to one or more agents so that load can be distributed
- provides logging and monitoring capabilities to track the progress and status of the ETL processes
- maintains a local repository of the downloaded nuget packages to avoid downloading them multiple times

## Available pipelines
The server will have apis to:
- provide a list of installed pipelines
- provide a list of updates available for the installed pipelines
- provide a list of available pipelines packages that can be installed from the nuget server
- install a pipeline package
- uninstall a pipeline package
- update a pipeline package

## Install a pipeline package
- a client will send a request to the server to install a specific pipeline
- the server will download the nuget package for the requested pipeline and store it in a cache folder
- the server will extract the nuget package and store the extracted files in a cache folder
- the server will run the `list` command of the pipeline executable (see samples projects)
- the server will store the list of available pipelines in a database for future reference

## Execute a pipeline:
To execute a pipeline, the server will:
- receive a request from a client to execute a specific pipeline
- delegate execution to an agent (the agent can be on the same machine or on a different machine)
- the agent downloads the nuget package from a nuget server and extracts the nuget package for the requested pipeline in a cache folder
- the agent executes the pipeline in a separate process passing as a parameter a session ID and server url
- the pipeline executable will use the EtlPipelines.Client to request from the server any configuration or data needed for the execution
- the pipeline will report its progress and results back to the server using the provided session ID and server URL
- the agent monitors the execution of the process and reports back to th server CPU and memory usage, start and completion as well as any errors encountered during execution

## Docker compose file
Add a `docker` folder to the root of the solution and add a docker compose file that will define the following services:
- server: the GRPC server
- agent: the agent that will execute the pipelines in a separate process
- postgres: the postgresql database used by the server
- nuget: a nuget server to host the pipeline packages (ex. https://www.bagetter.com/)

The docker compose file will include volumes for persisting:
- the database data
- the server cache
- the nuget server data
- the agent cache

## Development strategy
Divide the development into manageable phases. Each phase should have clear objectives and deliverables.
Each phase will consist of the following steps:
- create a feature branch for the phase
- implement the deliverables for the phase including code, tests but excluding documentation
- raise a PR and wait for review and approval
- once the PR is approved, merge the feature branch into the main branch
- move to the next phase

Once all phases are completed, have a final phase to update the documentation to reflect the final state of the project and ensure that all features are properly documented.
For the documentation, consider using a tool like DocFX and create a docs folder in the root of the solution to manage the docset.
