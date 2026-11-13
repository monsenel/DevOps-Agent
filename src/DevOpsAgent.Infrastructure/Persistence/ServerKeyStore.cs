using DevOpsAgent.Application.Abstractions;
using Microsoft.Extensions.Options;

namespace DevOpsAgent.Infrastructure.Persistence;

public sealed class ServerKeyStore(IOptions<ServerInventoryOptions> options) : IServerKeyStore
{
    private readonly string _directory = Path.GetFullPath(options.Value.KeyDirectory);

    public IReadOnlyList<string> ListFileNames()
    {
        if (!Directory.Exists(_directory))
        {
            return [];
        }

        return Directory.EnumerateFiles(_directory)
            .Select(Path.GetFileName)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => name!)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public bool Contains(string fileName) =>
        string.Equals(Path.GetFileName(fileName), fileName, StringComparison.Ordinal)
        && File.Exists(Path.Combine(_directory, fileName));
}
