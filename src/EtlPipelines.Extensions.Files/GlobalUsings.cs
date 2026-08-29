// Global using directives

global using ErrorOr;
global using EtlPipelines.Abstractions.Building;
global using EtlPipelines.Abstractions.Execution;

// This package's own folders. Declared once here rather than at the top of every file: the split is
// for reading, and having every file reopen with the same lines would undo the point of it.
global using EtlPipelines.Files.Archives;
global using EtlPipelines.Files.Configuration;
global using EtlPipelines.Files.Selection;
global using EtlPipelines.Files.Stages;
global using EtlPipelines.Files.Writing;
