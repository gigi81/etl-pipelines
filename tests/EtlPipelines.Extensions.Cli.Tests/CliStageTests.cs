using CliWrap;

namespace EtlPipelines.Extensions.Cli.Tests;

/// <summary>
/// Running an external command as a stage of a pipeline.
/// </summary>
/// <remarks>
/// <c>pwsh</c> stands in for "an arbitrary executable" throughout - it is the one program confirmed
/// present, for real, on every CI leg this repo runs on, so these tests need no OS-specific shell
/// builtin and no container.
/// </remarks>
public sealed class CliStageTests : IDisposable
{
    private const string Pipeline = "run";

    private readonly CliTestHost _host = new();

    public void Dispose() => _host.Dispose();

    private static string[] PwshCommand(string script) => ["-NoLogo", "-NoProfile", "-NonInteractive", "-Command", script];

    [Test]
    public async Task Runs_a_command_as_a_stage()
    {
        //arrange
        _host.AddEtlPipeline(Pipeline, b => b.RunCommand("pwsh", PwshCommand("exit 0")));

        //act
        var result = await _host.RunAsync(Pipeline);

        //assert
        result.IsError.Should().BeFalse(result.IsError ? result.FirstError.Description : null);
        var stage = result.Value.Stages.Should().ContainSingle().Which;
        stage.RowsIn.Should().Be(0);
        stage.RowsOut.Should().Be(0);
        stage.RowsFailed.Should().Be(0);
    }

    [Test]
    public async Task Names_the_stage_from_the_target_and_arguments_when_not_given()
    {
        //arrange
        _host.AddEtlPipeline(Pipeline, b => b.RunCommand("pwsh", PwshCommand("exit 0")));

        //act
        var result = await _host.RunAsync(Pipeline);

        //assert
        var name = result.Value.Stages.Should().ContainSingle().Which.Name;
        name.Length.Should().BeLessThanOrEqualTo(40);
        name.Should().StartWith("pwsh");
    }

    [Test]
    public async Task Takes_a_name_when_one_is_given()
    {
        //arrange
        _host.AddEtlPipeline(Pipeline, b => b.RunCommand(
            "pwsh", PwshCommand("exit 0"), options => options.Name = "run the checker"));

        //act
        var result = await _host.RunAsync(Pipeline);

        //assert
        result.Value.Stages.Should().ContainSingle().Which.Name.Should().Be("run the checker");
    }

    [Test]
    public async Task Fails_with_the_exit_code_when_the_process_exits_non_zero()
    {
        //arrange
        _host.AddEtlPipeline(Pipeline, b => b.RunCommand("pwsh", PwshCommand("exit 7")));

        //act
        var result = await _host.RunAsync(Pipeline);

        //assert
        result.IsError.Should().BeTrue();
        result.FirstError.Code.Should().EndWith(".exit_code");
        result.FirstError.Description.Should().Contain("7");
    }

    [Test]
    public async Task Treats_configured_exit_codes_as_success()
    {
        //arrange
        _host.AddEtlPipeline(Pipeline, b => b.RunCommand(
            "pwsh", PwshCommand("exit 7"), options => options.SuccessExitCodes = [0, 7]));

        //act
        var result = await _host.RunAsync(Pipeline);

        //assert
        result.IsError.Should().BeFalse(result.IsError ? result.FirstError.Description : null);
    }

    [Test]
    public async Task Includes_standard_error_in_the_failure_message()
    {
        //arrange
        _host.AddEtlPipeline(Pipeline, b => b.RunCommand(
            "pwsh", PwshCommand("[Console]::Error.WriteLine('boom'); exit 1")));

        //act
        var result = await _host.RunAsync(Pipeline);

        //assert
        result.FirstError.Description.Should().Contain("boom");
    }

    [Test]
    public async Task Says_so_when_the_executable_is_not_there()
    {
        //arrange
        _host.AddEtlPipeline(Pipeline, b => b.RunCommand("this-executable-does-not-exist-xyz"));

        //act
        var result = await _host.RunAsync(Pipeline);

        //assert
        result.IsError.Should().BeTrue();
        result.FirstError.Code.Should().EndWith(".not_found");
    }

    [Test]
    public async Task Runs_in_the_given_working_directory()
    {
        //arrange
        _host.AddEtlPipeline(Pipeline, b => b.RunCommand(
            "pwsh",
            PwshCommand("Set-Content -Path marker.txt -Value hi"),
            options => options.WorkingDirectory = _host.Root.FullName));

        //act
        var result = await _host.RunAsync(Pipeline);

        //assert
        result.IsError.Should().BeFalse(result.IsError ? result.FirstError.Description : null);
        _host.File("marker.txt").Refresh();
        _host.File("marker.txt").Exists.Should().BeTrue();
    }

    [Test]
    public async Task Passes_environment_variables_to_the_process()
    {
        //arrange
        _host.AddEtlPipeline(Pipeline, b => b.RunCommand(
            "pwsh",
            PwshCommand("if ($env:ETL_TEST_VALUE -eq 'expected') { exit 0 } else { exit 1 }"),
            options => options.EnvironmentVariables = new Dictionary<string, string?> { ["ETL_TEST_VALUE"] = "expected" }));

        //act
        var result = await _host.RunAsync(Pipeline);

        //assert
        result.IsError.Should().BeFalse(result.IsError ? result.FirstError.Description : null);
    }

    [Test]
    public async Task Times_out_a_command_that_runs_too_long()
    {
        //arrange
        _host.AddEtlPipeline(Pipeline, b => b.RunCommand(
            "pwsh",
            PwshCommand("Start-Sleep -Seconds 5"),
            options => options.Timeout = TimeSpan.FromMilliseconds(200)));

        //act
        var result = await _host.RunAsync(Pipeline);

        //assert
        result.IsError.Should().BeTrue();
        result.FirstError.Code.Should().EndWith(".timed_out");
    }

    [Test]
    public async Task Still_reports_a_graceful_failure_when_configure_reenables_clis_own_validation()
    {
        //arrange
        _host.AddEtlPipeline(Pipeline, b => b.RunCommand(
            "pwsh",
            PwshCommand("exit 1"),
            options => options.Configure = cmd => cmd.WithValidation(CommandResultValidation.ZeroExitCode)));

        //act
        var result = await _host.RunAsync(Pipeline);

        //assert
        result.IsError.Should().BeTrue();
        result.FirstError.Code.Should().EndWith(".exit_code");
    }
}
