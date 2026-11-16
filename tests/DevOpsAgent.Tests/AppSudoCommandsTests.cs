using System.Text.Json;
using DevOpsAgent.Application.Abstractions;
using DevOpsAgent.Application.Agent;
using DevOpsAgent.Application.Discovery;
using DevOpsAgent.Domain.Agent;
using DevOpsAgent.Domain.Servers;
using DevOpsAgent.Infrastructure.Collectors;
using DevOpsAgent.Infrastructure.Monitoring;
using Microsoft.Extensions.Options;

namespace DevOpsAgent.Tests;

public sealed class AppSudoCommandsTests
{
    [Fact]
    public async Task EverySudoCommandTheAppSendsIsOnTheList()
    {
        var commands = await CaptureAppSudoCommandsAsync();

        Assert.True(commands.Count >= 14, $"Only {commands.Count} sudo commands were captured.");
        Assert.All(commands, command => Assert.True(AppSudoCommands.IsAppCommand(SudoLogged(command)), command));
    }

    [Theory]
    [InlineData("ss -tulpnH; id")]
    [InlineData("ss -tulpnH\nid")]
    [InlineData("docker inspect 0123456789ab; rm -rf /")]
    [InlineData("docker inspect 0123456789ab $(id)")]
    [InlineData("nsenter -t 42 -n sh")]
    [InlineData("nsenter -t 42 -n ss -tnH state established; cat /etc/shadow")]
    [InlineData("cat /etc/shadow")]
    [InlineData("journalctl -u ssh -u sshd --since '@1' --no-pager; printf '__DVA_BAN_TOOLS__ __DVA_BAN_STATE__'")]
    public void OtherCommandsAreNotOnTheList(string command)
    {
        Assert.False(AppSudoCommands.IsAppCommand(SudoLogged(command)));
    }

    [Fact]
    public void EditConfigCommandsAreExemptOnlyForAnNginxPathAndTheActionsOwnStagingFile()
    {
        var rule = ConfigServiceRule.Nginx;
        var staged = ConfigEditService.StagingDirectory(Guid.NewGuid()) + "/content";
        var hash = new string('a', 64);
        string Apply(string path, string stagedFile) => ConfigEditService.ApplyCommand(path, stagedFile, 1763164800, hash, rule);
        bool Exempt(string command) => AppSudoCommands.IsAppCommand(SudoLogged(command));

        Assert.True(Exempt(Apply("/etc/nginx/conf.d/demo.conf", staged)));
        Assert.True(Exempt(Apply("/etc/nginx/conf.d/a..b.conf", staged))); // ".." inside a name is not a segment
        Assert.True(Exempt(ConfigEditService.ReadCommand("/etc/nginx/nginx.conf", 262145)));
        Assert.True(Exempt(ConfigEditService.RollbackCommand("/etc/nginx/nginx.conf", 1763164800, rule)));

        // Anything else the same script could be pointed at is a root write or read the owner has to hear about.
        foreach (var path in new[] { "/etc/sudoers.d/x", "/etc/shadow", "/etc/nginx/../shadow", "/etc/nginx/conf.d/../../shadow", "/etc/nginx/..", "/etc/nginx/'x", "/etc/nginxfoo/x.conf" })
        {
            Assert.False(Exempt(Apply(path, staged)), path);
            Assert.False(Exempt(ConfigEditService.ReadCommand(path, 262145)), path);
            Assert.False(Exempt(ConfigEditService.RollbackCommand(path, 1763164800, rule)), path);
        }

        foreach (var stagedFile in new[] { "/etc/shadow", "/tmp/dva-edit-abc/content", "/tmp/other/content", staged + "x", staged.Replace("/content", "/../../etc/shadow", StringComparison.Ordinal) })
            Assert.False(Exempt(Apply("/etc/nginx/conf.d/demo.conf", stagedFile)), stagedFile);
    }

