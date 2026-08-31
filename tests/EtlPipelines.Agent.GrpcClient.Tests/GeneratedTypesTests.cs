using EtlPipelines.AgentExecution.V1;

namespace EtlPipelines.Agent.GrpcClient.Tests;

/// <summary>
/// Phase 2 smoke test: proves <c>protos/v1/agent_execution.proto</c> actually codegenned into
/// this project under <c>EtlPipelines.AgentExecution.V1</c>, including the <c>WorkItem</c>
/// <c>oneof</c> - no consumer of the generated client exists yet (Phase 5), so there is nothing
/// else to test here.
/// </summary>
[Category("Agent")]
public class GeneratedTypesTests
{
    [Test]
    public async Task Request_and_response_types_exist_under_the_expected_namespace()
    {
        var request = new RegisterAgentRequest { MachineName = "agent-01", Version = "1.0.0" };
        request.Tags.Add("linux");

        await Assert.That(request.MachineName).IsEqualTo("agent-01");
        await Assert.That(request.Tags).Contains("linux");
    }

    [Test]
    public async Task WorkItem_carries_exactly_one_of_its_two_kinds()
    {
        var installWorkItem = new WorkItem
        {
            WorkItemId = "w1",
            InstallPackage = new InstallPackageWorkItem { PackageId = "EtlPipelines.Samples.CsvToDatabase" },
        };

        await Assert.That(installWorkItem.KindCase).IsEqualTo(WorkItem.KindOneofCase.InstallPackage);
        await Assert.That(installWorkItem.ExecutePipeline).IsNull();
    }

    [Test]
    public async Task The_generated_client_type_exists()
    {
        await Assert.That(typeof(AgentService.AgentServiceClient)).IsNotNull();
    }
}
