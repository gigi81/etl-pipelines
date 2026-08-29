// Global using directives

global using ErrorOr;
global using EtlPipelines.Abstractions.Building;
global using EtlPipelines.Abstractions.Execution;

// This package's own folders. Declared once here rather than at the top of every file: the split is
// for reading, and having every file reopen with the same lines would undo the point of it.
global using EtlPipelines.Extensions.Files.Archives;
global using EtlPipelines.Extensions.Files.Configuration;
global using EtlPipelines.Extensions.Files.Selection;
global using EtlPipelines.Extensions.Files.Stages;
global using EtlPipelines.Extensions.Files.Writing;
