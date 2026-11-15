using System.Net.Sockets;
using DevOpsAgent.Application.Abstractions;
using Renci.SshNet.Common;

namespace DevOpsAgent.Infrastructure.Ssh;

/// <summary>
/// Sorts a failed connection attempt into "the login failed" (<see cref="SshAuthFailedException"/>) and a real
/// connection-level failure, and describes either in words that are safe to show and store (T7.R19). Nothing here ever
/// copies a library's message: those can name key files, users or addresses, and a fake or future library could put anything
/// in them.
/// </summary>
public static class SshFailureClassifier
{
    /// <summary>
    /// The login failure behind <paramref name="exception"/>, or null when the attempt failed at connection level. Covers
    /// what the SSH.NET transport throws for a rejected key and what loading the key file throws for a missing, unreadable,
    /// invalid or passphrase-protected key. The result carries no inner exception and no message of the original.
    /// </summary>
    public static SshAuthFailedException? AsAuthFailure(Exception exception) => exception switch
    {
        SshAuthFailedException already => already,
        SshAuthenticationException => new SshAuthFailedException(SshAuthFailure.ServerRejectedKey),
        SshPassPhraseNullOrEmptyException => new SshAuthFailedException(SshAuthFailure.PassphraseRequired),
        FileNotFoundException or DirectoryNotFoundException => new SshAuthFailedException(SshAuthFailure.KeyFileNotFound),
        UnauthorizedAccessException => new SshAuthFailedException(SshAuthFailure.KeyFileUnreadable),
        _ => null
    };

    /// <summary>
    /// The safe, short cause of a failed attempt for the owner: "connection timed out", "connection refused", "key file not
    /// found"... A type name at most for anything unrecognised, never a message.
    /// </summary>
    public static string DescribeCause(Exception exception)
    {
        if (AsAuthFailure(exception) is { } auth) return auth.Message;
        return exception switch
        {
            SshHostKeyMismatchException => "host key mismatch (the presented key does not match the stored key)",
            SshOperationTimeoutException or TimeoutException => "connection timed out",
            SocketException socket => DescribeSocket(socket.SocketErrorCode),
            SshConnectionException => "the SSH connection was closed or the SSH handshake failed",
            // A jump tunnel that does not start (SshNetTransportFactory), the one InvalidOperationException a connect throws.
            InvalidOperationException => "the connection or the jump tunnel could not be set up",
            OperationCanceledException => "connection timed out",
            _ => $"connection failed ({exception.GetType().Name})"
        };
    }

    private static string DescribeSocket(SocketError error) => error switch
    {
        SocketError.ConnectionRefused => "connection refused",
        SocketError.TimedOut => "connection timed out",
        SocketError.HostNotFound or SocketError.NoData or SocketError.TryAgain => "host name could not be resolved",
        SocketError.HostUnreachable or SocketError.NetworkUnreachable or SocketError.NetworkDown => "no route to host",
        SocketError.ConnectionReset or SocketError.ConnectionAborted => "connection reset by the peer",
        _ => "network error"
    };
}
