using System.Collections.Concurrent;
using System.Diagnostics;
using DevOpsAgent.Application.Abstractions;
using DevOpsAgent.Application.Agent;
using DevOpsAgent.Application.Servers;
using DevOpsAgent.Domain.Agent;
using DevOpsAgent.Domain.Servers;
using DevOpsAgent.Infrastructure.Persistence;
using DevOpsAgent.Infrastructure.Ssh;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Renci.SshNet.Common;

namespace DevOpsAgent.Tests;

/// <summary>
/// Hardening tests for T7.R27 (sudo password follow-ups) and T7.R29 (known_hosts follow-ups).
/// </summary>
public sealed class SudoAndKnownHostsHardeningTests : IDisposable
{
    private static readonly byte[] KeyA = Convert.FromBase64String("AAAAC3NzaC1lZDI1NTE5AAAAIAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA");
    private static readonly byte[] KeyB = Convert.FromBase64String("AAAAC3NzaC1lZDI1NTE5AAAAIBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB");
    private static readonly byte[] KeyC = Convert.FromBase64String("AAAAC3NzaC1lZDI1NTE5AAAAICCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCC");
    private readonly string _directory = Directory.CreateTempSubdirectory("dva-r27-r29-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); }
        catch (IOException) { }
    }

    // ---- T7.R27: Sudo password follow-ups ----

    [Fact]
    public async Task T7R27_1_AbandonedAttempt_PreventsDelayedTransportFromWritingPassword()
    {
        var marker = OutputMarker.Create();
        Assert.True(marker.AbandonInput());
        Assert.True(marker.IsInputAbandoned);
        Assert.False(marker.TryBeginSendInput());
        Assert.False(marker.WasInputSent);

        // End-to-end through SshConnectionPool: a transport blocked in channel open sees IsInputAbandoned after timeout.
        var releaseChannelOpen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var passwordWritten = false;
        var transport = new DelayedWriteTransport(releaseChannelOpen.Task, () => passwordWritten = true);
        var server = new Server("db", "10.0.60.1", 22, "dvapw", "key", "staging");
        var services = new ServiceCollection()
            .AddSingleton<IServerRepository>(new HangServers([server]))
            .BuildServiceProvider();
        using var pool = new SshConnectionPool(
            services.GetRequiredService<IServiceScopeFactory>(),
            new SingleTransportFactory(transport),
            new HostReachabilityTracker(TimeProvider.System),
            NullLogger<SshConnectionPool>.Instance);

        using var session = await pool.GetSessionAsync(server.Id, false, CancellationToken.None);
        var poolMarker = OutputMarker.Create();
        await Assert.ThrowsAsync<SshOperationTimeoutException>(() =>
            session.ExecuteAsync("sudo -S true", "secret-pw\n", poolMarker, TimeSpan.FromMilliseconds(100), CancellationToken.None));

        Assert.True(poolMarker.IsInputAbandoned);
        releaseChannelOpen.SetResult();
        await transport.Completed.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(passwordWritten, "delayed transport must not write the password once the pool abandoned the attempt");
    }

    [Fact]
    public async Task T7R27_2_LockoutMessageWithoutMarker_CountsAsRejected_AndNotRetriedByRetryNow()
    {
        await using var harness = await SudoHarness.CreateAsync("wrong-password-123");
        harness.Session.Handler = (_, _, _) =>
            new RemoteCommandResult(1, string.Empty, "sudo: account validation failure, is your account locked?\n", TimeSpan.FromMilliseconds(5));

        var first = await harness.Runner.RunAsync(harness.ServerId, "ss -tulpnH", new SshRunOptions(TimeSpan.FromSeconds(30), UseSudo: true), CancellationToken.None);
        Assert.Equal(1, first.ExitCode);

        var server = await harness.GetServerAsync();
        Assert.NotNull(server.SudoPasswordRejectedAtUtc);
        Assert.Null(server.SudoPasswordOutcomeUnknownAtUtc);
        Assert.False(server.CanUseSudo);

        // Retry now / Test connection must skip the sudo probe (never resend a rejected password).
        var capability = new ServerCapabilityService(harness.Runner, harness.Repository, TimeProvider.System);
        var test = await capability.TestAsync(harness.ServerId, CancellationToken.None);
        Assert.True(test.SudoProbeSkipped);
        Assert.Equal(1, harness.Session.SudoCalls);
    }

