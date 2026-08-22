using EtlPipelines.Samples.ExcelToSql;

// The application itself is in ImportCommand.cs, where the tests can reach it too.
return await ImportCli.RunAsync(args);
