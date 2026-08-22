using EtlPipelines.Samples.CsvToDatabase;

// The application itself is in Command.cs, where the tests can reach it too.
return await TradesCommand.RunAsync(args);
