using System.Collections.Concurrent;
using System.Security.Cryptography;
using DevOpsAgent.Application.Abstractions;
using DevOpsAgent.Domain.Servers;
using DevOpsAgent.Infrastructure.Ssh;
using Renci.SshNet;

namespace DevOpsAgent.Tests;

/// <summary>
/// T7.R19: closing the pool (shutdown) never throws into the host, whatever it races with (a connect in flight, a lease being
/// released, a second Dispose, new calls, an edit), and never leaves a client open. The assertion that matters in every test
/// is the same two: no exception other than the clean shutdown cancellation, and no live client on the fake transport.
/// </summary>
public sealed class SshPoolShutdownRaceTests
{
    private static readonly TimeSpan Long = TimeSpan.FromSeconds(60);

    [Fact]
    public async Task DisposeDuringAConnectInFlightEndsEveryCallerCleanlyAndTheLateConnectionIsClosed()
    {
        var lab = new HangLab();
        var gate = new TaskCompletionSource();
        // A connect that ignores cancellation and finishes after the pool is gone, as SSH.NET can.
        var host = lab.Add("web", "10.0.0.1", script => { script.ConnectGate = gate.Task; script.ConnectHonoursToken = false; });
        var calls = Enumerable.Range(0, 6).Select(_ => Task.Run(() => lab.Run(host, Long))).ToArray();
        await WaitUntil(() => lab.Pool.ConnectWaiters(host.Id) == 6 && lab.Transports.Attempts(host) == 1);

        lab.Pool.Dispose();

        foreach (var call in calls)
            await Assert.ThrowsAsync<SshShutdownException>(() => call.WaitAsync(TimeSpan.FromSeconds(20)));
        gate.SetResult();
        await WaitUntil(() => lab.Transports.Created.Count == 1);
        await WaitUntil(() => lab.Transports.LiveClients == 0);
        Assert.Equal(0, lab.Pool.LiveClients);
        Assert.Null(lab.Reachability.Get(host.Id));
    }

    [Fact]
    public async Task DisposeDuringAConnectBehindAJumpHostClosesBothTheTunnelAndTheLateConnection()
    {
        var lab = new HangLab();
        var edge = lab.Add("edge", "10.0.0.1");
        var gate = new TaskCompletionSource();
        var app = lab.Add("app", "10.0.0.2", script => { script.ConnectGate = gate.Task; script.ConnectHonoursToken = false; }, edge);
        var call = Task.Run(() => lab.Run(app, Long));
        await WaitUntil(() => lab.Pool.ConnectWaiters(app.Id) == 1 && lab.Transports.Attempts(app) == 1);

        lab.Pool.Dispose();

        await Assert.ThrowsAsync<SshShutdownException>(() => call.WaitAsync(TimeSpan.FromSeconds(20)));
        gate.SetResult();
        await WaitUntil(() => lab.Transports.LiveClients == 0);
        Assert.Equal(0, lab.Pool.LiveClients);
        Assert.True(lab.Transports.CreatedFor(edge).All(transport => transport.Disposed));
    }

    [Fact]
    public async Task DisposeTwiceAndFromManyThreadsAtOnceNeverThrowsAndClosesEverything()
    {
        var lab = new HangLab();
        var servers = Enumerable.Range(1, 6).Select(index => lab.Add($"srv-{index}", $"10.0.0.{index}")).ToArray();
        var edge = lab.Add("edge", "10.0.1.1");
        var behind = lab.Add("behind", "10.0.1.2", jump: edge);
        var sessions = new List<ISshCommandSession>();
        foreach (var server in servers.Append(behind))
            sessions.Add(await lab.Pool.GetSessionAsync(server.Id, false, CancellationToken.None));

        var errors = new ConcurrentBag<Exception>();
        using var start = new ManualResetEventSlim();
        var threads = Enumerable.Range(0, 16).Select(_ => Task.Run(() =>
        {
            start.Wait();
            try { lab.Pool.Dispose(); } catch (Exception exception) { errors.Add(exception); }
        })).ToArray();
        start.Set();
        await Task.WhenAll(threads);
        lab.Pool.Dispose();
        lab.Pool.Dispose();

        Assert.Empty(errors);
        Assert.Equal(0, lab.Transports.LiveClients);
        Assert.Equal(0, lab.Pool.LiveClients);
        // Leases ended after the pool is gone are harmless, twice over as well.
        foreach (var session in sessions) { session.Dispose(); session.Dispose(); }
        Assert.Equal(0, lab.Pool.ActiveLeases);
        // Everything that follows is a clean refusal, never an ObjectDisposedException.
        await Assert.ThrowsAsync<SshShutdownException>(() => lab.Run(servers[0], TimeSpan.FromSeconds(1)));
        lab.Pool.Evict(servers[0].Id);
        Assert.Equal(0, lab.Pool.ConnectWaiters(servers[0].Id));
    }

