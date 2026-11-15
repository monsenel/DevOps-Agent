namespace DevOpsAgent.Application.Abstractions;

/// <summary>
/// The one place that turns a command into what is actually sent over SSH, so approval previews show the exact text that runs.
/// </summary>
public static class RemoteCommand
{
    /// <summary>The sudo capability probe of a server without a stored sudo password.</summary>
    public const string SudoProbe = "sudo -n true";

    /// <summary>What approval previews show in place of the per-call random marker of a password sudo.</summary>
    public const string DisplayMarker = OutputMarker.Prefix + "<random>";

    public static string Wrap(string command, bool useSudo) =>
        useSudo ? $"sudo -n -- sh -c {ShellQuote(command)}" : command;

    /// <summary>
    /// The command as it runs on a server with a stored sudo password (T7.R24, T7.R25): <c>sudo -S -p ''</c> reads the password
    /// from stdin (written by the runner, never part of this text). Inside sudo the shell first detaches stdin
    /// (<c>exec &lt;/dev/null</c>: a command such as apt never reads a password line sudo left unread), then prints
    /// <paramref name="marker"/>; the password counts as accepted only when that line comes back. <c>LC_ALL=C</c> keeps sudo's
    /// messages untranslated and stays outside the <c>sh -c</c> argument.
    /// </summary>
    public static string WrapWithSudoPassword(string command, string marker) =>
        $"LC_ALL=C sudo -S -p '' -- sh -c {ShellQuote(SudoPasswordPreamble(marker) + command)}";

    /// <summary>The sudo capability probe of a server with a stored sudo password: <c>true</c> in the same wrapper.</summary>
    public static string SudoPasswordProbe(string marker) => WrapWithSudoPassword("true", marker);

    /// <summary>What the shell inside a password sudo runs before the command itself.</summary>
    public static string SudoPasswordPreamble(string marker) => $"exec </dev/null; printf \"%s\\n\" \"{marker}\"; ";

    /// <summary>The text an approval preview shows for a command on a server, with the wrapper that server's sudo mode uses.</summary>
    public static string Display(string command, bool useSudo, bool sudoPassword) =>
        useSudo && sudoPassword ? WrapWithSudoPassword(command, DisplayMarker) : Wrap(command, useSudo);

    public static string ShellQuote(string value) => $"'{value.Replace("'", "'\\''", StringComparison.Ordinal)}'";
}
