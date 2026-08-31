using System.Text;
using EtlPipelines.Server.Secrets;
using Microsoft.AspNetCore.DataProtection;
using Moq;

namespace EtlPipelines.Server.Tests.Secrets;

/// <summary>
/// Fast tests against a SQLite-backed <c>ServerDbContext</c> and a mocked
/// <see cref="IDataProtector"/> - SERVER.md Phase 4's own instruction for this project. The mock
/// round-trips whatever bytes it is given, reversed - enough to prove
/// <see cref="SecretsStore"/> actually calls <c>Protect</c> on write and <c>Unprotect</c> on read,
/// without needing a real cryptographic round trip to do it.
/// </summary>
[Category("Server")]
public class SecretsStoreTests
{
    [Test]
    public async Task A_value_is_protected_on_write_and_unprotected_on_read()
    {
        //arrange
        await using var context = SqliteServerDbContext.Create();
        var store = new SecretsStore(context, CreateReversingProtectionProvider());

        //act
        await store.SetAsync("ConnectionStrings:sales", "super-secret", CancellationToken.None);
        context.ChangeTracker.Clear();

        //assert - the row exists but never holds the plaintext.
        var stored = context.ConfigurationEntries.Single(entry => entry.Key == "ConnectionStrings:sales");
        Encoding.UTF8.GetString(stored.EncryptedValue).Should().NotBe("super-secret");

        //act
        var roundTripped = await store.GetAsync("ConnectionStrings:sales", CancellationToken.None);

        //assert
        roundTripped.Should().Be("super-secret");
    }

    [Test]
    public async Task Setting_an_existing_key_again_overwrites_it_rather_than_duplicating_it()
    {
        //arrange
        await using var context = SqliteServerDbContext.Create();
        var store = new SecretsStore(context, CreateReversingProtectionProvider());
        await store.SetAsync("Sftp:vendor:Host", "sftp.example.com", CancellationToken.None);

        //act
        await store.SetAsync("Sftp:vendor:Host", "sftp2.example.com", CancellationToken.None);

        //assert
        context.ConfigurationEntries.Count(entry => entry.Key == "Sftp:vendor:Host").Should().Be(1);
        (await store.GetAsync("Sftp:vendor:Host", CancellationToken.None)).Should().Be("sftp2.example.com");
    }

    [Test]
    public async Task An_unset_key_returns_null()
    {
        //arrange
        await using var context = SqliteServerDbContext.Create();
        var store = new SecretsStore(context, CreateReversingProtectionProvider());

        //act
        var value = await store.GetAsync("nosuchkey", CancellationToken.None);

        //assert
        value.Should().BeNull();
    }

    private static IDataProtectionProvider CreateReversingProtectionProvider()
    {
        var protector = new Mock<IDataProtector>();
        protector.Setup(p => p.Protect(It.IsAny<byte[]>())).Returns<byte[]>(bytes => bytes.Reverse().ToArray());
        protector.Setup(p => p.Unprotect(It.IsAny<byte[]>())).Returns<byte[]>(bytes => bytes.Reverse().ToArray());

        var provider = new Mock<IDataProtectionProvider>();
        provider.Setup(p => p.CreateProtector(It.IsAny<string>())).Returns(protector.Object);
        return provider.Object;
    }
}
