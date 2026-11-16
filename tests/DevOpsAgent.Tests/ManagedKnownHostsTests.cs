using System.Security.Cryptography;
using System.Text;
using DevOpsAgent.Infrastructure.Ssh;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace DevOpsAgent.Tests;

/// <summary>T8.4 (D-041): trust on first use, refused changed keys, owner-accepted replacements, manual keys honoured.</summary>
public sealed class ManagedKnownHostsTests : IDisposable
{
    private static readonly byte[] KeyA = Convert.FromBase64String("AAAAC3NzaC1lZDI1NTE5AAAAIAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA");
    private static readonly byte[] KeyB = Convert.FromBase64String("AAAAC3NzaC1lZDI1NTE5AAAAIBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB");
    private readonly string _directory = Directory.CreateTempSubdirectory("dva-tofu-").FullName;
    private string Manual => Path.Combine(_directory, "known_hosts");
    private string Managed => Path.Combine(_directory, "data", "known_hosts");

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private ManagedKnownHosts Store() => new(Options.Create(new SshConnectionOptions { KnownHostsFile = Manual, ManagedKnownHostsFile = Managed }),
        TimeProvider.System, NullLogger<ManagedKnownHosts>.Instance);

    [Fact]
    public async Task UnknownHost_IsTrustedOnFirstUse_AndRecordedWithItsFirstSeenTime()
    {
        var store = Store();

        Assert.Equal(HostKeyDecision.FirstUse, store.Check("127.0.0.1", 2201, "ssh-ed25519", KeyA));
        Assert.False(File.Exists(Managed)); // nothing is written before the connection succeeded
        await store.RecordFirstUseAsync("127.0.0.1", 2201, "ssh-ed25519", KeyA, CancellationToken.None);

        var line = Assert.Single(File.ReadAllLines(Managed));
        Assert.StartsWith("[127.0.0.1]:2201 ssh-ed25519 ", line, StringComparison.Ordinal);
        Assert.Contains(" dva-first-seen=", line, StringComparison.Ordinal);
        Assert.Equal(HostKeyDecision.Trusted, Store().Check("127.0.0.1", 2201, "ssh-ed25519", KeyA));
        var key = Assert.Single(store.GetKeys("127.0.0.1", 2201));
        Assert.Equal(ManagedKnownHosts.Fingerprint(KeyA), key.Fingerprint);
        Assert.StartsWith("SHA256:", key.Fingerprint, StringComparison.Ordinal);
        Assert.False(key.Manual);
        Assert.NotNull(key.FirstSeenUtc);
        Assert.InRange(key.FirstSeenUtc!.Value, DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddMinutes(1));
    }

    [Fact]
    public async Task ChangedKey_IsRefused_AndRememberedForTheServerPage()
    {
        var store = Store();
        await store.RecordFirstUseAsync("10.88.20.30", 22, "ssh-ed25519", KeyA, CancellationToken.None);

        Assert.Equal(HostKeyDecision.Mismatch, store.Check("10.88.20.30", 22, "ssh-ed25519", KeyB));

        var mismatch = store.GetMismatch("10.88.20.30", 22);
        Assert.NotNull(mismatch);
        Assert.Equal(ManagedKnownHosts.Fingerprint(KeyA), Assert.Single(mismatch.Stored).Fingerprint);
        Assert.Equal(ManagedKnownHosts.Fingerprint(KeyB), mismatch.PresentedFingerprint);
        Assert.False(mismatch.StoredIsManual);
        // A second first use does not overwrite the stored key.
        await store.RecordFirstUseAsync("10.88.20.30", 22, "ssh-ed25519", KeyB, CancellationToken.None);
        Assert.Equal(HostKeyDecision.Mismatch, store.Check("10.88.20.30", 22, "ssh-ed25519", KeyB));
    }

    [Fact]
    public async Task AcceptingThePresentedKey_ReplacesTheStoredOne_OnlyWithTheConfirmedFingerprint()
    {
        var store = Store();
        await store.RecordFirstUseAsync("db", 22, "ssh-ed25519", KeyA, CancellationToken.None);
        await store.RecordFirstUseAsync("other", 22, "ssh-ed25519", KeyA, CancellationToken.None);
        store.Check("db", 22, "ssh-ed25519", KeyB);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.AcceptPresentedKeyAsync("db", 22, ManagedKnownHosts.Fingerprint(KeyA), CancellationToken.None));
        await store.AcceptPresentedKeyAsync("db", 22, ManagedKnownHosts.Fingerprint(KeyB), CancellationToken.None);

