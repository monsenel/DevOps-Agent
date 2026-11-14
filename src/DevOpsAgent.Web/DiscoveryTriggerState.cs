namespace DevOpsAgent.Web;

public sealed class DiscoveryTriggerState
{
    public event Action? RunRequested;
    public event Action? Changed;

    public bool IsRunning { get; private set; }

    public void RequestRun() => RunRequested?.Invoke();

    public void SetRunning(bool isRunning)
    {
        if (IsRunning == isRunning) return;
        IsRunning = isRunning;
        Changed?.Invoke();
    }
}