    [Fact]
    public async Task T7R27_3_TransientRejectionSaveFailure_RetriesUntilPersisted_SurvivesRestartAsRejected()
    {
        await using var harness = await SudoHarness.CreateAsync("wrong-password-456", failRejectionSaves: 2);
        harness.Session.Handler = (_, _, _) =>
            new RemoteCommandResult(1, string.Empty, "Sorry, try again.\n", TimeSpan.FromMilliseconds(5));

        await harness.Runner.RunAsync(harness.ServerId, "ss -tulpnH", new SshRunOptions(TimeSpan.FromSeconds(30), UseSudo: true), CancellationToken.None);

        // Transient save failures (2) were retried before returning, so SudoPasswordRejectedAtUtc IS persisted.
        var server = await harness.GetServerAsync();
        Assert.NotNull(server.SudoPasswordRejectedAtUtc);
        Assert.Null(server.SudoPasswordOutcomeUnknownAtUtc);
        Assert.Equal(3, harness.RejectionSaveAttempts);

        // Simulate app restart with a fresh SudoPasswordGate over the same database: still Rejected, Retry now sends nothing.
        var restartedGate = harness.CreateFreshGate();
        var restartedRunner = new SshRunner(harness.Pool, restartedGate);
        var probe = await restartedRunner.ProbeSudoAsync(harness.ServerId, new SshRunOptions(TimeSpan.FromSeconds(30)), CancellationToken.None);
        Assert.Equal(SudoPasswordGate.PausedMessage, probe.StandardError);
        Assert.Equal(1, harness.Session.SudoCalls);
    }

    [Fact]
    public async Task T7R27_4_RealBinShPreamble_DetectsConsumedVsUnreadStdinWithoutLeakingPassword()
    {
        if (OperatingSystem.IsWindows()) return;

        // Case 1: sudo consumed the password -> sh sees empty stdin -> checked marker.
        var checkedMarker = OutputMarker.Create();
        var checkedOutput = await RunShAsync(RemoteCommand.SudoPasswordPreamble(checkedMarker.Value) + "printf 'body-ok'", stdin: string.Empty);
        var strippedChecked = checkedMarker.Strip(checkedOutput);
        Assert.Equal("body-ok", strippedChecked);
        Assert.True(checkedMarker.IsSeen);
        Assert.True(checkedMarker.PasswordChecked);

        // Case 2: NOPASSWD / cached ticket did NOT consume stdin -> password line is still on stdin -> unchecked marker, no leak.
        const string secret = "super-secret-nopasswd-canary";
        var uncheckedMarker = OutputMarker.Create();
        var uncheckedOutput = await RunShAsync(RemoteCommand.SudoPasswordPreamble(uncheckedMarker.Value) + "printf 'body-ok'", stdin: secret + "\n");
        var strippedUnchecked = uncheckedMarker.Strip(uncheckedOutput);
        Assert.Equal("body-ok", strippedUnchecked);
        Assert.True(uncheckedMarker.IsSeen);
        Assert.False(uncheckedMarker.PasswordChecked);
        Assert.DoesNotContain(secret, uncheckedOutput, StringComparison.Ordinal);
    }

