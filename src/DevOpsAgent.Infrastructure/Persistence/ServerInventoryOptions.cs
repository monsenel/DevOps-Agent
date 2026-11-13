namespace DevOpsAgent.Infrastructure.Persistence;

public sealed class ServerInventoryOptions
{
    public const string SectionName = "ServerInventory";

    public string KeyDirectory { get; set; } = "/keys";
}
