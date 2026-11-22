namespace DevOpsAgent.Domain.Chat;

/// <summary>
/// Per-conversation chat execution mode chosen by the owner in the chat header dropdown (D-066).
/// Server group ReadOnly policies always take precedence and can never be bypassed by chat mode.
/// </summary>
public enum ChatMode
{
    /// <summary>Only read-only inspection tools are available; mutating tools are never exposed or proposed.</summary>
    ReadOnly = 0,

    /// <summary>All tools are available; every mutating call creates an approval card before running (default).</summary>
    Confirm = 1,

    /// <summary>
    /// All tools are available; safe typed tools (edit_config, restart_container, non-critical restart_service,
    /// block_ip, unblock_ip) auto-execute on Confirm-mode server groups and are logged in Action log.
    /// High-risk tools (run_shell, apply_updates, critical service restarts) still require explicit approval.
    /// </summary>
    Auto = 2
}