    [Fact]
    public async Task T7R27_4_NopasswdOrCachedTicket_ReportsPasswordNotChecked_AndDoesNotConfirmOrResolveAlert()
    {
        await using var harness = await SudoHarness.CreateAsync("untested-pw-789");
        // Seed an open sudo-auth-failed alert (as after an earlier failure + password edit) and untested SudoCapable=null.
        await harness.ExecuteSqlAsync("UPDATE Servers SET SudoAuthFailedSinceUtc = '2026-11-16 08:00:00+00:00', SudoCapable = NULL");

        // Fake session simulates NOPASSWD/cached ticket emitting the -unchecked marker.
        harness.Session.Handler = (_, _, marker) =>
        {
            var raw = marker!.UncheckedValue + "\nok";
            var stripped = marker.Strip(raw);
            return new RemoteCommandResult(0, stripped, string.Empty, TimeSpan.FromMilliseconds(5));
        };

        var capability = new ServerCapabilityService(harness.Runner, harness.Repository, TimeProvider.System);
        var result = await capability.TestAsync(harness.ServerId, CancellationToken.None);

        Assert.True(result.Reachable);
        Assert.True(result.SudoPasswordNotChecked);
        Assert.Null(result.SudoCapable); // never flips untested password to confirmed
        var server = await harness.GetServerAsync();
        Assert.NotNull(server.SudoAuthFailedSinceUtc); // alert stays open until password is actually verified
        Assert.Null(server.SudoPasswordAttemptStartedAtUtc); // attempt marker cleanly cleared
    }

    [Fact]
    public void T7R27_6_OverlappingSecrets_NeverLeakFragments()
    {
        Assert.Equal("pre [REDACTED] post", KnownSecretRedaction.Redact("pre abcdefghijk post", ["abcdefgh", "fghijk"]));
        Assert.Equal("x [REDACTED] y", KnownSecretRedaction.Redact("x abcdefghijklm y", ["abcdef", "defghi", "ghijklm"]));
        Assert.Equal("[REDACTED]", KnownSecretRedaction.Redact("abcdefgh", ["abcdefgh", "cde"]));
        Assert.Equal("[REDACTED][REDACTED]", KnownSecretRedaction.Redact("secret1secret2", ["secret1", "secret2"]));
    }

    [Fact]
    public async Task T7R27_6_ActionExecutionService_RedactsExceptionMessageAndSshRunnerDiRequiresGate()
    {
        // DI constructor rejects null SudoPasswordGate.
        Assert.Throws<ArgumentNullException>(() => new SshRunner(new RecordingSshConnectionPool(), sudoPasswords: null!));

        // ActionExecutionService redacts stored sudo passwords from exception messages.
        const string storedSecret = "ActionErrSecret99!";
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var _ = connection;
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        await using var context = new AppDbContext(options);
        await context.Database.EnsureCreatedAsync();

        var server = new Server("srv", "10.0.60.2", 22, "dva", "key", "staging");
        context.Servers.Add(server);
        var action = new ActionRecord("restart_service", "test", $"[\"{server.Id}\"]", "{\"unit\":\"nginx.service\"}", null, DateTimeOffset.UtcNow);
        context.ActionRecords.Add(action);
        await context.SaveChangesAsync();

        var modes = new ExecutionModeService(new AppSettingRepository(context), new ServerRepository(context), NullLogger<ExecutionModeService>.Instance);
        await modes.SaveAsync(new ExecutionModePolicy("staging", ExecutionMode.Confirm), CancellationToken.None);
        var registry = new ToolRegistry();
        var service = new ActionExecutionService(
            new ActionRecordRepository(context),
            new ServerRepository(context),
            registry,
            modes,
            new ToolCommandBuilder(registry),
            new ThrowingSecretRunner(storedSecret),
            TestBanGuard.Create(),
            TimeProvider.System,
            knownSecrets: new StaticKnownSecrets([storedSecret]));

        var completed = await service.ApproveAndRunAsync(action.Id, "owner", CancellationToken.None);
        Assert.Equal(ActionRecordStatus.Failed, completed.Status);
        Assert.DoesNotContain(storedSecret, completed.ResultsJson, StringComparison.Ordinal);
        Assert.Contains(KnownSecretRedaction.Redacted, completed.ResultsJson, StringComparison.Ordinal);
    }

    // ---- T7.R29: known_hosts follow-ups ----

