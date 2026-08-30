using System.IO.Abstractions;

namespace EtlPipelines.Extensions.Cli.Tests;

/// <summary>
/// Running a PowerShell script file as a stage of a pipeline, with <c>pwsh</c> and with the legacy,
/// Windows-only <c>powershell.exe</c>.
/// </summary>
public sealed class PowerShellScriptTests : IDisposable
{
    private const string Pipeline = "run";

    private readonly CliTestHost _host = new();

    public void Dispose() => _host.Dispose();

    [Test]
    public async Task Runs_a_powershell_script_file_with_pwsh()
    {
        //arrange
        var script = _host.Script("check.ps1", "exit 0");
        _host.AddEtlPipeline(Pipeline, b => b.RunPowerShellScript(script));

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
    public async Task Fails_when_the_script_throws()
    {
        //arrange
        var script = _host.Script("fail.ps1", "throw 'boom'");
        _host.AddEtlPipeline(Pipeline, b => b.RunPowerShellScript(script));

        //act
        var result = await _host.RunAsync(Pipeline);

        //assert
        result.IsError.Should().BeTrue();
        result.FirstError.Description.Should().Contain("boom");
    }

    [Test]
    public async Task Passes_arguments_to_the_scripts_param_block()
    {
        //arrange
        var script = _host.Script(
            "greet.ps1",
            """
            param($Name)
            Set-Content -Path marker.txt -Value $Name
            """);

        _host.AddEtlPipeline(Pipeline, b => b.RunPowerShellScript(
            script, ["World"], options => options.WorkingDirectory = _host.Root.FullName));

        //act
        var result = await _host.RunAsync(Pipeline);

        //assert
        result.IsError.Should().BeFalse(result.IsError ? result.FirstError.Description : null);
        var marketContent = await _host.File("marker.txt").ReadAllTextAsync();
        marketContent.Trim().Should().Be("World");
    }

    [Test]
    public async Task Names_the_stage_after_the_script_file_when_not_given()
    {
        //arrange
        var script = _host.Script("check.ps1", "exit 0");
        _host.AddEtlPipeline(Pipeline, b => b.RunPowerShellScript(script));

        //act
        var result = await _host.RunAsync(Pipeline);

        //assert
        result.Value.Stages.Should().ContainSingle().Which.Name.Should().Be(script.Name);
    }

    [Test]
    public async Task Takes_a_name_when_one_is_given()
    {
        //arrange
        var script = _host.Script("check.ps1", "exit 0");
        _host.AddEtlPipeline(Pipeline, b => b.RunPowerShellScript(
            script, configure: options => options.Name = "run the checker"));

        //act
        var result = await _host.RunAsync(Pipeline);

        //assert
        result.Value.Stages.Should().ContainSingle().Which.Name.Should().Be("run the checker");
    }

    [Test]
    public async Task Says_so_when_the_script_file_is_missing()
    {
        //arrange
        var script = _host.File("missing.ps1");
        _host.AddEtlPipeline(Pipeline, b => b.RunPowerShellScript(script));

        //act
        var result = await _host.RunAsync(Pipeline);

        //assert
        result.IsError.Should().BeTrue();
        result.FirstError.Code.Should().EndWith(".script_missing");
    }

    [Test]
    public async Task Honours_options_the_same_way_RunCommand_does()
    {
        //arrange
        var script = _host.Script("touch.ps1", "Set-Content -Path marker.txt -Value hi");
        _host.AddEtlPipeline(Pipeline, b => b.RunPowerShellScript(
            script, configure: options => options.WorkingDirectory = _host.Root.FullName));

        //act
        var result = await _host.RunAsync(Pipeline);

        //assert
        result.IsError.Should().BeFalse(result.IsError ? result.FirstError.Description : null);
        _host.File("marker.txt").Refresh();
        _host.File("marker.txt").Exists.Should().BeTrue();
    }

    [Test]
    public async Task Only_runs_windows_powershell_script_on_windows()
    {
        //arrange
        var script = _host.Script("check.ps1", "exit 0");
        _host.AddEtlPipeline(Pipeline, b => b.RunWindowsPowerShellScript(script));

        //act
        var result = await _host.RunAsync(Pipeline);

        //assert
        if (OperatingSystem.IsWindows())
        {
            result.IsError.Should().BeFalse(result.IsError ? result.FirstError.Description : null);
        }
        else
        {
            result.IsError.Should().BeTrue();
            result.FirstError.Code.Should().EndWith(".unsupported_platform");
        }
    }
}
