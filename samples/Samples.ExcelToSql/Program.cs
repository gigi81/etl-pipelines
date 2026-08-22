using EtlPipelines.Samples.Common;
using EtlPipelines.Samples.ExcelToSql;

// Everything a reader came here for is in ExcelToSqlSample.cs. This starts a generic host around it.
return await SampleHost.RunAsync<ExcelToSqlSample>(args);
