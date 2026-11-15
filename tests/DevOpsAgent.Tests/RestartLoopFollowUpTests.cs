using System.Diagnostics;
using System.Text.Json;
using DevOpsAgent.Application.Abstractions;
using DevOpsAgent.Application.Discovery;
using DevOpsAgent.Application.Monitoring;
using DevOpsAgent.Domain.Discovery;
using DevOpsAgent.Domain.Monitoring;
using DevOpsAgent.Domain.Servers;
using DevOpsAgent.Infrastructure.Monitoring;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace DevOpsAgent.Tests;

/// <summary>T7.R18: container states, stale data, partial inspect, Docker permission failures, history pruning.</summary>
public sealed class RestartLoopFollowUpTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 15, 10, 0, 0, TimeSpan.Zero);
    private static readonly Guid ServerId = Guid.NewGuid();

    // ---- container states (D-038) -------------------------------------------------------------------------------------

    [Theory]
    [InlineData("created")]
    [InlineData("removing")]
    public async Task CreatedAndRemovingAlertOnlyAfterMoreThanTenMinutesAndResolveWhenTheStateEnds(string state)
    {
        var rig = new Rig();
        for (var minute = 0; minute <= 10; minute++)
        {
            await rig.RecordAsync(new ContainerStatus("id1", "job", state, 0));
            // Up to and including exactly 10 minutes in the state: no alert.
            Assert.DoesNotContain(await rig.ConditionsAsync(), item => item.Rule == ContainerRestartMonitor.StateRule);
            rig.Time.Advance(TimeSpan.FromMinutes(1));
        }

        await rig.RecordAsync(new ContainerStatus("id1", "job", state, 0));
        var alert = Assert.Single(await rig.ConditionsAsync());
        Assert.Equal(ContainerRestartMonitor.StateRule, alert.Rule);
        Assert.Equal(AlertSeverity.Medium, alert.Severity);
        Assert.Equal($"container-state:{ServerId:N}:job:{state}", alert.Key);
        Assert.Equal($"Container job is {state}", alert.Title);
        Assert.Contains("for 11 minutes", alert.Details, StringComparison.Ordinal);

        // The state ends: the condition disappears and the alert engine resolves it.
        rig.Time.Advance(TimeSpan.FromMinutes(1));
        await rig.RecordAsync(new ContainerStatus("id1", "job", "running", 0));
        Assert.Empty(await rig.ConditionsAsync());
    }

    [Fact]
    public async Task ACreatedContainerThatIsStartedWithinTenMinutesNeverAlerts()
    {
        var rig = new Rig();
        for (var minute = 0; minute < 6; minute++)
        {
            await rig.RecordAsync(new ContainerStatus("id1", "job", "created", 0));
            Assert.Empty(await rig.ConditionsAsync());
            rig.Time.Advance(TimeSpan.FromMinutes(1));
        }

        for (var minute = 0; minute < 12; minute++)
        {
            await rig.RecordAsync(new ContainerStatus("id1", "job", "running", 0));
            Assert.Empty(await rig.ConditionsAsync());
            rig.Time.Advance(TimeSpan.FromMinutes(1));
        }
    }

    [Fact]
    public async Task TheCreatedClockRestartsForAnotherContainerOrAnotherStateInBetween()
    {
        var rig = new Rig();
        for (var minute = 0; minute < 8; minute++)
        {
            await rig.RecordAsync(new ContainerStatus("old", "job", "created", 0));
            rig.Time.Advance(TimeSpan.FromMinutes(1));
        }

        // Recreated under the same name (new ID): its own 10 minutes start now, although the name has been "created" for 8.
        for (var minute = 0; minute < 8; minute++)
        {
            await rig.RecordAsync(new ContainerStatus("new", "job", "created", 0));
            Assert.Empty(await rig.ConditionsAsync());
            rig.Time.Advance(TimeSpan.FromMinutes(1));
        }

        // Started and created again: the clock restarts as well.
        await rig.RecordAsync(new ContainerStatus("new", "job", "running", 0));
        rig.Time.Advance(TimeSpan.FromMinutes(1));
        for (var minute = 0; minute < 8; minute++)
        {
            await rig.RecordAsync(new ContainerStatus("new", "job", "created", 0));
            Assert.Empty(await rig.ConditionsAsync());
            rig.Time.Advance(TimeSpan.FromMinutes(1));
        }
    }

    [Fact]
    public async Task PausedAlertsLowAtOnceAndResolvesWhenTheContainerIsResumedOrRemoved()
    {
        var rig = new Rig();
        await rig.RecordAsync(new ContainerStatus("id1", "web", "running", 0), new ContainerStatus("id2", "db", "running", 0));
        Assert.Empty(await rig.ConditionsAsync());

        rig.Time.Advance(TimeSpan.FromMinutes(1));
        await rig.RecordAsync(new ContainerStatus("id1", "web", "paused", 0), new ContainerStatus("id2", "db", "running", 0));
        var alert = Assert.Single(await rig.ConditionsAsync());
        Assert.Equal(AlertSeverity.Low, alert.Severity);
        Assert.Equal(ContainerRestartMonitor.StateRule, alert.Rule);
        Assert.Equal($"container-state:{ServerId:N}:web:paused", alert.Key);
        Assert.Equal("Container web is paused", alert.Title);

        rig.Time.Advance(TimeSpan.FromMinutes(1));
        await rig.RecordAsync(new ContainerStatus("id1", "web", "running", 0), new ContainerStatus("id2", "db", "running", 0));
        Assert.Empty(await rig.ConditionsAsync());

        rig.Time.Advance(TimeSpan.FromMinutes(1));
        await rig.RecordAsync(new ContainerStatus("id1", "web", "paused", 0));
        Assert.Single(await rig.ConditionsAsync());
        rig.Time.Advance(TimeSpan.FromMinutes(1));
        await rig.RecordAsync(new ContainerStatus("id2", "db", "running", 0));
        Assert.Empty(await rig.ConditionsAsync());
    }

    [Fact]
    public async Task ExitedAndDeadKeepTheHighContainerStateAlertAndTheNewStatesAreNotRaisedByTheProbe()
    {
        // Regression pin (owner, T7.R18): exited and dead behave exactly as before: High, `container-state`, one alert per
        // container keyed by server and name, title "Container <name> is <state>". Containers that are running, restarting
        // (restart-loop rule), created, paused or removing (history rules) raise nothing from the probe.
        var server = new Server("srv-app", "srv-app", 22, "dva", "key", "staging");
        var runner = new StatusRunner(_ => Ok(
            "web\texited\nredis\tdead\napi\trunning\nloop\trestarting\njob\tcreated\nfrozen\tpaused\nstopping\tremoving\nodd\tfuture-state\n"));
        var source = Source([server], runner, new Rig());

        var states = (await source.GetActiveConditionsAsync(CancellationToken.None)).Where(item => item.Rule == "container-state").ToArray();

        Assert.Equal(new[] { "odd", "redis", "web" }, states.Select(item => item.Target).Order());
        Assert.All(states, item => Assert.Equal(AlertSeverity.High, item.Severity));
        var exited = Assert.Single(states, item => item.Target == "web");
        Assert.Equal($"container-state:{server.Id:N}:web", exited.Key);
        Assert.Equal("Container web is exited", exited.Title);
        Assert.Equal("Latest read-only Docker status reports: exited.", exited.Details);
        Assert.Equal("Container redis is dead", Assert.Single(states, item => item.Target == "redis").Title);
    }

    // ---- unreachable servers (a) ----------------------------------------------------------------------------------------

    [Fact]
    public async Task AnUnreachableServerKeepsAnOpenRestartLoopAlertAndItResolvesOnlyFromFreshQuietSamples()
    {
        var rig = new Rig();
        for (var minute = 0; minute <= 3; minute++)
        {
            await rig.RecordAsync(new ContainerStatus("id1", "crashloop", "running", minute));
            rig.Time.Advance(TimeSpan.FromMinutes(1));
        }

        Assert.Single(await rig.ConditionsAsync());

        // The server cannot be reached for 45 minutes: no samples. The old code resolved the alert after 10 minutes.
        foreach (var minutes in new[] { 5, 10, 11, 20, 45 })
        {
            rig.Time.Set(Start.AddMinutes(4 + minutes));
            var loop = Assert.Single(await rig.ConditionsAsync());
            Assert.Equal(ContainerRestartMonitor.Rule, loop.Rule);
        }

        // It answers again. The first fresh sample shows the counter unchanged, but ten quiet minutes still have to be seen.
        var recovery = rig.Time.Now;
        for (var minute = 0; minute < 10; minute++)
        {
            await rig.RecordAsync(new ContainerStatus("id1", "crashloop", "running", 3));
            rig.Time.Advance(TimeSpan.FromMinutes(1));
            if (rig.Time.Now - recovery < ContainerRestartMonitor.Window)
                Assert.Single(await rig.ConditionsAsync());
        }

        Assert.Equal(recovery.AddMinutes(10), rig.Time.Now);
        Assert.Empty(await rig.ConditionsAsync());
    }

    [Fact]
    public async Task AnUnreachableServerThatRecoversStillLoopingStaysOpenAndOneThatLostTheContainerResolves()
    {
        var rig = new Rig();
        for (var minute = 0; minute <= 3; minute++)
        {
            await rig.RecordAsync(new ContainerStatus("id1", "crashloop", "running", minute));
            rig.Time.Advance(TimeSpan.FromMinutes(1));
        }

        rig.Time.Set(Start.AddMinutes(40));
        await rig.RecordAsync(new ContainerStatus("id1", "crashloop", "restarting", 40));
        rig.Time.Advance(TimeSpan.FromMinutes(1));
        await rig.RecordAsync(new ContainerStatus("id1", "crashloop", "restarting", 41));
        Assert.Single(await rig.ConditionsAsync());

        // Container removed (fresh data): resolved at once.
        rig.Time.Advance(TimeSpan.FromMinutes(1));
        await rig.RecordAsync();
        Assert.Empty(await rig.ConditionsAsync());
    }

    [Fact]
    public void ALatestSampleThatIsTooOldHoldsTheLoopAsOfTheLastSample()
    {
        var history = new List<ContainerCycle>();
        for (var minute = 0; minute <= 3; minute++)
            history.Add(new ContainerCycle(Start.AddMinutes(minute), [new ContainerStatus("id1", "web", "running", minute)]));

        Assert.Single(ContainerRestartMonitor.Evaluate(history, Start.AddMinutes(3)));
        Assert.Single(ContainerRestartMonitor.Evaluate(history, Start.AddMinutes(5))); // 2 minutes old: still current
        Assert.Single(ContainerRestartMonitor.Evaluate(history, Start.AddHours(5)));   // no fresh sample: held, not resolved
    }

    [Fact]
    public async Task AnUnreachableServerDoesNotOpenOrAgeContainerStateAlertsFromStaleDataAndKeepsOpenOnes()
    {
        var rig = new Rig();
        // Created for 8 minutes, paused, and (another container) created for 11 minutes when the server drops out.
        for (var minute = 0; minute <= 11; minute++)
        {
            var job = minute >= 3 ? "created" : "running";
            await rig.RecordAsync(new ContainerStatus("a", "young", minute >= 4 ? "created" : "running", 0),
                new ContainerStatus("b", "old", "created", 0), new ContainerStatus("c", "frozen", "paused", 0), new ContainerStatus("d", "other", job, 0));
            rig.Time.Advance(TimeSpan.FromMinutes(1));
        }

        var before = (await rig.ConditionsAsync()).Select(item => item.Key).Order().ToArray();
        Assert.Equal(new[] { $"container-state:{ServerId:N}:frozen:paused", $"container-state:{ServerId:N}:old:created" }, before);

        // Two hours without a sample: the same picture; "young" (created 8 minutes) does not cross ten minutes by itself.
        rig.Time.Advance(TimeSpan.FromHours(2));
        Assert.Equal(before, (await rig.ConditionsAsync()).Select(item => item.Key).Order().ToArray());

        // Fresh data: old is running and frozen is resumed, so both resolve.
        await rig.RecordAsync(new ContainerStatus("b", "old", "running", 0), new ContainerStatus("c", "frozen", "running", 0));
        Assert.Empty(await rig.ConditionsAsync());
    }

    [Fact]
    public async Task AnUnreachableServerThroughTheAlertSourceKeepsRestartLoopAndStateAlertsNextToTheUnreachableAlert()
    {
        var server = new Server("srv-app", "srv-app", 22, "dva", "key", "staging");
        var rig = new Rig(server.Id);
        for (var minute = 0; minute <= 3; minute++)
        {
            await rig.RecordAsync(new ContainerStatus("id1", "crashloop", "running", minute), new ContainerStatus("id2", "web", "paused", 0));
            rig.Time.Advance(TimeSpan.FromMinutes(1));
        }

        rig.Time.Advance(TimeSpan.FromMinutes(30));
        var state = new HostReachabilityState(server.Id, Start, Start.AddMinutes(60), 3, "could not connect", false, null);
        var runner = new StatusRunner(_ => throw new HostUnreachableException(server.Id, "srv-app", state));
        var conditions = await Source([server], runner, rig).GetActiveConditionsAsync(CancellationToken.None);

        Assert.Contains(conditions, item => item.Rule == "server-unreachable");
        Assert.Contains(conditions, item => item.Rule == ContainerRestartMonitor.Rule && item.Target == "crashloop");
        Assert.Contains(conditions, item => item.Rule == ContainerRestartMonitor.StateRule && item.Severity == AlertSeverity.Low && item.Target == "web");
        Assert.DoesNotContain(conditions, item => item.Rule == "container-state" && item.Severity == AlertSeverity.High);
    }

    // ---- partial inspect (b) ---------------------------------------------------------------------------------------------

    [Fact]
    public void PartialInspectOutputKeepsTheContainersThatWereInspected()
    {
        var containers = ContainerStatusQuery.Parse(
            "abc123|/web|running|0\nError: No such object: dead00\ndef456|/db|exited|2\n")!;

        Assert.Equal(new[] { new ContainerStatus("abc123", "web", "running", 0), new ContainerStatus("def456", "db", "exited", 2) }, containers);
    }

    [Fact]
    public async Task APartialInspectKeepsTheOtherContainersAndDoesNotTriggerTheSudoRetry()
    {
        var server = new Server("srv-app", "srv-app", 22, "dva", "key", "staging");
        server.RecordCapabilities(true, "Debian GNU/Linux 12", Start);
        // What the command now prints for a container removed between `ps` and `inspect`: the others, exit 0.
        var runner = new QueryRunner("id1|/web|running|0\nError: No such object: gone\nid2|/db|paused|0\n", exitCode: 0);
        var rig = new Rig(server.Id);
        var service = new MetricService(new OneServer(server), new NoMetrics(), runner, rig.Monitor, rig.Time);

        await service.PollAsync(CancellationToken.None);

        Assert.DoesNotContain(runner.Calls, call => call.UseSudo);
        Assert.Equal(1, runner.Calls.Count(call => !call.UseSudo));
        Assert.Contains("\"web\"", rig.Settings.Values.Single(pair => pair.Key.StartsWith("monitoring.containers.", StringComparison.Ordinal)).Value, StringComparison.Ordinal);
        var paused = Assert.Single(await rig.ConditionsAsync());
        Assert.Equal("db", paused.Target);
    }

    [Fact]
    public async Task ARealDockerFailureOfTheQueryStillRetriesWithSudo()
    {
        var server = new Server("srv-app", "srv-app", 22, "dva", "key", "staging");
        server.RecordCapabilities(true, "Debian GNU/Linux 12", Start);
        var runner = new QueryRunner(string.Empty, exitCode: 1);
        var rig = new Rig(server.Id);

        await new MetricService(new OneServer(server), new NoMetrics(), runner, rig.Monitor, rig.Time).PollAsync(CancellationToken.None);

        Assert.Single(runner.Calls, call => call.UseSudo);
    }

    [Fact]
    public void TheQueryCommandExitCodeFollowsPartialAndRealFailuresInARealShell()
    {
        if (!OperatingSystem.IsLinux()) return;
        const string two = "id1|/web|running|0\nid2|/db|exited|1";

        // ps fine, inspect printed two containers and complained about a third: exit 0 with both lines.
        var partial = RunShell(ContainerStatusQuery.Command, new FakeDocker { PsOut = "id1\nid2\ngone", InspectOut = two, InspectErr = "Error: No such object: gone", InspectRc = 1 });
        Assert.Equal(0, partial.ExitCode);
        Assert.Equal(two.Split('\n'), ContainerStatusQuery.Parse(partial.Output)!.Select(item => $"{item.Id}|/{item.Name}|{item.State}|{item.RestartCount}"));

        // The only container vanished: nothing to report, but not a failure either.
        var vanished = RunShell(ContainerStatusQuery.Command, new FakeDocker { PsOut = "gone", InspectErr = "Error: No such object: gone", InspectRc = 1 });
        Assert.Equal(0, vanished.ExitCode);
        Assert.Empty(ContainerStatusQuery.Parse(vanished.Output)!);

        // A daemon that dies after `ps` is a real failure.
        var dead = RunShell(ContainerStatusQuery.Command, new FakeDocker { PsOut = "id1", InspectErr = "Cannot connect to the Docker daemon at unix:///var/run/docker.sock", InspectRc = 1 });
        Assert.Equal(1, dead.ExitCode);

        // `ps` refused (permission denied): a real failure, so the sudo retry still happens.
        var denied = RunShell(ContainerStatusQuery.Command, new FakeDocker { PsErr = "permission denied while trying to connect to the Docker daemon socket" });
        Assert.NotEqual(0, denied.ExitCode);

        // Healthy, empty, and no Docker at all.
        Assert.Equal(0, RunShell(ContainerStatusQuery.Command, new FakeDocker { PsOut = "id1", InspectOut = "id1|/web|running|0" }).ExitCode);
        Assert.Equal(0, RunShell(ContainerStatusQuery.Command, new FakeDocker { PsOut = "" }).ExitCode);
        var none = RunShell(ContainerStatusQuery.Command, null);
        Assert.Equal(0, none.ExitCode);
        Assert.Null(ContainerStatusQuery.Parse(none.Output));
    }

    // ---- status probe (c) ------------------------------------------------------------------------------------------------

    [Fact]
    public async Task APermissionDeniedProbeIsReportedAndTheSudoFallbackIsTried()
    {
        var server = new Server("srv-app", "srv-app", 22, "dva", "key", "staging");
        server.RecordCapabilities(true, "Debian GNU/Linux 12", Start);
        var runner = new StatusRunner(_ => Ok("", "__DVA_DOCKER_DENIED__\n"));

        var conditions = await Source([server], runner, new Rig()).GetActiveConditionsAsync(CancellationToken.None);

        Assert.Equal(new[] { false, true }, runner.StatusCalls.Select(call => call.UseSudo));
        var failure = Assert.Single(conditions, item => item.Rule == "docker-status-unavailable");
        Assert.Equal(AlertSeverity.Medium, failure.Severity);
        Assert.Equal(server.Id, failure.ServerId);
        Assert.Contains("permission denied", failure.Details, StringComparison.Ordinal);
        Assert.Contains("did not help", failure.Details, StringComparison.Ordinal);
        Assert.DoesNotContain(conditions, item => item.Rule == "server-unreachable");
    }

    [Fact]
    public async Task APermissionDeniedProbeWithoutSudoIsReportedWithoutTryingSudo()
    {
        var notTested = new Server("srv-a", "srv-a", 22, "dva", "key", "staging");
        var noSudo = new Server("srv-b", "srv-b", 22, "dva", "key", "staging");
        noSudo.RecordCapabilities(false, "Debian GNU/Linux 12", Start);
        var runner = new StatusRunner(_ => Ok("", "__DVA_DOCKER_DENIED__\n"));

        var conditions = await Source([notTested, noSudo], runner, new Rig()).GetActiveConditionsAsync(CancellationToken.None);

        Assert.All(runner.StatusCalls, call => Assert.False(call.UseSudo));
        Assert.Equal(2, conditions.Count(item => item.Rule == "docker-status-unavailable"));
        Assert.All(conditions.Where(item => item.Rule == "docker-status-unavailable"),
            item => Assert.Contains("not available for this server", item.Details, StringComparison.Ordinal));
    }

    [Fact]
    public async Task TheSudoFallbackOfTheProbeShowsTheContainersAndRaisesNoFailure()
    {
        var server = new Server("srv-app", "srv-app", 22, "dva", "key", "staging");
        server.RecordCapabilities(true, "Debian GNU/Linux 12", Start);
        var runner = new StatusRunner(options => options.UseSudo ? Ok("web\texited\n") : Ok("", "__DVA_DOCKER_DENIED__\n"));

        var conditions = await Source([server], runner, new Rig()).GetActiveConditionsAsync(CancellationToken.None);

        Assert.DoesNotContain(conditions, item => item.Rule == "docker-status-unavailable");
        Assert.Contains(conditions, item => item.Rule == "container-state" && item.Target == "web" && item.Severity == AlertSeverity.High);
    }

    [Fact]
    public async Task AFailingSudoRetryKeepsTheRefusalReport()
    {
        var server = new Server("srv-app", "srv-app", 22, "dva", "key", "staging");
        server.RecordCapabilities(true, "Debian GNU/Linux 12", Start);
        var runner = new StatusRunner(options => options.UseSudo
            ? new RemoteCommandResult(1, string.Empty, "sudo: a password is required", TimeSpan.Zero)
            : Ok("", "__DVA_DOCKER_DENIED__\n"));

        var conditions = await Source([server], runner, new Rig()).GetActiveConditionsAsync(CancellationToken.None);

        Assert.Single(conditions, item => item.Rule == "docker-status-unavailable");
        Assert.DoesNotContain(conditions, item => item.Rule == "server-unreachable");
    }

    [Fact]
    public async Task HostsWithoutDockerOrWithAStoppedDaemonStayQuiet()
    {
        var server = new Server("srv-app", "srv-app", 22, "dva", "key", "staging");
        server.RecordCapabilities(true, "Debian GNU/Linux 12", Start);

        foreach (var dockerPart in new[] { string.Empty, "__DVA_DOCKER_DOWN__\n" })
        {
            var runner = new StatusRunner(_ => Ok("", dockerPart));
            var conditions = await Source([server], runner, new Rig()).GetActiveConditionsAsync(CancellationToken.None);

            Assert.Empty(conditions);
            Assert.Single(runner.StatusCalls); // no sudo retry: nothing is refused
        }
    }

    [Fact]
    public void TheParserSeparatesNoDockerStoppedDaemonAndPermissionDenied()
    {
        Assert.Equal(DockerFailure.None, RemoteServiceStatusParser.Parse("__DVA_DOCKER__\n__DVA_SYSTEMD_FAILED__\n").Docker);
        Assert.Equal(DockerFailure.None, RemoteServiceStatusParser.Parse("__DVA_DOCKER__\nweb\trunning\n__DVA_SYSTEMD_FAILED__\n").Docker);
        var down = RemoteServiceStatusParser.Parse("__DVA_DOCKER__\n__DVA_DOCKER_DOWN__\n__DVA_SYSTEMD_FAILED__\n");
        Assert.Equal(DockerFailure.DaemonUnavailable, down.Docker);
        Assert.Empty(down.Containers);
        var denied = RemoteServiceStatusParser.Parse("__DVA_DOCKER__\n__DVA_DOCKER_DENIED__\n__DVA_SYSTEMD_FAILED__\nnginx.service loaded failed failed\n");
        Assert.Equal(DockerFailure.PermissionDenied, denied.Docker);
        Assert.Equal(new[] { "nginx.service" }, denied.FailedUnits);
    }

    [Fact]
    public void TheStatusCommandReportsDockerFailuresInARealShellAndToleratesHostsWithoutSystemd()
    {
        if (!OperatingSystem.IsLinux()) return;
        var command = AlertSignalSource.StatusCommand;

        var ok = RunShell(command, new FakeDocker { PsOut = "web\trunning\napi\texited" });
        Assert.Equal(0, ok.ExitCode);
        Assert.Equal(new[] { "web", "api" }, RemoteServiceStatusParser.Parse(ok.Output).Containers.Select(item => item.Name));
        Assert.Equal(DockerFailure.None, RemoteServiceStatusParser.Parse(ok.Output).Docker);

        var denied = RunShell(command, new FakeDocker { PsErr = "permission denied while trying to connect to the Docker daemon socket at unix:///var/run/docker.sock" });
        Assert.Equal(0, denied.ExitCode); // the probe itself ran; the refusal is in the output, not in the exit code
        var deniedStatus = RemoteServiceStatusParser.Parse(denied.Output);
        Assert.Equal(DockerFailure.PermissionDenied, deniedStatus.Docker);
        Assert.Empty(deniedStatus.Containers);
        Assert.DoesNotContain("unix:///", denied.Output, StringComparison.Ordinal); // the server's error text is not forwarded

        var capitalised = RunShell(command, new FakeDocker { PsErr = "Got permission denied while trying to connect to the Docker daemon socket" });
        Assert.Equal(DockerFailure.PermissionDenied, RemoteServiceStatusParser.Parse(capitalised.Output).Docker);

        var down = RunShell(command, new FakeDocker { PsErr = "Cannot connect to the Docker daemon at unix:///var/run/docker.sock. Is the docker daemon running?" });
        Assert.Equal(DockerFailure.DaemonUnavailable, RemoteServiceStatusParser.Parse(down.Output).Docker);

        var none = RunShell(command, null);
        Assert.Equal(0, none.ExitCode);
        var noneStatus = RemoteServiceStatusParser.Parse(none.Output);
        Assert.Equal(DockerFailure.None, noneStatus.Docker);
        Assert.Empty(noneStatus.Containers);
    }

    [Fact]
    public void TheStatusCommandRunsThroughSudoAsOneOfTheAppsOwnCommandLines()
    {
        var logged = SudoLogged(AlertSignalSource.StatusCommand);

        Assert.True(AppSudoCommands.IsAppCommand(logged));
        Assert.False(AppSudoCommands.IsAppCommand(SudoLogged(AlertSignalSource.StatusCommand + "; id")));
        Assert.False(AppSudoCommands.IsAppCommand(SudoLogged("docker ps -a")));
        Assert.True(AppSudoCommands.IsAppCommand(SudoLogged(ContainerStatusQuery.Command)));
        Assert.Contains(AlertSignalSource.StatusCommand, AppSudoCommands.Templates);
    }

    // ---- history pruning and corrupt history (d) -------------------------------------------------------------------------

    [Fact]
    public async Task HistoryOlderThanTwentyFourHoursIsPrunedWhenRead()
    {
        var rig = new Rig();
        rig.Time.Set(Start.AddDays(3));
        var now = rig.Time.Now;
        rig.Seed(
            new ContainerCycle(now - TimeSpan.FromHours(24) - TimeSpan.FromSeconds(1), [new ContainerStatus("a", "web", "running", 0)]),
            new ContainerCycle(now - TimeSpan.FromHours(24), [new ContainerStatus("a", "web", "running", 0)]),
            new ContainerCycle(now - TimeSpan.FromHours(23), [new ContainerStatus("a", "web", "running", 0)]),
            new ContainerCycle(now - TimeSpan.FromHours(30), [new ContainerStatus("a", "web", "running", 0)]));

        await rig.ConditionsAsync();

        // Exactly 24 hours old stays; one second older goes.
        Assert.Equal(new[] { now - TimeSpan.FromHours(24), now - TimeSpan.FromHours(23) }, rig.Stored().Select(item => item.AtUtc));
    }

    [Fact]
    public async Task AServerThatHasNotBeenSampledForADayLosesItsHistoryAndItsHeldAlerts()
    {
        var rig = new Rig();
        for (var minute = 0; minute <= 3; minute++)
        {
            await rig.RecordAsync(new ContainerStatus("id1", "crashloop", "running", minute));
            rig.Time.Advance(TimeSpan.FromMinutes(1));
        }

        rig.Time.Set(Start.AddHours(23));
        Assert.Single(await rig.ConditionsAsync());
        Assert.Equal(4, rig.Stored().Count);

        rig.Time.Set(Start.AddHours(25));
        Assert.Empty(await rig.ConditionsAsync());
        Assert.Empty(rig.Stored());
        Assert.Empty(await rig.ConditionsAsync()); // nothing left to prune, no second write
    }

    [Fact]
    public async Task WritingDropsHistoryOlderThanTwentyFourHoursAndKeepsSamplesAcrossAnUnsampledGap()
    {
        var rig = new Rig();
        rig.Time.Set(Start.AddDays(3));
        var now = rig.Time.Now;
        rig.Seed(
            new ContainerCycle(now - TimeSpan.FromHours(30), [new ContainerStatus("a", "web", "running", 0)]),
            new ContainerCycle(now - TimeSpan.FromMinutes(40), [new ContainerStatus("a", "web", "running", 0)]),
            new ContainerCycle(now - TimeSpan.FromMinutes(20), [new ContainerStatus("a", "web", "running", 0)]),
            new ContainerCycle(now - TimeSpan.FromMinutes(1), [new ContainerStatus("a", "web", "running", 0)]));

        await rig.RecordAsync(new ContainerStatus("a", "web", "running", 0));

        // Time in which the server was not sampled does not age the history (the samples before an outage are still there to
        // compare with the first ones after it), but nothing older than 24 hours stays.
        Assert.Equal(new[] { now - TimeSpan.FromMinutes(40), now - TimeSpan.FromMinutes(20), now - TimeSpan.FromMinutes(1), now },
            rig.Stored().Select(item => item.AtUtc));
    }

    [Fact]
    public async Task WritingKeepsThirtyMinutesOfSampledTime()
    {
        var rig = new Rig();
        for (var minute = 0; minute < 45; minute++)
        {
            await rig.RecordAsync(new ContainerStatus("a", "web", "running", 0));
            rig.Time.Advance(TimeSpan.FromMinutes(1));
        }

        var stored = rig.Stored();
        Assert.Equal(31, stored.Count);
        Assert.Equal(Start.AddMinutes(14), stored[0].AtUtc);
        Assert.Equal(Start.AddMinutes(44), stored[^1].AtUtc);
    }

    [Theory]
    [InlineData("{not json")]
    [InlineData("{\"a\":1}")]
    [InlineData("[{\"atUtc\":\"2026-11-15T10:00:00+00:00\",\"containers\":null}]")]
    [InlineData("[{\"atUtc\":\"nope\",\"containers\":[]}]")]
    public async Task CorruptHistoryIsLoggedWithoutItsContentsAndTreatedAsEmpty(string stored)
    {
        var rig = new Rig();
        rig.Settings.Values[rig.HistoryKey] = stored;

        var conditions = await rig.ConditionsAsync();

        Assert.Empty(conditions);
        var entry = Assert.Single(rig.Logger.Entries);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Contains(ServerId.ToString(), entry.Message, StringComparison.Ordinal);
        Assert.Contains("cannot be parsed", entry.Message, StringComparison.Ordinal);
        // Neither the stored text nor the exception message (which can quote it) is logged.
        Assert.DoesNotContain("not json", entry.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("nope", entry.Message, StringComparison.Ordinal);
        Assert.Null(entry.Exception);

        // It continues with an empty history: the unreadable value is replaced, reported once, and recording works.
        await rig.ConditionsAsync();
        Assert.Single(rig.Logger.Entries);
        await rig.RecordAsync(new ContainerStatus("a", "web", "paused", 0));
        Assert.Single(await rig.ConditionsAsync());
        Assert.Single(rig.Logger.Entries);
    }

    [Fact]
    public async Task CorruptHistoryIsAlsoLoggedWhenTheMetricCycleRecords()
    {
        var rig = new Rig();
        rig.Settings.Values[rig.HistoryKey] = "[[[";

        await rig.RecordAsync(new ContainerStatus("a", "web", "running", 0));

        Assert.Single(rig.Logger.Entries);
        Assert.Single(rig.Stored());
    }

    // ---- fakes ---------------------------------------------------------------------------------------------------------

    private static RemoteCommandResult Ok(string containers, string dockerPart = "") =>
        new(0, "__DVA_DOCKER__\n" + dockerPart + containers + "__DVA_SYSTEMD_FAILED__\n", string.Empty, TimeSpan.Zero);

    private static string SudoLogged(string command) =>
        "/usr/bin/sh -c '" + command.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("'", "\\'", StringComparison.Ordinal) + "'";

    private static AlertSignalSource Source(Server[] servers, ISshRunner runner, Rig rig)
    {
        var scanner = new AuthLogScanner(runner, new MemorySettings(), Options.Create(new SecurityMonitoringOptions()), rig.Time);
        return new AlertSignalSource(new FakeServers(servers), new NoMetrics(), new NoHealthChecks(), new NoSnapshots(), runner,
            scanner, new DriftService(new NoSnapshots(), null!), rig.Monitor, rig.Time);
    }

    private sealed class Rig
    {
        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
        private readonly Guid _serverId;

        public Rig(Guid? serverId = null)
        {
            _serverId = serverId ?? ServerId;
            Time = new ManualTime(Start);
            Settings = new MemorySettings();
            Logger = new CapturingLogger<ContainerRestartMonitor>();
            Monitor = new ContainerRestartMonitor(Settings, Time, Logger);
        }

        public ManualTime Time { get; }
        public MemorySettings Settings { get; }
        public CapturingLogger<ContainerRestartMonitor> Logger { get; }
        public ContainerRestartMonitor Monitor { get; }
        public string HistoryKey => $"monitoring.containers.{_serverId:N}";

        public Task RecordAsync(params ContainerStatus[] containers) => Monitor.RecordAsync(_serverId, containers, CancellationToken.None);

        public Task<IReadOnlyList<AlertCondition>> ConditionsAsync() => Monitor.GetConditionsAsync(_serverId, "srv-app", CancellationToken.None);

        public void Seed(params ContainerCycle[] cycles) => Settings.Values[HistoryKey] = JsonSerializer.Serialize(cycles, Json);

        public List<ContainerCycle> Stored() => JsonSerializer.Deserialize<List<ContainerCycle>>(Settings.Values[HistoryKey], Json)!;
    }

    private sealed class MemorySettings : IAppSettingRepository
    {
        public Dictionary<string, string> Values { get; } = new(StringComparer.Ordinal);
        public Task<string?> GetAsync(string key, CancellationToken cancellationToken) => Task.FromResult(Values.GetValueOrDefault(key));
        public Task SetAsync(string key, string value, CancellationToken cancellationToken) { Values[key] = value; return Task.CompletedTask; }
    }

    private sealed class ManualTime(DateTimeOffset start) : TimeProvider
    {
        public DateTimeOffset Now { get; private set; } = start;
        public override DateTimeOffset GetUtcNow() => Now;
        public void Advance(TimeSpan elapsed) => Now += elapsed;
        public void Set(DateTimeOffset at) => Now = at;
    }

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, string Message, Exception? Exception)> Entries { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception), exception));
    }

    /// <summary>Answers the status probe through a function (which may throw); the auth-log scan always finds nothing.</summary>
    private sealed class StatusRunner(Func<SshRunOptions, RemoteCommandResult> status) : ISshRunner
    {
        public List<SshRunOptions> StatusCalls { get; } = [];

        public Task<RemoteCommandResult> RunAsync(Guid serverId, string command, SshRunOptions options, CancellationToken cancellationToken)
        {
            if (command.Contains("journalctl", StringComparison.Ordinal))
                return Task.FromResult(new RemoteCommandResult(0, "\n__DVA_BAN_TOOLS__\n__DVA_BAN_STATE__\n", string.Empty, TimeSpan.Zero));
            Assert.Equal(AlertSignalSource.StatusCommand, command);
            lock (StatusCalls) StatusCalls.Add(options);
            return Task.FromResult(status(options));
        }
    }

    /// <summary>Answers the container query of a metric cycle with a fixed result and records every call.</summary>
    private sealed class QueryRunner(string output, int exitCode) : ISshRunner
    {
        public List<SshRunOptions> Calls { get; } = [];

        public Task<RemoteCommandResult> RunAsync(Guid serverId, string command, SshRunOptions options, CancellationToken cancellationToken)
        {
            if (command != ContainerStatusQuery.Command) return Task.FromResult(new RemoteCommandResult(1, string.Empty, "skipped", TimeSpan.Zero));
            Calls.Add(options);
            return Task.FromResult(new RemoteCommandResult(exitCode, output, string.Empty, TimeSpan.Zero));
        }
    }

    private sealed class FakeServers(params Server[] items) : IServerRepository
    {
        public Task<IReadOnlyList<Server>> ListAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<Server>>(items);
        public Task<Server?> GetAsync(Guid id, CancellationToken cancellationToken) => Task.FromResult(items.FirstOrDefault(item => item.Id == id));
        public Task AddAsync(Server server, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task SaveAsync(Server server, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task DeleteAsync(Server server, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class OneServer(Server server) : IServerRepository
    {
        public Task<IReadOnlyList<Server>> ListAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<Server>>([server]);
        public Task<Server?> GetAsync(Guid id, CancellationToken cancellationToken) => Task.FromResult<Server?>(server);
        public Task AddAsync(Server item, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task SaveAsync(Server item, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task DeleteAsync(Server item, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class NoMetrics : IMetricRepository
    {
        public Task AddSamplesAsync(IReadOnlyCollection<MetricSample> samples, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<IReadOnlyList<MetricSample>> GetRecentAsync(Guid serverId, DateTimeOffset sinceUtc, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<MetricSample>>([]);
        public Task<IReadOnlyList<MetricSample>> GetLatestAsync(Guid serverId, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<MetricSample>>([]);
        public Task<IReadOnlyList<MetricSample>> GetUnrolledAsync(DateTimeOffset beforeUtc, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<MetricSample>>([]);
        public Task SaveRollupsAsync(IReadOnlyCollection<MetricRollup> rollups, DateTimeOffset rawPruneBeforeUtc, DateTimeOffset rollupPruneBeforeUtc, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class NoHealthChecks : IHealthCheckRepository
    {
        public Task<IReadOnlyList<HealthCheck>> ListAsync(Guid serverId, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<HealthCheck>>([]);
        public Task<IReadOnlyList<HealthCheck>> ListEnabledAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<HealthCheck>>([]);
        public Task<HealthCheck?> GetAsync(Guid id, CancellationToken cancellationToken) => Task.FromResult<HealthCheck?>(null);
        public Task AddAsync(HealthCheck healthCheck, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task SaveAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task DeleteAsync(HealthCheck healthCheck, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class NoSnapshots : ISnapshotRepository
    {
        public Task<Snapshot?> GetLatestAsync(CancellationToken cancellationToken) => Task.FromResult<Snapshot?>(null);
        public Task<Snapshot?> GetLatestRunAsync(CancellationToken cancellationToken) => Task.FromResult<Snapshot?>(null);
        public Task<IReadOnlyList<Snapshot>> ListAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<Snapshot>>([]);
        public Task<Snapshot> CreateAsync(IReadOnlyCollection<Guid> serverIds, DateTimeOffset startedAtUtc, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task CompleteAsync(Snapshot snapshot, IReadOnlyCollection<CollectorRun> collectorRuns, DateTimeOffset finishedAtUtc, int succeededServerCount, int failedServerCount, bool cancelled, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task FailAsync(Snapshot snapshot, IReadOnlyCollection<CollectorRun> collectorRuns, DateTimeOffset finishedAtUtc, int succeededServerCount, int failedServerCount, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    // ---- a real /bin/sh with a fake `docker` ------------------------------------------------------------------------------

    private sealed class FakeDocker
    {
        public string PsOut { get; init; } = string.Empty;
        public string PsErr { get; init; } = string.Empty;
        public string InspectOut { get; init; } = string.Empty;
        public string InspectErr { get; init; } = string.Empty;
        public int InspectRc { get; init; }
    }

    /// <summary>
    /// Runs a remote command line in a local POSIX shell. <paramref name="docker"/> null means Docker is not installed; the
    /// fake `docker` shadows any real one. Nothing is sent over SSH and nothing on this machine is touched.
    /// </summary>
    private static (int ExitCode, string Output) RunShell(string command, FakeDocker? docker)
    {
        var directory = Directory.CreateTempSubdirectory("dva-fake-docker-");
        try
        {
            var grep = new[] { "/usr/bin/grep", "/bin/grep" }.First(File.Exists);
            File.CreateSymbolicLink(Path.Combine(directory.FullName, "grep"), grep);
            if (docker is not null)
            {
                var script = Path.Combine(directory.FullName, "docker");
                File.WriteAllText(script, """
                    #!/bin/sh
                    case "$1" in
                      ps) if [ -n "$PS_ERR" ]; then echo "$PS_ERR" >&2; exit 1; fi; printf '%s\n' "$PS_OUT"; exit 0;;
                      inspect) [ -n "$INSPECT_ERR" ] && echo "$INSPECT_ERR" >&2; [ -n "$INSPECT_OUT" ] && printf '%s\n' "$INSPECT_OUT"; exit "${INSPECT_RC:-0}";;
                    esac
                    exit 99
                    """);
                if (!OperatingSystem.IsWindows())
                    File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }

            var info = new ProcessStartInfo("/bin/sh") { RedirectStandardOutput = true, RedirectStandardError = true };
            info.ArgumentList.Add("-c");
            info.ArgumentList.Add(command);
            info.Environment.Clear();
            info.Environment["PATH"] = directory.FullName;
            info.Environment["PS_OUT"] = docker?.PsOut ?? string.Empty;
            info.Environment["PS_ERR"] = docker?.PsErr ?? string.Empty;
            info.Environment["INSPECT_OUT"] = docker?.InspectOut ?? string.Empty;
            info.Environment["INSPECT_ERR"] = docker?.InspectErr ?? string.Empty;
            info.Environment["INSPECT_RC"] = (docker?.InspectRc ?? 0).ToString(System.Globalization.CultureInfo.InvariantCulture);
            using var process = Process.Start(info) ?? throw new InvalidOperationException("sh did not start.");
            var output = process.StandardOutput.ReadToEndAsync();
            process.StandardError.ReadToEnd();
            Assert.True(process.WaitForExit(20_000), "the shell did not finish");
            return (process.ExitCode, output.GetAwaiter().GetResult());
        }
        finally
        {
            directory.Delete(true);
        }
    }
}
