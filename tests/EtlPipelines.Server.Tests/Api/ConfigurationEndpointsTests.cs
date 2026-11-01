using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using EtlPipelines.Server.Contracts;
using Microsoft.EntityFrameworkCore;

namespace EtlPipelines.Server.Tests.Api;

/// <summary>The configuration routes: entries pipelines read, listed by key, never by value.</summary>
[Category("Server")]
public class ConfigurationEndpointsTests
{
    private const string Secret = "Host=db;Password=hunter2";

    [Test]
    public async Task Entries_are_listed_by_key_and_when_they_changed_and_the_value_is_never_in_the_answer()
    {
        //arrange
        await using var host = await ApiTestHost.StartAsync();
        await Put(host, "Sftp:vendor:Password", "p4ss");
        await Put(host, "ConnectionStrings:sales", Secret);

        //act
        var response = await host.Client.GetAsync("/api/configuration");
        var body = await response.Content.ReadAsStringAsync();
        var keys = JsonSerializer.Deserialize<List<ConfigurationKeyDto>>(body, ApiTestHost.Json)!;

        //assert
        keys.Select(k => k.Key).Should().Equal("ConnectionStrings:sales", "Sftp:vendor:Password");
        keys.Should().OnlyContain(k => k.UpdatedAtUtc > DateTime.UtcNow.AddMinutes(-1));
        body.Should().NotContain("hunter2").And.NotContain("p4ss");
    }

    [Test]
    public async Task Setting_a_key_again_replaces_its_value_and_the_value_is_encrypted_at_rest()
    {
        //arrange
        await using var host = await ApiTestHost.StartAsync();
        await Put(host, "ConnectionStrings:sales", "old");

        //act
        var response = await Put(host, "ConnectionStrings:sales", Secret);

        //assert
        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
        await using var context = host.NewContext();
        var entry = await context.ConfigurationEntries.SingleAsync();
        Encoding.UTF8.GetString(entry.EncryptedValue).Should().NotContain("hunter2", "it is stored encrypted");
        entry.EncryptedValue.Should().NotEqual(Encoding.UTF8.GetBytes(Secret));
    }

    [Test]
    [Arguments("")]
    [Arguments("  ")]
    [Arguments("has space")]
    [Arguments("trailing:")]
    [Arguments(":leading")]
    [Arguments("double::colon")]
    public async Task A_key_that_is_not_a_configuration_path_is_a_400(string key)
    {
        await using var host = await ApiTestHost.StartAsync();

        var response = await Put(host, key, "value");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        await using var context = host.NewContext();
        (await context.ConfigurationEntries.CountAsync()).Should().Be(0);
    }

    [Test]
    public async Task An_empty_value_is_a_400()
    {
        await using var host = await ApiTestHost.StartAsync();

        var response = await Put(host, "ConnectionStrings:sales", "");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Test]
    public async Task Deleting_removes_the_entry_and_an_unknown_key_is_a_404()
    {
        //arrange
        await using var host = await ApiTestHost.StartAsync();
        await Put(host, "ConnectionStrings:sales", Secret);

        //act
        var removed = await host.Client.DeleteAsync("/api/configuration?key=ConnectionStrings:sales");
        var again = await host.Client.DeleteAsync("/api/configuration?key=ConnectionStrings:sales");

        //assert
        removed.StatusCode.Should().Be(HttpStatusCode.NoContent);
        again.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await host.GetAsync<List<ConfigurationKeyDto>>("/api/configuration")).Should().BeEmpty();
    }

    private static Task<HttpResponseMessage> Put(ApiTestHost host, string key, string value) =>
        host.Client.PutAsJsonAsync("/api/configuration", new ConfigurationEntryDto(key, value), ApiTestHost.Json);
}