        Assert.Equal(HostKeyDecision.Trusted, store.Check("db", 22, "ssh-ed25519", KeyB));
        Assert.Equal(HostKeyDecision.Mismatch, store.Check("db", 22, "ssh-ed25519", KeyA));
        Assert.Equal(HostKeyDecision.Trusted, store.Check("other", 22, "ssh-ed25519", KeyA)); // other hosts untouched
        Assert.Equal(2, File.ReadAllLines(Managed).Length);
        // Nothing left to accept once the new key is trusted.
        store.Check("db", 22, "ssh-ed25519", KeyB);
        Assert.Null(store.GetMismatch("db", 22));
    }

    [Fact]
    public async Task ManualKeys_AreHonoured_AndDecideAloneForTheHostsTheyList()
    {
        File.WriteAllText(Manual, $"edge.test,edge-alias ssh-ed25519 {Convert.ToBase64String(KeyA)}\n[edge.test]:2222 ssh-ed25519 {Convert.ToBase64String(KeyA)}\n");
        var store = Store();

        Assert.Equal(HostKeyDecision.Trusted, store.Check("edge.test", 22, "ssh-ed25519", KeyA));
        Assert.Equal(HostKeyDecision.Trusted, store.Check("edge.test", 2222, "ssh-ed25519", KeyA));
        Assert.Equal(HostKeyDecision.Mismatch, store.Check("edge.test", 22, "ssh-ed25519", KeyB)); // no TOFU for a listed host
        Assert.Equal(HostKeyDecision.FirstUse, store.Check("edge.test", 2200, "ssh-ed25519", KeyA)); // another port is another host
        var mismatch = store.GetMismatch("edge.test", 22)!;
        Assert.True(mismatch.StoredIsManual);
        Assert.True(Assert.Single(store.GetKeys("edge.test", 22)).Manual);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.AcceptPresentedKeyAsync("edge.test", 22, mismatch.PresentedFingerprint, CancellationToken.None));
        await store.RecordFirstUseAsync("edge.test", 22, "ssh-ed25519", KeyB, CancellationToken.None);
        Assert.False(File.Exists(Managed)); // a manually listed host is never recorded
    }

    [Fact]
    public async Task HashedNamesAndPatternsInTheOwnersFile_PinTheirHosts_SoNoneIsTrustedOnFirstUse()
    {
        static string Hashed(string name)
        {
            var salt = RandomNumberGenerator.GetBytes(20);
            return $"|1|{Convert.ToBase64String(salt)}|{Convert.ToBase64String(HMACSHA1.HashData(salt, Encoding.UTF8.GetBytes(name)))}";
        }

        var a = Convert.ToBase64String(KeyA);
        File.WriteAllText(Manual, string.Join('\n',
            $"{Hashed("hashed.test")} ssh-ed25519 {a}",
            $"{Hashed("[hashed.test]:2222")} ssh-ed25519 {a}",
            $"*.corp.test,!open.corp.test ssh-ed25519 {a}",
            $"db?.lab.test ssh-ed25519 {a}"));
        var store = Store();

        foreach (var (host, port) in new[] { ("hashed.test", 22), ("HASHED.test", 22), ("hashed.test", 2222), ("db.corp.test", 22), ("db1.lab.test", 22) })
        {
            Assert.Equal(HostKeyDecision.Trusted, store.Check(host, port, "ssh-ed25519", KeyA));
            Assert.Equal(HostKeyDecision.Mismatch, store.Check(host, port, "ssh-ed25519", KeyB)); // pinned: no trust on first use
            Assert.True(Assert.Single(store.GetKeys(host, port)).Manual);
        }

        // Not listed: another port, a negated name, a name the pattern does not cover.
        foreach (var (host, port) in new[] { ("hashed.test", 2200), ("open.corp.test", 22), ("corp.test", 22), ("db12.lab.test", 22), ("other.test", 22) })
            Assert.Equal(HostKeyDecision.FirstUse, store.Check(host, port, "ssh-ed25519", KeyA));

        await store.RecordFirstUseAsync("hashed.test", 22, "ssh-ed25519", KeyB, CancellationToken.None);
        Assert.False(File.Exists(Managed));
    }
}
