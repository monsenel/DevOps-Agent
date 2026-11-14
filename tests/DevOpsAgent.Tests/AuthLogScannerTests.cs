using DevOpsAgent.Application.Abstractions;
using DevOpsAgent.Domain.Servers;
using DevOpsAgent.Infrastructure.Monitoring;
using Microsoft.Extensions.Options;

namespace DevOpsAgent.Tests;

public sealed class AuthLogScannerTests
{
    [Fact]
    public async Task Scan_DetectsFailedBurstNewIpAndSudoOutsideAllowlistAndPersistsCursor()
    {
        var now = DateTimeOffset.UtcNow;
        var time = new MutableTimeProvider(now);
        var timestamp = now.AddMinutes(-1).ToString("yyyy-MM-dd'T'HH:mm:sszzz");
        var failures = string.Join('\n', Enumerable.Range(0, 50).Select(index =>
            $"{timestamp} host sshd[{index}]: Failed password for attacker from 203.0.113.9 port {5000 + index} ssh2"));
        var output = failures + $"\n{timestamp} host sshd[200]: Accepted publickey for deploy from 198.51.100.7 port 22 ssh2"
            + $"\n{timestamp} host sudo[201]: rogue : TTY=pts/0 ; PWD=/tmp ; USER=root ; COMMAND=/bin/id"
            + $"\n{timestamp} host sudo[202]: deploy : TTY=pts/0 ; PWD=/tmp ; USER=root ; COMMAND=/bin/true"
            + "\n__DVA_BAN_TOOLS__\nnft\n__DVA_BAN_STATE__\nblocked address: 192.0.2.44\n";
        var runner = new FixtureSshRunner(output);
        var settings = new MemorySettings();
        var server = new Server("edge", "edge.example", 22, "deploy", "id_ed25519", "staging");
        var scanner = new AuthLogScanner(runner, settings,
            Options.Create(new SecurityMonitoringOptions { SudoAllowlist = ["deploy"] }), time);

        var conditions = await scanner.ScanAsync(server, CancellationToken.None);

        Assert.Equal(3, conditions.Count);
        var burst = Assert.Single(conditions, item => item.Rule == "ssh-failed-burst");
        Assert.Contains("50 failed logins", burst.Details, StringComparison.Ordinal);
        Assert.Contains("203.0.113.9", burst.Details, StringComparison.Ordinal);
        Assert.Contains("successful login: yes", burst.Details, StringComparison.Ordinal);
        Assert.Contains("nft", burst.Details, StringComparison.Ordinal);
        Assert.Contains("192.0.2.44", burst.Details, StringComparison.Ordinal);
        var newLogin = Assert.Single(conditions, item => item.Rule == "ssh-new-ip-login");
        Assert.Contains("username: deploy", newLogin.Details, StringComparison.Ordinal);
        Assert.Contains("successful login: yes", newLogin.Details, StringComparison.Ordinal);
        var sudo = Assert.Single(conditions, item => item.Rule == "ssh-unusual-sudo");
        Assert.Contains("rogue", sudo.Details, StringComparison.Ordinal);
        Assert.Contains("/bin/id", sudo.Details, StringComparison.Ordinal);
        Assert.Contains("journalctl", runner.LastCommand, StringComparison.Ordinal);
        Assert.NotNull(await settings.GetAsync($"security.auth-log.{server.Id:N}", CancellationToken.None));
    }

    [Fact]
    public async Task Scan_RetainsNewIpAlertAcrossCursorAdvanceThenExpiresAfterFiveMinutes()
    {
        var now = DateTimeOffset.UtcNow;
        var time = new MutableTimeProvider(now);
        var timestamp = now.AddMinutes(-1).ToString("yyyy-MM-dd'T'HH:mm:sszzz");
        var output = $"{timestamp} host sshd[1]: Accepted publickey for deploy from 198.51.100.7 port 22 ssh2"
            + "\n__DVA_BAN_TOOLS__\nfail2ban-client\n__DVA_BAN_STATE__\nsshd ban list empty\n";
        var runner = new FixtureSshRunner(output);
        var scanner = new AuthLogScanner(runner, new MemorySettings(),
            Options.Create(new SecurityMonitoringOptions()), time);
        var server = new Server("edge", "edge.example", 22, "deploy", "id_ed25519", "staging");

        Assert.Single(await scanner.ScanAsync(server, CancellationToken.None));
        Assert.Single(await scanner.ScanAsync(server, CancellationToken.None));
        time.Advance(TimeSpan.FromMinutes(6));
        Assert.Empty(await scanner.ScanAsync(server, CancellationToken.None));
    }

