using System.Net.Sockets;
using DevOpsAgent.Infrastructure.Ssh;
using Renci.SshNet.Common;

namespace DevOpsAgent.Tests;

public sealed class SshTransportFailureTests
{
    [Fact]
    public void OnlyBrokenTransportsInvalidateTheSharedConnection()
    {
        Assert.True(SshTransportFailure.IsTransportFailure(new SshConnectionException("closed")));
        Assert.True(SshTransportFailure.IsTransportFailure(new SocketException()));
        Assert.True(SshTransportFailure.IsTransportFailure(new ObjectDisposedException("client")));
        // A command timeout must not dispose the client shared with concurrent commands and jump tunnels.
        Assert.False(SshTransportFailure.IsTransportFailure(new SshOperationTimeoutException("command timed out")));
        Assert.False(SshTransportFailure.IsTransportFailure(new InvalidOperationException("other")));
        Assert.False(SshTransportFailure.IsTransportFailure(new OperationCanceledException()));
    }
}