    [Fact]
    public async Task DisposeWhileLeasesAreBeingReleasedNeverThrows()
    {
        var errors = new ConcurrentBag<Exception>();
        for (var round = 0; round < 60; round++)
        {
            var lab = new HangLab();
            var edge = lab.Add("edge", "10.0.1.1");
            var servers = Enumerable.Range(1, 4).Select(index => lab.Add($"srv-{index}", $"10.0.2.{index}", jump: index % 2 == 0 ? edge : null)).ToArray();
            var sessions = new List<ISshCommandSession>();
            foreach (var server in servers)
            {
                sessions.Add(await lab.Pool.GetSessionAsync(server.Id, false, CancellationToken.None));
                sessions.Add(await lab.Pool.GetSessionAsync(server.Id, false, CancellationToken.None));
            }

            using var start = new ManualResetEventSlim();
            var releasers = sessions.Select((session, index) => Task.Run(() =>
            {
                start.Wait();
                try
                {
                    if (index % 3 == 0) lab.Pool.Evict(servers[index % servers.Length].Id);
                    session.Dispose();
                }
                catch (Exception exception) { errors.Add(exception); }
            })).ToList();
            releasers.Add(Task.Run(() =>
            {
                start.Wait();
                try { lab.Pool.Dispose(); } catch (Exception exception) { errors.Add(exception); }
            }));
            start.Set();
            await Task.WhenAll(releasers);
            lab.Pool.Dispose();

            Assert.Equal(0, lab.Transports.LiveClients);
            Assert.Equal(0, lab.Pool.LiveClients);
        }

        Assert.Empty(errors);
    }

    [Fact]
    public async Task DisposeRacingNewCallsCommandsAndEditsOnlyEverProducesTheCleanShutdownCancellation()
    {
        var unexpected = new ConcurrentBag<Exception>();
        for (var round = 0; round < 40; round++)
        {
            var lab = new HangLab();
            var edge = lab.Add("edge", "10.0.1.1", script => script.ConnectDelay = TimeSpan.FromMilliseconds(round % 4));
            var servers = Enumerable.Range(1, 4).Select(index => lab.Add($"srv-{index}", $"10.0.2.{index}",
                script => script.ConnectDelay = TimeSpan.FromMilliseconds(index + round % 3), jump: index % 2 == 0 ? edge : null)).ToArray();
            using var stop = new CancellationTokenSource();
            var workers = Enumerable.Range(0, 12).Select(worker => Task.Run(async () =>
            {
                var random = new Random(worker + round * 100);
                while (!stop.IsCancellationRequested)
                {
                    var server = servers[random.Next(servers.Length)];
                    try
                    {
                        if (random.Next(5) == 0) { lab.Pool.Evict(server.Id); continue; }
                        using var session = await lab.Pool.GetSessionAsync(server.Id, random.Next(4) == 0, TimeSpan.FromSeconds(10), CancellationToken.None);
                        await session.ExecuteAsync("true", TimeSpan.FromSeconds(5), CancellationToken.None);
                    }
                    catch (SshShutdownException) { return; }
                    // The edit storm of this test: a caller told twice in a row that the settings changed while it
                    // connected is told to try again, as it always was; not a shutdown problem.
                    catch (InvalidOperationException exception) when (exception.Message.StartsWith("The server settings changed while connecting", StringComparison.Ordinal)
                        || exception.Message.StartsWith("The SSH connection to the server kept changing", StringComparison.Ordinal)) { }
                    catch (Exception exception) { unexpected.Add(exception); return; }
                }
            })).ToArray();

            await Task.Delay(5 + round % 7);
            lab.Pool.Dispose();
            await Task.WhenAll(workers).WaitAsync(TimeSpan.FromSeconds(10));
            stop.Cancel();

            // Late connections finish in the background and close what they opened.
            await WaitUntil(() => lab.Transports.LiveClients == 0);
            Assert.Equal(0, lab.Pool.LiveClients);
            Assert.Equal(0, lab.Pool.ActiveLeases);
        }

        Assert.Empty(unexpected.Select(exception => exception.ToString()));
    }

