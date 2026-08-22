// Global using directives

global using ErrorOr;
global using EtlPipelines.Abstractions.Building;
global using EtlPipelines.Abstractions.Execution;
global using EtlPipelines.Abstractions.Lifecycle;
global using EtlPipelines.Abstractions.Ports;

// This package's own folders. Declared once here rather than at the top of twenty files: the split
// is for reading, and having every file reopen with the same six lines would undo the point of it.
global using EtlPipelines.Sql.Configuration;
global using EtlPipelines.Sql.Connections;
global using EtlPipelines.Sql.Loading;
global using EtlPipelines.Sql.Ports;
global using EtlPipelines.Sql.Scripts;
global using EtlPipelines.Sql.Stages;
