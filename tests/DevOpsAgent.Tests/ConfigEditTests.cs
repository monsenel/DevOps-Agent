using System.Diagnostics;
using System.Text.Json;
using DevOpsAgent.Application.Abstractions;
using DevOpsAgent.Application.Agent;
using DevOpsAgent.Application.Discovery;
using DevOpsAgent.Application.Monitoring;
using DevOpsAgent.Domain.Agent;
using DevOpsAgent.Domain.Discovery;
using DevOpsAgent.Domain.Graph;
using DevOpsAgent.Domain.Monitoring;
using DevOpsAgent.Domain.Servers;
using DevOpsAgent.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace DevOpsAgent.Tests;

/// <summary>T8.8: edit_config — allowlist, diffs, approval rules, and the apply/rollback steps (the scripts run for real in sh).</summary>
public sealed class ConfigEditTests
{
    private const string Demo = """
        upstream api_backend {
            server srv-app:5000;
            keepalive 8;
        }

        server {
            listen 80;
            server_name api.test.local;
            location / {
                proxy_pass http://api_backend;
            }
        }

        """;

    [Theory]
    [InlineData("/etc/nginx/conf.d/demo.conf", null)]
    [InlineData("/etc/nginx/nginx.conf", null)]
    [InlineData("/etc/passwd", "allowlisted")]
    [InlineData("/etc/nginx/../shadow", "plain absolute")]
    [InlineData("etc/nginx/conf.d/demo.conf", "absolute")]
    [InlineData("/etc/nginx/ssl/server.key", "private key")]
    [InlineData("/etc/nginx/conf.d/a b.conf", "characters")]
    [InlineData("/etc/nginx/conf.d/x';reboot;'.conf", "characters")]
    public void Paths_AreAllowlisted_AndTheDenyListStillApplies(string path, string? refusal)
    {
        var result = ConfigEditPolicy.Check(path, new ConfigEditOptions().AllowedPaths);
        if (refusal is null) Assert.Null(result);
        else Assert.Contains(refusal, result, StringComparison.Ordinal);
        Assert.True(ConfigEditPolicy.Matches("/etc/app/x.yml", "/etc/app/*.yml"));
        Assert.False(ConfigEditPolicy.Matches("/etc/app/sub/x.yml", "/etc/app/*.yml"));
    }

    [Fact]
    public void Diffs_ApplyExactly_AndAreCreatedForReview()
    {
        var updated = Demo.Replace("srv-app:5000", "srv-app:5999", StringComparison.Ordinal);
        var diff = UnifiedDiff.Create(Demo, updated, "/etc/nginx/conf.d/demo.conf");

        Assert.Contains("-    server srv-app:5000;\n+    server srv-app:5999;", diff); // removal first, as diff(1) prints it
        Assert.StartsWith("--- a/etc/nginx/conf.d/demo.conf\n+++ b/etc/nginx/conf.d/demo.conf\n@@ -1,5 +1,5 @@", diff);
        Assert.Equal(updated, UnifiedDiff.Apply(Demo, diff));
        var stale = Assert.Throws<ArgumentException>(() => UnifiedDiff.Apply(Demo.Replace("keepalive 8", "keepalive 16", StringComparison.Ordinal), diff));
        Assert.Contains("does not match the current file", stale.Message);
        Assert.Throws<ArgumentException>(() => UnifiedDiff.Apply(Demo, "not a diff"));
    }

