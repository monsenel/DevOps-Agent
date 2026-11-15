namespace DevOpsAgent.Application.Abstractions;

/// <summary>
/// The one place that turns a command into what is actually sent over SSH, so approval previews show the exact text that runs.
/// </summary>
public static class RemoteCommand
{
    /// <summary>The sudo capability probe of a server without a stored sudo password.</summary>
    public const string SudoProbe = "sudo -n true";

    /// <summary>
    /// The sudo capability probe of a server with a stored sudo password (T7.R24): the password goes to stdin, never on the
    /// command line. <c>LC_ALL=C</c> keeps sudo's "Sorry, try again." untranslated so a rejected password is recognised.
    /// </summary>
    public const string SudoPasswordProbe = "LC_ALL=C sudo -S -p '' true";

    public static string Wrap(string command, bool useSudo) =>
        useSudo ? $"sudo -n -- sh -c {ShellQuote(command)}" : command;

    /// <summary>
    /// The command as it runs on a server with a stored sudo password: <c>sudo -S -p ''</c> reads the password from stdin
    /// (written by the runner, never part of this text). sudo logs the same <c>sh -c '…'</c> command as with <c>sudo -n</c>.
    /// </summary>
    public static string WrapWithSudoPassword(string command) =>
        $"LC_ALL=C sudo -S -p '' -- sh -c {ShellQuote(command)}";

    /// <summary>The text an approval preview shows for a command on a server, with the wrapper that server's sudo mode uses.</summary>
    public static string Display(string command, bool useSudo, bool sudoPassword) =>
        useSudo && sudoPassword ? WrapWithSudoPassword(command) : Wrap(command, useSudo);

    public static string ShellQuote(string value) => $"'{value.Replace("'", "'\\''", StringComparison.Ordinal)}'";
}