    [Fact]
    public void ToolCommandsWithInjectedArgumentsAreNotOnTheList()
    {
        var builder = new ToolCommandBuilder(new ToolRegistry());
        var restart = builder.Build(Action("restart_service", new { unit = "nginx.service" })).Command;

        Assert.True(AppSudoCommands.IsAppCommand(SudoLogged(restart)));
        Assert.False(AppSudoCommands.IsAppCommand(SudoLogged(restart.Replace("'nginx.service'", "'nginx'; id; '", StringComparison.Ordinal))));
        Assert.False(AppSudoCommands.IsAppCommand(SudoLogged(restart + "; id")));
        Assert.False(AppSudoCommands.IsAppCommand("/usr/bin/sh -c 'ss -tulpnH' 'extra'"));
        Assert.False(AppSudoCommands.IsAppCommand("/usr/bin/ss -tulpnH"));
    }

    [Fact]
    public async Task ScannerExemptsTheAppUsersOwnCommandsIncludingSplitLinesAndAlertsOnAnythingElse()
    {
        var now = DateTimeOffset.UtcNow;
        var timestamp = now.AddSeconds(-30).ToString("yyyy-MM-dd'T'HH:mm:sszzz");
        var scanCommand = AuthLogScanner.BuildCommand(now.AddMinutes(-1));
        var server = new Server("edge", "edge.example", 22, "dva", "id_ed25519", "staging");
        string Log(string user, string command)
        {
            // Like sudo: about 1,000 characters per line, broken at the last space (which is dropped), continued on
            // "(command continued)" lines.
            var rest = SudoLogged(command);
            var lines = new List<string>();
            var prefix = $"{timestamp} host sudo:      {user} : PWD=/home/{user} ; USER=root ; COMMAND=";
            while (true)
            {
                var room = 1000 - prefix.Length;
                if (rest.Length <= room) { lines.Add(prefix + rest); break; }
                var cut = rest.LastIndexOf(' ', room);
                lines.Add(prefix + rest[..cut]);
                rest = rest[(cut + 1)..];
                prefix = $"{timestamp} host sudo:      {user} : (command continued) ";
            }
            return string.Join('\n', lines);
        }

        async Task<bool> AlertsFor(string log)
        {
            var scanner = new AuthLogScanner(new FixedRunner(log + "\n__DVA_BAN_TOOLS__\n__DVA_BAN_STATE__\n"), new MemorySettings(),
                Options.Create(new SecurityMonitoringOptions()), new FixedTime(now));
            return (await scanner.ScanAsync(server, CancellationToken.None)).Any(condition => condition.Rule == "ssh-unusual-sudo");
        }

        Assert.True(scanCommand.Length > 1000);
        Assert.False(await AlertsFor(Log("dva", scanCommand)));
        Assert.False(await AlertsFor(Log("dva", ListenerCollector.Command) + "\n" + Log("dva", NginxCollector.Command)));
        Assert.True(await AlertsFor(Log("mallory", scanCommand)));
        Assert.True(await AlertsFor(Log("dva", "cat /etc/shadow")));
        Assert.True(await AlertsFor(Log("dva", scanCommand + "; id")));
        Assert.True(await AlertsFor(Log("dva", scanCommand).Replace("(command continued) ", "(command continued) ; id ", StringComparison.Ordinal)));
    }

