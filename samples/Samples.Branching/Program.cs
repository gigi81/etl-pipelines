using EtlPipelines.Samples.Branching;

// The application itself is in Command.cs, where the tests can reach it too.
return await ReadingsCommand.RunAsync(args);
