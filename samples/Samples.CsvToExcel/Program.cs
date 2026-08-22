using EtlPipelines.Samples.Common;
using EtlPipelines.Samples.CsvToExcel;

// Everything a reader came here for is in CsvToExcelSample.cs. This starts a generic host around it.
return await SampleHost.RunAsync<CsvToExcelSample>(args);