    [Fact]
    public void ScanCommandCapturedFromARealSudoLogIsRecognisedAcrossTheLineSplit()
    {
        // Captured on the Debian 12 lab (sudo 1.9.13): sudo broke the scan command at a space and dropped it.
        var log = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "auth-log-sudo-split.txt"));

        var sudo = Assert.Single(AuthLogParser.Parse(log, DateTimeOffset.Parse("2026-11-15T06:00:00Z", System.Globalization.CultureInfo.InvariantCulture)));

        Assert.Equal("dva", sudo.Username);
        Assert.True(AppSudoCommands.IsAppCommand(sudo.Command));
    }

    private static string SudoLogged(string command) =>
        "/usr/bin/sh -c '" + command.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("'", "\\'", StringComparison.Ordinal) + "'";

    private static ActionRecord Action(string tool, object args) =>
        new(tool, "staging", "[]", JsonSerializer.Serialize(args, new JsonSerializerOptions(JsonSerializerDefaults.Web)), null, DateTimeOffset.UtcNow);

    private static async Task<List<string>> CaptureAppSudoCommandsAsync()
    {
        var runner = new CapturingRunner();
        var server = new Server("srv", "srv.example", 22, "dva", "key", "staging");
        server.RecordCapabilities(true, "Debian GNU/Linux 12", DateTimeOffset.UtcNow);
        var context = new ServerContext(server, runner);
        await new ListenerCollector().CollectAsync(context, CancellationToken.None);
        await new NginxCollector().CollectAsync(context, CancellationToken.None);
        var docker = new DockerCollector();
        await docker.IsApplicableAsync(new ServerContext(server, new CapturingRunner(failUnprivilegedDocker: true, sink: runner)), CancellationToken.None);
        await docker.CollectAsync(context, CancellationToken.None);
        await new UpdatesCollector(new UpdateCollectorOptions()).CollectAsync(context, CancellationToken.None);
        await new AuthLogScanner(runner, new MemorySettings(), Options.Create(new SecurityMonitoringOptions()), TimeProvider.System)
            .ScanAsync(server, CancellationToken.None);
        var commands = runner.SudoCommands.ToList();
        commands.Add(ConnectionCollector.NamespaceCommand(4242));

        var tools = new ToolCommandBuilder(new ToolRegistry());
        commands.Add(tools.BuildReadOnly("disk_usage", new DiskUsageArgs()).Command);
        commands.Add(tools.BuildReadOnly("disk_usage", new DiskUsageArgs("/var/log")).Command);
        foreach (var action in new[]
                 {
                     Action("restart_service", new { unit = "nginx.service" }),
                     Action("restart_service", new { unit = "getty@tty1" }),
                     Action("apply_updates", new { packages = new[] { "openssl", "libc6", "linux-image-6.1.0-28-amd64" } }),
                     Action("block_ip", new { address = "203.0.113.9" }),
                     Action("block_ip", new { address = "2001:db8::9" }),
                     Action("unblock_ip", new { address = "203.0.113.9" }),
                     Action("unblock_ip", new { address = "2001:db8::9" }),
                 })
        {
            var command = tools.Build(action);
            Assert.True(command.UseSudo, action.ToolName);
            commands.Add(command.Command);
        }

        return commands;
    }

    private sealed class CapturingRunner(bool failUnprivilegedDocker = false, CapturingRunner? sink = null) : ISshRunner
    {
        public List<string> SudoCommands { get; } = [];

        public Task<RemoteCommandResult> RunAsync(Guid serverId, string command, SshRunOptions options, CancellationToken cancellationToken)
        {
            if (options.UseSudo) (sink ?? this).SudoCommands.Add(command);
            var exit = failUnprivilegedDocker && !options.UseSudo && command == DockerCollector.InfoCommand ? 1 : 0;
            var output = command switch
            {
                DockerCollector.ContainerListCommand => "0123456789ab\nfedcba987654\n",
                DockerCollector.NetworkListCommand => "abcdefabcdef\n",
                _ when command.StartsWith("docker", StringComparison.Ordinal) && command.Contains("inspect", StringComparison.Ordinal) => "[]",
                _ => string.Empty
            };
            return Task.FromResult(new RemoteCommandResult(exit, output, string.Empty, TimeSpan.FromMilliseconds(1)));
        }
    }

    private sealed class FixedRunner(string output) : ISshRunner
    {
        public Task<RemoteCommandResult> RunAsync(Guid serverId, string command, SshRunOptions options, CancellationToken cancellationToken) =>
            Task.FromResult(new RemoteCommandResult(0, output, string.Empty, TimeSpan.FromMilliseconds(1)));
    }

    private sealed class MemorySettings : IAppSettingRepository
    {
        private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);
        public Task<string?> GetAsync(string key, CancellationToken cancellationToken) => Task.FromResult(_values.GetValueOrDefault(key));
        public Task SetAsync(string key, string value, CancellationToken cancellationToken) { _values[key] = value; return Task.CompletedTask; }
    }

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
