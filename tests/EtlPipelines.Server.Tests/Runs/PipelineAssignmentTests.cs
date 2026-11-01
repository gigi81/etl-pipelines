using EtlPipelines.Server.Runs;

namespace EtlPipelines.Server.Tests.Runs;

/// <summary><see cref="PipelineAssignment"/>: which agents a pipeline may run on.</summary>
[Category("Server")]
public class PipelineAssignmentTests
{
    private static readonly Guid Named = Guid.NewGuid();

    [Test]
    public void With_nothing_assigned_any_agent_may_run_it()
    {
        PipelineAssignment.Any.IsAny.Should().BeTrue();
        PipelineAssignment.Any.Allows(Guid.NewGuid(), []).Should().BeTrue();
    }

    [Test]
    public void An_agent_with_any_one_of_the_tags_may_run_it()
    {
        var assignment = new PipelineAssignment(["gpu", "eu"], []);

        assignment.Allows(Guid.NewGuid(), ["linux", "eu"]).Should().BeTrue();
        assignment.Allows(Guid.NewGuid(), ["GPU"]).Should().BeTrue("tags are compared ignoring case");
        assignment.Allows(Guid.NewGuid(), ["linux", "us"]).Should().BeFalse();
        assignment.Allows(Guid.NewGuid(), []).Should().BeFalse();
    }

    [Test]
    public void A_named_agent_may_run_it_whatever_its_tags_and_the_tags_work_alongside()
    {
        var assignment = new PipelineAssignment(["gpu"], [Named]);

        assignment.Allows(Named, []).Should().BeTrue("it is named");
        assignment.Allows(Guid.NewGuid(), ["gpu"]).Should().BeTrue("it has the tag");
        assignment.Allows(Guid.NewGuid(), ["cpu"]).Should().BeFalse();
    }

    [Test]
    public void It_describes_itself_for_the_message_when_nothing_fits()
    {
        PipelineAssignment.Any.Describe().Should().Be("any agent");
        new PipelineAssignment(["gpu", "eu"], []).Describe().Should().Be("tagged 'gpu' or 'eu'");
        new PipelineAssignment([], [Named]).Describe().Should().Be($"agent {Named}");
        new PipelineAssignment(["gpu"], [Named]).Describe().Should().Be($"tagged 'gpu'; agent {Named}");
    }
}
