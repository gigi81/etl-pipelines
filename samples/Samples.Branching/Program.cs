using EtlPipelines.Samples.Branching;
using EtlPipelines.Samples.Common;

// Everything a reader came here for is in BranchingSample.cs. This starts a generic host around it.
return await SampleHost.RunAsync<BranchingSample>(args);
