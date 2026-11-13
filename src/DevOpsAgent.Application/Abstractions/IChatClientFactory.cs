using DevOpsAgent.Domain.Llm;
using Microsoft.Extensions.AI;

namespace DevOpsAgent.Application.Abstractions;

public interface IChatClientFactory
{
    IChatClient Create(LlmProvider provider);
}
