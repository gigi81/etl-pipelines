using EtlPipelines.Server.Runs;

namespace EtlPipelines.Server.Tests.Runs;

/// <summary><see cref="AgentSelector"/>: which connected agent a run goes to.</summary>
[Category("Server")]
public class AgentSelectorTests
{
    private static AgentCandidate Agent(int n, string[]? tags = null, bool hasPackage = false, int active = 0) =>
        new(new Guid(n, 0, 0, [0, 0, 0, 0, 0, 0, 0, 0]), tags ?? [], hasPackage, active);

    [Test]
    public void Only_an_agent_the_assignment_allows_is_chosen()
    {
        var gpu = Agent(2, ["gpu"]);

        AgentSelector.Choose([Agent(1, ["cpu"]), gpu, Agent(3)], new PipelineAssignment(["gpu"], [])).Should().Be(gpu);
    }

    [Test]
    public void No_agent_fitting_is_no_choice()
    {
        AgentSelector.Choose([Agent(1, ["cpu"])], new PipelineAssignment(["gpu"], [])).Should().BeNull();
        AgentSelector.Choose([], PipelineAssignment.Any).Should().BeNull();
    }

    [Test]
    public void The_agent_with_the_fewest_runs_going_is_chosen_so_work_spreads_out_instead_of_queueing_behind_a_busy_one()
    {
        var idle = Agent(2, active: 0);

        AgentSelector.Choose([Agent(1, active: 2), idle, Agent(3, active: 1)], PipelineAssignment.Any).Should().Be(idle);
        AgentSelector.Choose([Agent(1, hasPackage: true, active: 1), idle], PipelineAssignment.Any).Should().Be(idle, "installing on demand beats waiting");
    }

    [Test]
    public void Among_equally_busy_agents_one_that_has_the_package_already_is_preferred()
    {
        var installed = Agent(2, hasPackage: true, active: 1);

        AgentSelector.Choose([Agent(1, active: 1), installed, Agent(3, active: 1)], PipelineAssignment.Any).Should().Be(installed);
    }

    [Test]
    public void A_tie_goes_to_the_same_agent_every_time()
    {
        AgentCandidate[] agents = [Agent(5), Agent(3), Agent(4)];

        AgentSelector.Choose(agents, PipelineAssignment.Any)!.Id.Should().Be(agents[1].Id);
        AgentSelector.Choose(agents.Reverse(), PipelineAssignment.Any)!.Id.Should().Be(agents[1].Id);
    }

    [Test]
    public void Naming_an_agent_widens_who_may_run_a_pipeline_it_does_not_pin_it()
    {
        var named = Agent(1);
        var tagged = Agent(2, ["gpu"], hasPackage: true);

        // Both are allowed and equally idle; the one with the package wins.
        AgentSelector.Choose([named, tagged], new PipelineAssignment(["gpu"], [named.Id])).Should().Be(tagged);
        AgentSelector.Choose([named], new PipelineAssignment(["gpu"], [named.Id])).Should().Be(named);
    }
}
