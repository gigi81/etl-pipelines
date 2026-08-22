using EtlPipelines.Samples.ExcelToSql;

// The application itself is in Command.cs, where the tests can reach it too.
return await ImportCommand.RunAsync(args);
