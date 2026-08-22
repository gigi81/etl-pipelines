using EtlPipelines.Samples.SqlToWorkbook;

// The application itself is in Command.cs, where the tests can reach it too.
return await ReportCommand.RunAsync(args);
