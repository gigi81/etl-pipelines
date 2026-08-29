// Global using directives

global using ErrorOr;
global using EtlPipelines.Abstractions.Building;
global using EtlPipelines.Abstractions.Execution;
global using EtlPipelines.Abstractions.Lifecycle;
global using EtlPipelines.Abstractions.Ports;

// This package's own folders. Declared once here rather than at the top of twenty files: the split
// is for reading, and having every file reopen with the same six lines would undo the point of it.
global using EtlPipelines.Extensions.Sql.Configuration;
global using EtlPipelines.Extensions.Sql.Connections;
global using EtlPipelines.Extensions.Sql.Loading;
global using EtlPipelines.Extensions.Sql.Ports;
global using EtlPipelines.Extensions.Sql.Scripts;
global using EtlPipelines.Extensions.Sql.Stages;
global using EtlPipelines.Extensions.Sql.Statements;