    [Fact]
    public async Task Proposal_ReadsTheFile_StoresContentHashAndDiff_AndIsRefusedOnReadOnlyGroups()
    {
        await using var lab = await Lab.CreateAsync();
        var args = JsonSerializer.SerializeToElement(new { path = "/etc/nginx/conf.d/demo.conf", content = Demo.Replace("5000", "5001", StringComparison.Ordinal), serverIds = new[] { lab.Edge.Id } });

        var readOnly = await lab.Dispatcher.DispatchAsync("edit_config", args, null, default);
        Assert.Contains("read-only", readOnly.Summary);
        Assert.Empty(await lab.Context.ActionRecords.ToListAsync());

        await lab.ConfirmAsync();
        var proposed = await lab.Dispatcher.DispatchAsync("edit_config", args, null, default);
        Assert.True(proposed.NeedsApproval);
        Assert.Contains("-    server srv-app:5000;", proposed.Summary);
        Assert.Contains("+    server srv-app:5001;", proposed.Summary);
        var stored = JsonSerializer.Deserialize<EditConfigArgs>((await lab.Context.ActionRecords.SingleAsync()).ArgumentsJson, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.Equal(ConfigEditService.Sha256(Demo), stored.BaseSha256);
        Assert.Contains("srv-app:5001", stored.Content);

        var outside = await lab.Dispatcher.DispatchAsync("edit_config", JsonSerializer.SerializeToElement(new { path = "/etc/ssh/sshd_config", content = "x", serverIds = new[] { lab.Edge.Id } }), null, default);
        Assert.Contains("allowlisted", outside.Summary);
        var forged = await lab.Dispatcher.DispatchAsync("edit_config", JsonSerializer.SerializeToElement(new { path = "/etc/nginx/nginx.conf", content = "x",
            baseSha256 = new string('a', 64), serverIds = new[] { lab.Edge.Id } }), null, default);
        Assert.Contains("the app computes the rest", forged.Summary);
    }

    [Fact]
    public async Task HealthyEdit_StagesWritesValidatesReloadsChecks_AndCleansUp()
    {
        await using var lab = await Lab.CreateAsync(healthCode: "200");
        var (action, plan) = await lab.ProposeAsync(Demo.Replace("5000", "5001", StringComparison.Ordinal));

        var result = await lab.Service.ExecuteAsync(action, plan, lab.Edge, default);

        Assert.Null(result.Error);
        Assert.Equal(0, result.ExitCode);
        Assert.Contains("http://api.test.local:80/ via 127.0.0.1: 200 ok", result.StandardOutput);
        Assert.Collection(lab.Runner.Calls,
            call => Assert.StartsWith("umask 077; d='/tmp/dva-edit-", call.Command),
            call => Assert.True(call.Command.Contains("nginx -t", StringComparison.Ordinal) && call.UseSudo),
            call => Assert.True(call.Command.StartsWith("sleep 1; command -v curl", StringComparison.Ordinal) && !call.UseSudo),
            call => Assert.StartsWith("rm -rf -- '/tmp/dva-edit-", call.Command));
    }

    [Fact]
    public async Task FailedHealthCheck_RestoresTheBackupAndReloads()
    {
        await using var lab = await Lab.CreateAsync(healthCode: "502");
        var (action, plan) = await lab.ProposeAsync(Demo.Replace("5000", "5999", StringComparison.Ordinal));

        var result = await lab.Service.ExecuteAsync(action, plan, lab.Edge, default);

        Assert.Contains("Health check failed after the reload (api.test.local:80 answered 502); the backup was restored and nginx reloaded.", result.Error);
        Assert.Contains(lab.Runner.Calls, call => call.UseSudo && call.Command.Contains("DVA_ROLLBACK_FAILED=restore", StringComparison.Ordinal));
        Assert.StartsWith("rm -rf -- ", lab.Runner.Calls[^1].Command);
    }

    [Theory]
    [InlineData("DVA_EDIT_FAILED=validate\nDVA_RESTORED\nDVA_RELOADED_AFTER_RESTORE", 7, "validation (nginx -t) failed. The backup was restored and the service reloaded.")]
    [InlineData("DVA_EDIT_FAILED=changed", 4, "the file changed since the edit was proposed")]
    public async Task FailedApplySteps_AreReported_WithoutAHealthCheck(string output, int exit, string expected)
    {
        await using var lab = await Lab.CreateAsync(applyOutput: (output, exit));
        var (action, plan) = await lab.ProposeAsync(Demo.Replace("5000", "5001", StringComparison.Ordinal));

        var result = await lab.Service.ExecuteAsync(action, plan, lab.Edge, default);

        Assert.Contains(expected, result.Error);
        Assert.DoesNotContain(lab.Runner.Calls, call => call.Command.Contains("curl", StringComparison.Ordinal));
        Assert.StartsWith("rm -rf -- ", lab.Runner.Calls[^1].Command);
    }

    [Theory]
    [InlineData("true", "true", 0, "DVA_RELOADED", true)]
    [InlineData("false", "true", 7, "DVA_EDIT_FAILED=validate", false)]
    [InlineData("true", "false", 8, "DVA_EDIT_FAILED=reload", false)]
    public void ApplyScript_BacksUpWritesAtomicallyAndRestoresOnFailure_InARealShell(string validate, string reload, int expectedExit, string marker, bool keepsNew)
    {
        var directory = Directory.CreateTempSubdirectory("dva-edit-script-").FullName;
        try
        {
            var target = Path.Combine(directory, "demo.conf");
            var staged = Path.Combine(directory, "staged");
            File.WriteAllText(target, Demo);
            File.WriteAllText(staged, "new content\n");
            var rule = new ConfigServiceRule("/**", "test", validate, reload);
            var (exit, output) = Shell(ConfigEditService.ApplyCommand(target, staged, 1763164800, ConfigEditService.Sha256(Demo), rule));

            Assert.Equal(expectedExit, exit);
            Assert.Contains(marker, output);
            Assert.Equal(Demo, File.ReadAllText(target + ".bak-1763164800"));
            Assert.Equal(keepsNew ? "new content\n" : Demo, File.ReadAllText(target));
            Assert.Empty(Directory.GetFiles(directory, "demo.conf.dva-*")); // no temporary file left behind

            if (keepsNew)
            {
                var (rollbackExit, rollbackOutput) = Shell(ConfigEditService.RollbackCommand(target, 1763164800, rule));
                Assert.Equal(0, rollbackExit);
                Assert.Contains("DVA_RESTORED", rollbackOutput);
                Assert.Equal(Demo, File.ReadAllText(target));
            }

            // A file changed since the proposal is never overwritten.
            File.WriteAllText(target, "someone else's edit\n");
            var (changedExit, changedOutput) = Shell(ConfigEditService.ApplyCommand(target, staged, 1763164801, ConfigEditService.Sha256(Demo), rule));
            Assert.Equal(4, changedExit);
            Assert.Contains("DVA_EDIT_FAILED=changed", changedOutput);
            Assert.Equal("someone else's edit\n", File.ReadAllText(target));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static (int Exit, string Output) Shell(string script)
    {
        using var process = Process.Start(new ProcessStartInfo("/bin/sh", ["-c", script]) { RedirectStandardOutput = true, RedirectStandardError = true })!;
        var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        process.WaitForExit();
        return (process.ExitCode, output);
    }

    private sealed class Lab : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;

        private Lab(SqliteConnection connection, AppDbContext context, Server edge, ScriptedRunner runner, AgentToolDispatcher dispatcher,
            ConfigEditService service, ExecutionModeService modes)
        {
            _connection = connection;
            Context = context;
            Edge = edge;
            Runner = runner;
            Dispatcher = dispatcher;
            Service = service;
            Modes = modes;
        }

        public AppDbContext Context { get; }
        public Server Edge { get; }
        public ScriptedRunner Runner { get; }
        public AgentToolDispatcher Dispatcher { get; }
        public ConfigEditService Service { get; }
        public ExecutionModeService Modes { get; }

        public static async Task<Lab> CreateAsync(string healthCode = "200", (string Output, int Exit)? applyOutput = null)
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var context = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options);
            await context.Database.EnsureCreatedAsync();
            var edge = new Server("srv-edge", "127.0.0.1", 2201, "dva", "key", "staging");
            edge.RecordCapabilities(true, null, DateTimeOffset.UtcNow);
            context.Servers.Add(edge);
            var snapshot = new Snapshot([edge.Id], DateTimeOffset.UtcNow);
            snapshot.Complete(DateTimeOffset.UtcNow, 1, 0);
            context.Snapshots.Add(snapshot);
            await context.SaveChangesAsync();
            var block = new GraphNode(snapshot.Id, $"service:{edge.Id:N}:nginx-server:/etc/nginx/conf.d/demo.conf:6", GraphNodeKind.Service,
                "server block · api.test.local", null, edge.Id, JsonSerializer.Serialize(new { FilePath = "/etc/nginx/conf.d/demo.conf" }));
            var domain = new GraphNode(snapshot.Id, "domain:api.test.local", GraphNodeKind.Domain, "api.test.local", null, edge.Id, "{}");
            var edgeToBlock = new GraphEdge(snapshot.Id, domain.Id, block.Id, GraphEdgeKind.Declared);
            await new GraphRepository(context).SaveAsync(snapshot.Id, new GraphBuildResult([block, domain], [edgeToBlock], [], []), default);

            var runner = new ScriptedRunner(command =>
                command.StartsWith("test -f ", StringComparison.Ordinal) ? (Demo, 0)
                : command.StartsWith("umask 077", StringComparison.Ordinal) ? (StagedHash(command), 0)
                : command.Contains("DVA_EDIT_FAILED=missing", StringComparison.Ordinal) ? applyOutput ?? ("DVA_BACKUP=x\nDVA_WRITTEN\nDVA_VALIDATED\nDVA_RELOADED\n", 0)
                : command.StartsWith("sleep 1; command -v curl", StringComparison.Ordinal) ? ($"DVA_HEALTH api.test.local:80 {healthCode}\n", 0)
                : command.Contains("DVA_ROLLBACK_FAILED=restore", StringComparison.Ordinal) ? ("DVA_RESTORED\nDVA_RELOADED\n", 0)
                : (string.Empty, 0));
            var registry = new ToolRegistry();
            var servers = new ServerRepository(context);
            var snapshots = new SnapshotRepository(context);
            var graphs = new GraphRepository(context);
            var actions = new ActionRecordRepository(context);
            var modes = new ExecutionModeService(new AppSettingRepository(context), servers, NullLogger<ExecutionModeService>.Instance);
            var options = new ConfigEditOptions();
            var builder = new ToolCommandBuilder(registry, options);
            var service = new ConfigEditService(runner, servers, snapshots, graphs, new HealthCheckRepository(context), new NoProbe(), options);
            var approvals = new ApprovalService(registry, modes, servers, actions, TestBanGuard.Create(), TimeProvider.System);
            var execution = new ActionExecutionService(actions, servers, registry, modes, builder, runner, TestBanGuard.Create(), TimeProvider.System, service);
            var discovery = new DiscoveryService(servers, snapshots, graphs, new GraphBuilder(), runner, [], TimeProvider.System);
            var dispatcher = new AgentToolDispatcher(registry, approvals, execution, builder, servers, graphs, snapshots, discovery, runner, service);
            return new Lab(connection, context, edge, runner, dispatcher, service, modes);
        }

        public Task ConfirmAsync() => Modes.SaveAsync(new ExecutionModePolicy("staging", ExecutionMode.Confirm), default);

        public async Task<(ActionRecord Action, ToolCommand Plan)> ProposeAsync(string content)
        {
            await ConfirmAsync();
            var proposed = await Dispatcher.DispatchAsync("edit_config", JsonSerializer.SerializeToElement(new
            {
                path = "/etc/nginx/conf.d/demo.conf", content, serverIds = new[] { Edge.Id }
            }), null, default);
            Assert.True(proposed.NeedsApproval, proposed.Summary);
            Runner.Calls.Clear();
            var action = await Context.ActionRecords.SingleAsync(item => item.Id == proposed.ActionId);
            return (action, new ToolCommandBuilder(new ToolRegistry(), new ConfigEditOptions()).Build(action));
        }

        // The stage command prints the sha256 of what it wrote: decode the base64 it carries.
        private static string StagedHash(string command)
        {
            var start = command.IndexOf("printf '%s' '", StringComparison.Ordinal) + "printf '%s' '".Length;
            var encoded = command[start..command.IndexOf('\'', start)];
            return ConfigEditService.Sha256(System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(encoded))) + "\n";
        }

        public async ValueTask DisposeAsync()
        {
            await Context.DisposeAsync();
            await _connection.DisposeAsync();
        }
    }

    internal sealed class ScriptedRunner(Func<string, (string Output, int Exit)> answer) : ISshRunner
    {
        public List<(string Command, bool UseSudo)> Calls { get; } = [];

        public Task<RemoteCommandResult> RunAsync(Guid serverId, string command, SshRunOptions options, CancellationToken cancellationToken)
        {
            Calls.Add((command, options.UseSudo));
            var (output, exit) = answer(command);
            return Task.FromResult(new RemoteCommandResult(exit, output, string.Empty, TimeSpan.FromMilliseconds(3)));
        }
    }

    private sealed class NoProbe : IHealthProbe
    {
        public Task<HealthProbeResult> ProbeAsync(HealthCheckType type, string target, CancellationToken cancellationToken) =>
            Task.FromResult(new HealthProbeResult(true, 200, null, null));
    }
}
