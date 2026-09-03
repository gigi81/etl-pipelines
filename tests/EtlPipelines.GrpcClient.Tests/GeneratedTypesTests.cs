using EtlPipelines.PipelineExecution.V1;

namespace EtlPipelines.GrpcClient.Tests;

/// <summary>
/// Phase 2 smoke test: proves <c>protos/v1/pipeline_execution.proto</c> actually codegenned into
/// this project under <c>EtlPipelines.PipelineExecution.V1</c>. <see cref="GrpcConfigurationProviderTests"/>/
/// <see cref="GrpcProgressReporterTests"/> are Phase 6's real consumers of the generated client.
/// </summary>
[Category("GrpcClient")]
public class GeneratedTypesTests
{
    [Test]
    public async Task Request_and_response_types_exist_under_the_expected_namespace()
    {
        var request = new GetConfigurationRequest { SessionId = "test-session" };
        var response = new GetConfigurationResponse { Entries = { ["ConnectionStrings:sales"] = "..." } };

        await Assert.That(request.SessionId).IsEqualTo("test-session");
        await Assert.That(response.Entries["ConnectionStrings:sales"]).IsEqualTo("...");
    }

    [Test]
    public async Task The_generated_client_type_exists()
    {
        await Assert.That(typeof(PipelineExecutionService.PipelineExecutionServiceClient)).IsNotNull();
    }
}