    [Fact]
    public async Task T7R29_1_RevokedKeyInOwnerFile_IsRefusedEvenIfUnlistedOrAlsoListedNormally()
    {
        var manual = Path.Combine(_directory, "revoked_known_hosts");
        var managed = Path.Combine(_directory, "revoked_managed_known_hosts");
        var a = Convert.ToBase64String(KeyA);
        var b = Convert.ToBase64String(KeyB);
        File.WriteAllText(manual, string.Join('\n',
            $"@revoked host-a.test ssh-ed25519 {a}",
            $"host-a.test ssh-ed25519 {a}", // even if also listed normally, @revoked wins
            $"@revoked host-b.test ssh-ed25519 {b}",
            $"@cert-authority *.ca.test ssh-ed25519 {a}"));
        var store = CreateStore(manual, managed);

        Assert.Equal(HostKeyDecision.Mismatch, store.Check("host-a.test", 22, "ssh-ed25519", KeyA));
        var mismatchA = store.GetMismatch("host-a.test", 22)!;
        Assert.True(mismatchA.Revoked);
        Assert.True(mismatchA.StoredIsManual);
        Assert.Throws<InvalidOperationException>(() => store.IssueAcceptToken("host-a.test", 22, mismatchA.PresentedFingerprint));

        // Even when no normal pin exists for host-b.test, @revoked KeyB is refused (not FirstUse).
        Assert.Equal(HostKeyDecision.Mismatch, store.Check("host-b.test", 22, "ssh-ed25519", KeyB));
        Assert.True(store.GetMismatch("host-b.test", 22)!.Revoked);
        await store.RecordFirstUseAsync("host-b.test", 22, "ssh-ed25519", KeyB, CancellationToken.None);
        Assert.False(File.Exists(managed));
    }

    [Fact]
    public async Task T7R29_2_MissingOrUnreadableOwnerKnownHosts_LogsErrorAndRefusesFirstUse()
    {
        var missingManual = Path.Combine(_directory, "does-not-exist-known_hosts");
        var managed = Path.Combine(_directory, "missing_managed_known_hosts");
        var logger = new ListLogger<ManagedKnownHosts>();
        var store = CreateStore(missingManual, managed, logger: logger);

        Assert.Equal(HostKeyDecision.Mismatch, store.Check("new-host.test", 22, "ssh-ed25519", KeyA));
        Assert.True(store.GetMismatch("new-host.test", 22)!.StoredIsManual);
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Error && e.Message.Contains("missing or unreadable", StringComparison.Ordinal));
        await store.RecordFirstUseAsync("new-host.test", 22, "ssh-ed25519", KeyA, CancellationToken.None);
        Assert.False(File.Exists(managed));

        // A directory path is unreadable as a file: also refuses first use.
        var dirAsFile = Directory.CreateDirectory(Path.Combine(_directory, "dir-not-file")).FullName;
        var dirStore = CreateStore(dirAsFile, managed);
        Assert.Equal(HostKeyDecision.Mismatch, dirStore.Check("dir-host.test", 22, "ssh-ed25519", KeyA));

