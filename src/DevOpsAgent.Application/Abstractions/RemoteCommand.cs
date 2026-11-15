namespace DevOpsAgent.Application.Abstractions;

/// <summary>
/// The one place that turns a command into what is actually sent over SSH, so approval previews show the exact text that runs.
/// </summary>
public static class RemoteCommand
{
    public static string Wrap(string command, bool useSudo) =>
        useSudo ? $"sudo -n -- sh -c {ShellQuote(command)}" : command;

    public static string ShellQuote(string value) => $"'{value.Replace("'", "'\\''", StringComparison.Ordinal)}'";
}
