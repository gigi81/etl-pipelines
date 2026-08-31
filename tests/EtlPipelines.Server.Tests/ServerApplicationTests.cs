using EtlPipelines.AgentExecution.V1;
using EtlPipelines.Management.V1;
using EtlPipelines.PipelineExecution.V1;
using EtlPipelines.Server;
using EtlPipelines.Server.Services;

namespace EtlPipelines.Server.Tests;

/// <summary>
/// Phase 2 smoke test: proves the generated proto types exist under the expected namespaces, the
/// three service stubs really implement the generated server base classes, and the whole
/// <see cref="WebApplication"/> - DI included - builds without error.
/// </summary>
/// <remarks>
/// Deliberately never calls <c>RunAsync()</c>/<c>StartAsync()</c> - <see cref="ServerApplication"/>
/// itself explains why building stops short of opening a real socket, which is what keeps this
/// test compatible with Phase 2's "no network code yet."
/// </remarks>
[Category("Server")]
public class ServerApplicationTests
{
    [Test]
    public async Task The_host_builds_with_every_v1_service_mapped()
    {
        //act
        var app = ServerApplication.Build([]);

        //assert
        try
        {
            app.Should().NotBeNull();
        }
        finally
        {
            await app.DisposeAsync();
        }
    }

    [Test]
    public async Task Service_stubs_implement_the_generated_server_base_classes()
    {
        await Assert.That(typeof(PipelineExecutionServiceImpl).IsSubclassOf(typeof(PipelineExecutionService.PipelineExecutionServiceBase))).IsTrue();
        await Assert.That(typeof(AgentServiceImpl).IsSubclassOf(typeof(AgentService.AgentServiceBase))).IsTrue();
        await Assert.That(typeof(ManagementServiceImpl).IsSubclassOf(typeof(ManagementService.ManagementServiceBase))).IsTrue();
    }

    [Test]
    public async Task Generated_request_and_response_types_exist_under_every_v1_namespace()
    {
        // Not exhaustive - one message per proto file is enough to prove protoc codegen actually
        // ran and landed the expected csharp_namespace, which is what this test exists to check.
        await Assert.That(new GetConfigurationRequest { SessionId = "s" }.SessionId).IsEqualTo("s");
        await Assert.That(new RegisterAgentRequest { MachineName = "m" }.MachineName).IsEqualTo("m");
        await Assert.That(new SetConfigurationEntryRequest { Key = "k" }.Key).IsEqualTo("k");
    }
}
