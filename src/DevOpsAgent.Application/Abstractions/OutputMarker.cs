using System.Security.Cryptography;

namespace DevOpsAgent.Application.Abstractions;

/// <summary>
/// A random line a command prints before anything else (T7.R25): a password sudo is confirmed only when sudo let the command
/// start, which is when this line comes back. The transport watches the output as it arrives, reports the marker the moment it
/// is seen (also when the command later fails or times out) and removes it from the output it returns.
/// </summary>
public sealed class OutputMarker
{
    public const string Prefix = "dva-sudo-ok-";

    private readonly TaskCompletionSource _seen = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private OutputMarker(string value) => Value = value;

    /// <summary>A fresh marker for one call: <see cref="Prefix"/> and 32 random hex digits.</summary>
    public static OutputMarker Create() => new(Prefix + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16)));

    public string Value { get; }

    public bool IsSeen => _seen.Task.IsCompleted;

    /// <summary>Completes when the marker line has come back.</summary>
    public Task Seen => _seen.Task;

    public void MarkSeen() => _seen.TrySetResult();

    /// <summary>
    /// Removes the marker line from the start of <paramref name="output"/> and reports it seen; output that does not start
    /// with it is returned unchanged.
    /// </summary>
    public string Strip(string output)
    {
        var line = Value + "\n";
        if (!output.StartsWith(line, StringComparison.Ordinal)) return output;
        MarkSeen();
        return output[line.Length..];
    }

    public override string ToString() => "output marker";
}