        // When Ssh:KnownHostsFile is intentionally unconfigured (empty), TOFU still works.
        var openStore = CreateStore(string.Empty, managed);
        Assert.Equal(HostKeyDecision.FirstUse, openStore.Check("open-host.test", 22, "ssh-ed25519", KeyA));
    }

    [Fact]
    public async Task T7R29_3_WriteManaged_FlushesAtomicallyAndLeavesNoTempFile()
    {
        var manual = Path.Combine(_directory, "empty_known_hosts");
        var managed = Path.Combine(_directory, "flushed_known_hosts");
        File.WriteAllText(manual, string.Empty);
        var store = CreateStore(manual, managed);

        await store.RecordFirstUseAsync("flush.test", 22, "ssh-ed25519", KeyA, CancellationToken.None);

        Assert.True(File.Exists(managed));
        Assert.False(File.Exists(managed + ".tmp"));
        Assert.Equal(HostKeyDecision.Trusted, store.Check("flush.test", 22, "ssh-ed25519", KeyA));
    }

    [Fact]
    public async Task T7R29_4_AcceptPresentedKey_RequiresValidOneTimeConfirmationToken()
    {
        var clock = new ManualClock();
        var manual = Path.Combine(_directory, "token_manual");
        var managed = Path.Combine(_directory, "token_managed");
        File.WriteAllText(manual, string.Empty);
        var store = CreateStore(manual, managed, clock);

        await store.RecordFirstUseAsync("tok.test", 22, "ssh-ed25519", KeyA, CancellationToken.None);
        Assert.Equal(HostKeyDecision.Mismatch, store.Check("tok.test", 22, "ssh-ed25519", KeyB));
        var fpB = ManagedKnownHosts.Fingerprint(KeyB);

        // Missing or wrong token rejected.
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.AcceptPresentedKeyAsync("tok.test", 22, fpB, "", CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.AcceptPresentedKeyAsync("tok.test", 22, fpB, "wrong-token", CancellationToken.None));

        // Expired token rejected.
        var expired = store.IssueAcceptToken("tok.test", 22, fpB);
        clock.Advance(TimeSpan.FromMinutes(6));
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.AcceptPresentedKeyAsync("tok.test", 22, fpB, expired, CancellationToken.None));

        // Valid token succeeds once and cannot be replayed.
        var valid = store.IssueAcceptToken("tok.test", 22, fpB);
        await store.AcceptPresentedKeyAsync("tok.test", 22, fpB, valid, CancellationToken.None);
        Assert.Equal(HostKeyDecision.Trusted, store.Check("tok.test", 22, "ssh-ed25519", KeyB));

        // Re-trigger mismatch and try replaying the already-consumed token: rejected.
        Assert.Equal(HostKeyDecision.Mismatch, store.Check("tok.test", 22, "ssh-ed25519", KeyC));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.AcceptPresentedKeyAsync("tok.test", 22, ManagedKnownHosts.Fingerprint(KeyC), valid, CancellationToken.None));
    }

    [Fact]
    public async Task T7R29_5_AcceptHostKeyAndTest_ClearsBackoffForAllInventoryEntriesSharingHostAndPort()
    {
        var manual = Path.Combine(_directory, "multi_manual");
        var managed = Path.Combine(_directory, "multi_managed");
        File.WriteAllText(manual, string.Empty);
        var store = CreateStore(manual, managed);
        await store.RecordFirstUseAsync("10.88.20.30", 22, "ssh-ed25519", KeyA, CancellationToken.None);
        Assert.Equal(HostKeyDecision.Mismatch, store.Check("10.88.20.30", 22, "ssh-ed25519", KeyB));

        var clock = new ManualClock();
        using var lab = new HangLab(clock);
        var dbPrimary = lab.Add("db-primary", "10.88.20.30");
        var dbSecondary = lab.Add("db-secondary", "10.88.20.30");
        var otherHost = lab.Add("other", "10.88.20.99");

        // All three are in host-key-mismatch backoff.
        var mismatchEx = new SshHostKeyMismatchException("10.88.20.30", 22, "fp");
        lab.Reachability.RecordFailure(dbPrimary.Id, mismatchEx);
        lab.Reachability.RecordFailure(dbSecondary.Id, mismatchEx);
        lab.Reachability.RecordFailure(otherHost.Id, new SshHostKeyMismatchException("10.88.20.99", 22, "other"));

        var token = store.IssueAcceptToken("10.88.20.30", 22, ManagedKnownHosts.Fingerprint(KeyB));
        var capability = new ServerCapabilityService(lab.Runner, lab.Servers, clock);
        var result = await capability.AcceptHostKeyAndTestAsync(
            dbPrimary.Id, ManagedKnownHosts.Fingerprint(KeyB), token, store, lab.Pool, CancellationToken.None);

        Assert.True(result.Reachable);
        // Both inventory entries sharing 10.88.20.30:22 are cleared immediately; unrelated host stays in backoff.
        Assert.Null(lab.Reachability.Get(dbPrimary.Id));
        Assert.Null(lab.Reachability.Get(dbSecondary.Id));
        Assert.NotNull(lab.Reachability.Get(otherHost.Id));
    }

    [Fact]
    public async Task T7R29_6_TargetBehindJumpHostWithHostKeyMismatch_ReportsJumpHostMismatch()
    {
        using var lab = new HangLab();
        var edge = lab.Add("edge", "10.0.70.1", script => script.ConnectFails = () => new SshHostKeyMismatchException("10.0.70.1", 22, "newfp"));
        var app = lab.Add("app", "10.0.70.2", jump: edge);

        var thrown = await Assert.ThrowsAsync<HostUnreachableException>(() => lab.Run(app, TimeSpan.FromSeconds(5)));

        Assert.Contains("app via jump host edge: host key mismatch on edge", thrown.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("could not connect", thrown.Message, StringComparison.Ordinal);

        var status = ServerReachability.Status(app.Id, lab.Map(), lab.Reachability)!;
        Assert.Equal(ServerReachabilityStatus.HostKeyMismatchBadge, status.Badge);
        Assert.Contains("host key mismatch on edge", status.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task T7R29_7_DualKeyTypeHost_KeepsOneManagedEntryPerKeyTypeWithoutFlipFlop()
    {
        var manual = Path.Combine(_directory, "dual_manual");
        var managed = Path.Combine(_directory, "dual_managed");
        File.WriteAllText(manual, string.Empty);
        var store = CreateStore(manual, managed);

        // First use records ssh-ed25519 KeyA.
        await store.RecordFirstUseAsync("dual.test", 22, "ssh-ed25519", KeyA, CancellationToken.None);
        Assert.Equal(HostKeyDecision.Trusted, store.Check("dual.test", 22, "ssh-ed25519", KeyA));

        // Host next negotiates ecdsa-sha2-nistp256 KeyB -> Mismatch -> owner accepts it.
        Assert.Equal(HostKeyDecision.Mismatch, store.Check("dual.test", 22, "ecdsa-sha2-nistp256", KeyB));
        var tokenB = store.IssueAcceptToken("dual.test", 22, ManagedKnownHosts.Fingerprint(KeyB));
        await store.AcceptPresentedKeyAsync("dual.test", 22, ManagedKnownHosts.Fingerprint(KeyB), tokenB, CancellationToken.None);

        // BOTH key types are now trusted simultaneously (no flip-flop).
        Assert.Equal(HostKeyDecision.Trusted, store.Check("dual.test", 22, "ssh-ed25519", KeyA));
        Assert.Equal(HostKeyDecision.Trusted, store.Check("dual.test", 22, "ecdsa-sha2-nistp256", KeyB));
        Assert.Equal(2, store.GetKeys("dual.test", 22).Count);

        // Rotating ssh-ed25519 to KeyC replaces ONLY the ssh-ed25519 entry while preserving ecdsa-sha2-nistp256 KeyB.
        Assert.Equal(HostKeyDecision.Mismatch, store.Check("dual.test", 22, "ssh-ed25519", KeyC));
        var tokenC = store.IssueAcceptToken("dual.test", 22, ManagedKnownHosts.Fingerprint(KeyC));
        await store.AcceptPresentedKeyAsync("dual.test", 22, ManagedKnownHosts.Fingerprint(KeyC), tokenC, CancellationToken.None);

        Assert.Equal(HostKeyDecision.Trusted, store.Check("dual.test", 22, "ssh-ed25519", KeyC));
        Assert.Equal(HostKeyDecision.Trusted, store.Check("dual.test", 22, "ecdsa-sha2-nistp256", KeyB));
        Assert.Equal(HostKeyDecision.Mismatch, store.Check("dual.test", 22, "ssh-ed25519", KeyA));
        Assert.Equal(2, store.GetKeys("dual.test", 22).Count);
    }

    // ---- Helpers ----

    private ManagedKnownHosts CreateStore(string manual, string managed, TimeProvider? clock = null, ILogger<ManagedKnownHosts>? logger = null) =>
        new(Options.Create(new SshConnectionOptions { KnownHostsFile = manual, ManagedKnownHostsFile = managed }),
            clock ?? TimeProvider.System,
            logger ?? NullLogger<ManagedKnownHosts>.Instance);

    private static async Task<string> RunShAsync(string script, string stdin)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo("/bin/sh")
            {
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            }
        };
        process.StartInfo.ArgumentList.Add("-c");
        process.StartInfo.ArgumentList.Add(script);
        process.Start();
        await process.StandardInput.WriteAsync(stdin);
        process.StandardInput.Close();
        var stdout = await process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync();
        Assert.Equal(0, process.ExitCode);
        return stdout;
    }

    private sealed class DelayedWriteTransport(Task channelOpenGate, Action onWrite) : ISshTransport
    {
        private readonly TaskCompletionSource _completed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task Completed => _completed.Task;
        public bool IsConnected => true;

        public Task<RemoteCommandResult> ExecuteAsync(string command, TimeSpan timeout, CancellationToken cancellationToken) =>
            ExecuteAsync(command, null, null, timeout, cancellationToken);

        public async Task<RemoteCommandResult> ExecuteAsync(string command, string? standardInput, OutputMarker? marker, TimeSpan timeout, CancellationToken cancellationToken)
        {
            try
            {
                // Simulates synchronous channel open blocking past the pool's timeout.
                await channelOpenGate;
                if (standardInput is not null && (marker is null || marker.TryBeginSendInput()))
                {
                    onWrite();
                    marker?.MarkInputSent();
                }

                return new RemoteCommandResult(0, string.Empty, string.Empty, TimeSpan.Zero);
            }
            finally
            {
                _completed.TrySetResult();
            }
        }

        public void Dispose() { }
    }

    private sealed class SingleTransportFactory(ISshTransport transport) : ISshTransportFactory
    {
        public Task<ISshTransport> ConnectAsync(Server server, ISshTransport? jump, CancellationToken cancellationToken) =>
            Task.FromResult(transport);
    }

    private sealed class ThrowingSecretRunner(string secret) : ISshRunner
    {
        public Task<RemoteCommandResult> RunAsync(Guid serverId, string command, SshRunOptions options, CancellationToken cancellationToken) =>
            throw new InvalidOperationException($"Remote failed while processing {secret}");
    }

    private sealed class StaticKnownSecrets(IReadOnlyList<string> secrets) : IKnownSecrets
    {
        public Task<IReadOnlyList<string>> GetAsync(CancellationToken cancellationToken) => Task.FromResult(secrets);
        public void Invalidate() { }
    }

    private sealed class ListLogger<T> : ILogger<T>
    {
        public ConcurrentQueue<(LogLevel Level, string Message)> Entries { get; } = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entries.Enqueue((logLevel, formatter(state, exception)));
    }

    private sealed class SudoHarness : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;
        private readonly ServiceProvider _services;
        private readonly TestProtector _protector = new();
        private readonly FlakyRejectionState _flakyState;

        private SudoHarness(SqliteConnection connection, ServiceProvider services, Guid serverId,
            ScriptedSudoSession session, SudoPasswordGate gate, FlakyRejectionState flakyState)
        {
            _connection = connection;
            _services = services;
            ServerId = serverId;
            Session = session;
            Pool = new StubPool(session);
            Gate = gate;
            Runner = new SshRunner(Pool, gate);
            _flakyState = flakyState;
        }

        public Guid ServerId { get; }
        public ScriptedSudoSession Session { get; }
        public ISshConnectionPool Pool { get; }
        public SudoPasswordGate Gate { get; }
        public SshRunner Runner { get; }
        public int RejectionSaveAttempts => Volatile.Read(ref _flakyState.Attempts);
        public IServerRepository Repository => new ServerRepository(NewContext());

        public static async Task<SudoHarness> CreateAsync(string password, int failRejectionSaves = 0)
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var flaky = new FlakyRejectionState(failRejectionSaves);
            var protector = new TestProtector();
            var services = new ServiceCollection()
                .AddScoped(_ => new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options))
                .AddScoped<IServerRepository>(sp => new FlakyRejectionServerRepository(
                    new ServerRepository(sp.GetRequiredService<AppDbContext>()), flaky))
                .BuildServiceProvider();

            await using (var scope = services.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                await db.Database.EnsureCreatedAsync();
                var server = new Server("db", "10.0.80.1", 22, "dvapw", "key", "staging");
                server.RecordCapabilities(true, "Debian 12", DateTimeOffset.UtcNow);
                server.SetSudoPassword(protector.Protect(password));
                db.Servers.Add(server);
                await db.SaveChangesAsync();

                var session = new ScriptedSudoSession();
                var gate = new SudoPasswordGate(services.GetRequiredService<IServiceScopeFactory>(), protector,
                    TimeProvider.System, NullLogger<SudoPasswordGate>.Instance)
                {
                    SaveRetryBaseDelay = TimeSpan.FromMilliseconds(5)
                };
                return new SudoHarness(connection, services, server.Id, session, gate, flaky);
            }
        }

        public SudoPasswordGate CreateFreshGate() =>
            new(_services.GetRequiredService<IServiceScopeFactory>(), _protector, TimeProvider.System, NullLogger<SudoPasswordGate>.Instance)
            {
                SaveRetryBaseDelay = TimeSpan.FromMilliseconds(5)
            };

        public async Task<Server> GetServerAsync()
        {
            await using var context = NewContext();
            return await context.Servers.AsNoTracking().SingleAsync(s => s.Id == ServerId);
        }

        public async Task ExecuteSqlAsync(string sql)
        {
            await using var context = NewContext();
            await context.Database.ExecuteSqlRawAsync(sql);
        }

        private AppDbContext NewContext() =>
            new(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options);

        public async ValueTask DisposeAsync()
        {
            await _services.DisposeAsync();
            await _connection.DisposeAsync();
        }
    }

    private sealed class FlakyRejectionState(int failCount)
    {
        public int FailCount { get; } = failCount;
        public int Attempts;
    }

    private sealed class FlakyRejectionServerRepository(IServerRepository inner, FlakyRejectionState state) : IServerRepository
    {
        public Task<IReadOnlyList<Server>> ListAsync(CancellationToken cancellationToken) => inner.ListAsync(cancellationToken);
        public Task<Server?> GetAsync(Guid id, CancellationToken cancellationToken) => inner.GetAsync(id, cancellationToken);
        public Task AddAsync(Server server, CancellationToken cancellationToken) => inner.AddAsync(server, cancellationToken);
        public Task DeleteAsync(Server server, CancellationToken cancellationToken) => inner.DeleteAsync(server, cancellationToken);

        public Task SaveAsync(Server server, CancellationToken cancellationToken)
        {
            if (server.SudoPasswordRejectedAtUtc is not null)
            {
                var attempt = Interlocked.Increment(ref state.Attempts);
                if (attempt <= state.FailCount)
                {
                    throw new IOException("simulated transient sqlite lock");
                }
            }

            return inner.SaveAsync(server, cancellationToken);
        }
    }

    private sealed class ScriptedSudoSession : ISshCommandSession
    {
        private int _sudoCalls;
        public int SudoCalls => Volatile.Read(ref _sudoCalls);
        public Func<string, string?, OutputMarker?, RemoteCommandResult>? Handler { get; set; }
        public bool IsConnected => true;
        public void Dispose() { }

        public Task<RemoteCommandResult> ExecuteAsync(string command, TimeSpan timeout, CancellationToken cancellationToken) =>
            ExecuteAsync(command, null, null, timeout, cancellationToken);

        public Task<RemoteCommandResult> ExecuteAsync(string command, string? standardInput, TimeSpan timeout, CancellationToken cancellationToken) =>
            ExecuteAsync(command, standardInput, null, timeout, cancellationToken);

        public Task<RemoteCommandResult> ExecuteAsync(string command, string? standardInput, OutputMarker? marker, TimeSpan timeout, CancellationToken cancellationToken)
        {
            if (standardInput is not null)
            {
                Interlocked.Increment(ref _sudoCalls);
                if (Handler is not null) return Task.FromResult(Handler(command, standardInput, marker));
            }

            return Task.FromResult(new RemoteCommandResult(0, "NAME=\"Debian\"\nVERSION_ID=\"12\"\n", string.Empty, TimeSpan.FromMilliseconds(2)));
        }
    }

    private sealed class StubPool(ISshCommandSession session) : ISshConnectionPool
    {
        public Task<ISshCommandSession> GetSessionAsync(Guid serverId, bool bypassBackoff, CancellationToken cancellationToken) =>
            Task.FromResult(session);
        public void Evict(Guid serverId) { }
        public void Dispose() { }
    }

    private sealed class TestProtector : ISecretProtector
    {
        public string Protect(string plaintext) => "prot:" + plaintext;
        public string Unprotect(string protectedValue) => protectedValue["prot:".Length..];
    }
}
