using EtlPipelines.Samples.CsvToExcel;

// The application itself is in Command.cs, where the tests can reach it too.
return await SalesCommand.RunAsync(args);
