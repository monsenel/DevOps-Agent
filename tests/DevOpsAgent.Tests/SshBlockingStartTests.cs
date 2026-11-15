using System.Diagnostics;

namespace DevOpsAgent.Tests;

/// <summary>
/// Review of T7.R17: SSH.NET opens the channel of a command synchronously, so on a session whose peer is frozen the "async"
/// <c>ExecuteAsync</c> blocks its caller for up to the connect timeout before it returns a task. The monitoring cycles start
/// their hosts one after another from one thread (<c>Task.WhenAll(hosts.Select(async ...))</c>), and on a pooled client
/// nothing yields before the command starts, so one blocked start held back every host after it and a cycle ran past its
/// deadline (live: 31 s alert cycles against a 25 s deadline and a 30 s interval with the jump host frozen). The pool must
/// start a command off the caller's thread.
/// </summary>
public sealed class SshBlockingStartTests
{
    [Fact]
    public async Task HostsWhoseCommandStartBlocksTheThreadAreStillStartedTogether()
    {
        const int hosts = 8;
        var block = TimeSpan.FromMilliseconds(400);
        using var lab = new HangLab(connectHeadroom: HangLab.ConnectHeadroom);
        var servers = Enumerable.Range(1, hosts).Select(index => lab.Add($"frozen-{index}", $"10.9.0.{index}")).ToArray();

        // Warm the pool: from here on every call finds a connected client, so nothing yields before the command starts.
        foreach (var server in servers) await lab.Run(server, TimeSpan.FromSeconds(10));
        foreach (var server in servers)
        {
            var script = lab.Transports.Script(server.Host);
            script.Commands = CommandMode.BlockSynchronously;
            script.BlockFor = block;
        }

        var stopwatch = Stopwatch.StartNew();
        // The shape of a monitoring cycle: every host's work is created in one pass on this thread.
        await Task.WhenAll(servers.Select(async server => await lab.Run(server, TimeSpan.FromSeconds(10))));
        stopwatch.Stop();

        // Started one after another this takes hosts * block (3.2 s); started together it takes about one block.
        Assert.True(stopwatch.Elapsed < block * (hosts / 2), $"the hosts were started one after another: {stopwatch.ElapsedMilliseconds} ms");
        Assert.Equal(0, lab.Pool.ActiveLeases);
    }
}