    [Fact]
    public async Task Scan_IgnoresDelimitersEmbeddedInSudoAuditLines()
    {
        var now = DateTimeOffset.UtcNow;
        var time = new MutableTimeProvider(now);
        var timestamp = now.AddMinutes(-1).ToString("yyyy-MM-dd'T'HH:mm:sszzz");
        var auditedCommand = $"{timestamp} host sudo[1]: dva : COMMAND=/bin/sh -c printf '__DVA_BAN_TOOLS__ __DVA_BAN_STATE__'";
        var failures = string.Join('\n', Enumerable.Range(0, 50).Select(index =>
            $"{timestamp} host sshd[{index}]: Invalid user admin{index} from 203.0.113.9 port {5000 + index}"));
        var output = auditedCommand + "\n" + failures
            + "\n__DVA_BAN_TOOLS__\nfail2ban-client\n__DVA_BAN_STATE__\nsshd currently banned: 1";
        var scanner = new AuthLogScanner(new FixtureSshRunner(output), new MemorySettings(),
            Options.Create(new SecurityMonitoringOptions()), time);
        var server = new Server("edge", "edge.example", 22, "deploy", "id_ed25519", "staging");

        var conditions = await scanner.ScanAsync(server, CancellationToken.None);
        var burst = Assert.Single(conditions, item => item.Rule == "ssh-failed-burst");

        Assert.Equal("ssh-failed-burst", burst.Rule);
        Assert.Contains("50 failed logins", burst.Details, StringComparison.Ordinal);
        Assert.Contains("fail2ban-client", burst.Details, StringComparison.Ordinal);
        Assert.Contains("currently banned: 1", burst.Details, StringComparison.Ordinal);
        Assert.Contains(conditions, item => item.Rule == "ssh-unusual-sudo");
    }

    [Fact]
    public async Task Scan_DoesNotAlertOnItsOwnSudoWrappedLogAndBanStateProbe()
    {
        var now = DateTimeOffset.UtcNow;
        var timestamp = now.AddMinutes(-1).ToString("yyyy-MM-dd'T'HH:mm:sszzz");
        var ownCommand = $"{timestamp} host sudo[1]: dva : TTY=unknown ; PWD=/home/dva ; USER=root ; COMMAND=/usr/bin/sh -c 'log_output=$(journalctl -u ssh -u sshd --since \'@1\'); printf __DVA_BAN_TOOLS__; printf __DVA_BAN_STATE__'";
        var output = ownCommand + "\n__DVA_BAN_TOOLS__\nfail2ban-client\n__DVA_BAN_STATE__\nno active bans";
        var scanner = new AuthLogScanner(new FixtureSshRunner(output), new MemorySettings(),
            Options.Create(new SecurityMonitoringOptions()), new MutableTimeProvider(now));
        var server = new Server("edge", "edge.example", 22, "dva", "id_ed25519", "staging");

        var conditions = await scanner.ScanAsync(server, CancellationToken.None);

        Assert.DoesNotContain(conditions, item => item.Rule == "ssh-unusual-sudo");
    }

    [Fact]
    public async Task ScanFailure_DoesNotImmediatelyResolveRecentSecurityAlert()
    {
        var now = DateTimeOffset.UtcNow;
        var time = new MutableTimeProvider(now);
        var timestamp = now.AddMinutes(-1).ToString("yyyy-MM-dd'T'HH:mm:sszzz");
        var output = $"{timestamp} host sshd[1]: Accepted publickey for deploy from 198.51.100.8 port 22 ssh2"
            + "\n__DVA_BAN_TOOLS__\nnft\n__DVA_BAN_STATE__\n";
        var runner = new FixtureSshRunner(output);
        var scanner = new AuthLogScanner(runner, new MemorySettings(),
            Options.Create(new SecurityMonitoringOptions()), time);
        var server = new Server("edge", "edge.example", 22, "deploy", "id_ed25519", "staging");

        var first = Assert.Single(await scanner.ScanAsync(server, CancellationToken.None));
        runner.Fail = true;
        var retry = Assert.Single(await scanner.ScanAsync(server, CancellationToken.None));

        Assert.Equal(first.Key, retry.Key);
        Assert.Contains("Scan unavailable", retry.Details, StringComparison.Ordinal);
    }

    private sealed class FixtureSshRunner(string output) : ISshRunner
    {
        public string LastCommand { get; private set; } = string.Empty;
        public bool Fail { get; set; }

        public Task<RemoteCommandResult> RunAsync(Guid serverId, string command, SshRunOptions options,
            CancellationToken cancellationToken)
        {
            LastCommand = command;
            if (Fail) throw new InvalidOperationException("fixture SSH failure");
            return Task.FromResult(new RemoteCommandResult(0, output, string.Empty, TimeSpan.FromMilliseconds(1)));
        }
    }

    private sealed class MemorySettings : IAppSettingRepository
    {
        private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);
        public Task<string?> GetAsync(string key, CancellationToken cancellationToken) =>
            Task.FromResult(_values.GetValueOrDefault(key));
        public Task SetAsync(string key, string value, CancellationToken cancellationToken)
        {
            _values[key] = value;
            return Task.CompletedTask;
        }
    }

    private sealed class MutableTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan elapsed) => _now += elapsed;
    }
}
