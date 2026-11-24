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
    public const string UncheckedSuffix = "-unchecked";

    private const int InputArmed = 0;
    private const int InputSending = 1;
    private const int InputSent = 2;
    private const int InputAbandoned = 3;

    private readonly TaskCompletionSource _seen = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _passwordChecked = 1;
    private int _inputState = InputArmed;

    private OutputMarker(string value) => Value = value;

    /// <summary>A fresh marker for one call: <see cref="Prefix"/> and 32 random hex digits.</summary>
    public static OutputMarker Create() => new(Prefix + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16)));

    public string Value { get; }

    /// <summary>
    /// Printed instead of <see cref="Value"/> when the command started without sudo reading the password from stdin
    /// (for example a NOPASSWD rule or a cached sudo timestamp ticket, T7.R27).
    /// </summary>
    public string UncheckedValue => Value + UncheckedSuffix;

    public bool IsSeen => _seen.Task.IsCompleted;

    /// <summary>
    /// True when the marker confirmed that sudo actually consumed the password; false when sudo ran via NOPASSWD or a
    /// cached ticket without reading the password from stdin (T7.R27).
    /// </summary>
    public bool PasswordChecked => Volatile.Read(ref _passwordChecked) != 0;

    /// <summary>True once the transport began or finished writing the stdin password for this attempt (T7.R27).</summary>
    public bool WasInputSent => Volatile.Read(ref _inputState) is InputSending or InputSent;

    /// <summary>True when the attempt was abandoned before the transport could write the stdin password (T7.R27).</summary>
    public bool IsInputAbandoned => Volatile.Read(ref _inputState) == InputAbandoned;

    /// <summary>Completes when the marker line has come back.</summary>
    public Task Seen => _seen.Task;

    /// <summary>
    /// Transitions this attempt from armed to sending. Returns false if the attempt was already abandoned or sent, so a
    /// transport task blocked in channel open never writes a password after the pool abandoned the call (T7.R27).
    /// </summary>
    public bool TryBeginSendInput() => Interlocked.CompareExchange(ref _inputState, InputSending, InputArmed) == InputArmed;

    /// <summary>Marks that the password write finished.</summary>
    public void MarkInputSent() => Interlocked.CompareExchange(ref _inputState, InputSent, InputSending);

    /// <summary>
    /// Marks this attempt as abandoned before sending. Returns true if it was still armed (no password will be written);
    /// false if the transport had already begun or completed sending.
    /// </summary>
    public bool AbandonInput() => Interlocked.CompareExchange(ref _inputState, InputAbandoned, InputArmed) == InputArmed;

    public void MarkSeen(bool passwordChecked = true)
    {
        if (!passwordChecked) Volatile.Write(ref _passwordChecked, 0);
        _seen.TrySetResult();
    }

    /// <summary>
    /// Removes the marker line (checked or unchecked) from the start of <paramref name="output"/> and reports it seen;
    /// output that does not start with either is returned unchanged.
    /// </summary>
    public string Strip(string output)
    {
        var checkedLine = Value + "\n";
        if (output.StartsWith(checkedLine, StringComparison.Ordinal))
        {
            MarkSeen(passwordChecked: true);
            return output[checkedLine.Length..];
        }

        var uncheckedLine = UncheckedValue + "\n";
        if (output.StartsWith(uncheckedLine, StringComparison.Ordinal))
        {
            MarkSeen(passwordChecked: false);
            return output[uncheckedLine.Length..];
        }

        return output;
    }

    public override string ToString() => "output marker";
}
