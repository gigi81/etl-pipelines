using EtlPipelines.Samples.Common;
using EtlPipelines.Samples.CsvToDatabase;

// Everything a reader came here for is in CsvToDatabaseSample.cs. This starts a generic host around it.
return await SampleHost.RunAsync<CsvToDatabaseSample>(args);