    [Fact]
    public async Task StoppingTheApplicationAndDisposingTheHostTogetherNeverThrowsEither()
    {
        var unexpected = new ConcurrentBag<Exception>();
        for (var round = 0; round < 30; round++)
        {
            var lab = new HangLab();
            var servers = Enumerable.Range(1, 5).Select(index => lab.Add($"srv-{index}", $"10.0.3.{index}",
                script => { script.ConnectDelay = TimeSpan.FromMilliseconds(index * 3); script.ConnectHonoursToken = index % 2 == 0; })).ToArray();
            var calls = servers.SelectMany(server => Enumerable.Range(0, 3).Select(_ => Task.Run(async () =>
            {
                try { await lab.Run(server, Long); }
                catch (SshShutdownException) { }
                catch (Exception exception) { unexpected.Add(exception); }
            }))).ToArray();

            await Task.Delay(round % 10);
            var stopper = Task.Run(() => lab.Lifetime.StopApplication());
            var disposer = Task.Run(() => lab.Pool.Dispose());
            await Task.WhenAll(stopper, disposer);
            await Task.WhenAll(calls).WaitAsync(TimeSpan.FromSeconds(30));

            await WaitUntil(() => lab.Transports.LiveClients == 0);
            Assert.Equal(0, lab.Pool.LiveClients);
        }

        Assert.Empty(unexpected.Select(exception => exception.ToString()));
    }

    [Fact]
    public async Task AClientThatFailsToCloseNeverStopsTheShutdownOrThrowsFromIt()
    {
        var lab = new HangLab();
        var broken = lab.Add("broken", "10.0.0.1", script => script.DisposeThrows = true);
        var fine = lab.Add("fine", "10.0.0.2");
        var behind = lab.Add("behind", "10.0.0.3", script => script.DisposeThrows = true, broken);
        using var one = await lab.Pool.GetSessionAsync(broken.Id, false, CancellationToken.None);
        using var two = await lab.Pool.GetSessionAsync(fine.Id, false, CancellationToken.None);
        using var three = await lab.Pool.GetSessionAsync(behind.Id, false, CancellationToken.None);

        var exception = Record.Exception(lab.Pool.Dispose);

        Assert.Null(exception);
        Assert.Equal(0, lab.Pool.LiveClients);
        Assert.True(lab.Transports.CreatedFor(fine).All(transport => transport.Disposed));
    }

    [Fact]
    public async Task ARetiredClientThatFailsToCloseDoesNotThrowIntoTheCallerThatEndsItsLastLease()
    {
        using var lab = new HangLab();
        var host = lab.Add("web", "10.0.0.1", script => script.DisposeThrows = true);
        var session = await lab.Pool.GetSessionAsync(host.Id, false, CancellationToken.None);
        lab.Pool.Evict(host.Id);

        var exception = Record.Exception(session.Dispose);

        Assert.Null(exception);
        Assert.Equal(0, lab.Pool.LiveClients);
    }

    [Fact]
    public void TheSshNetTransportCanBeDisposedTwiceAndFromManyThreadsWithoutThrowing()
    {
        using var rsa = RSA.Create(2048);
        using var keyStream = new MemoryStream(System.Text.Encoding.ASCII.GetBytes(rsa.ExportRSAPrivateKeyPem()));
        var transport = new SshNetTransport(new SshClient("127.0.0.1", 1, "nobody", new PrivateKeyFile(keyStream)),
            new PrivateKeyFile(new MemoryStream(System.Text.Encoding.ASCII.GetBytes(rsa.ExportRSAPrivateKeyPem()))), null, null);

        var errors = new ConcurrentBag<Exception>();
        Parallel.For(0, 16, _ =>
        {
            try { transport.Dispose(); } catch (Exception exception) { errors.Add(exception); }
        });
        transport.Dispose();

        Assert.Empty(errors);
        Assert.False(transport.IsConnected);
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        var timer = System.Diagnostics.Stopwatch.StartNew();
        while (!condition())
        {
            if (timer.Elapsed > TimeSpan.FromSeconds(30)) throw new TimeoutException("The condition was never met.");
            await Task.Delay(5);
        }
    }
}
