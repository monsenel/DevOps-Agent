using DevOpsAgent.Application.Llm;

namespace DevOpsAgent.Web;

public sealed class ProviderStatusState
{
    public LlmProviderSummary? DefaultProvider { get; private set; }

    public event Action? Changed;

    public void Set(LlmProviderSummary? provider)
    {
        DefaultProvider = provider;
        Changed?.Invoke();
    }
}
