using EtlPipelines.Samples.Common;
using EtlPipelines.Samples.SqlToWorkbook;

// Everything a reader came here for is in SqlToWorkbookSample.cs. This starts a generic host around it.
return await SampleHost.RunAsync<SqlToWorkbookSample>(args);
