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
            .Where(name => !string.Equals(name, "known_hosts", StringComparison.OrdinalIgnoreCase))
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public bool Contains(string fileName) =>
        string.Equals(Path.GetFileName(fileName), fileName, StringComparison.Ordinal)
        && File.Exists(Path.Combine(_directory, fileName));

    public string GetFullPath(string fileName)
    {
        if (!Contains(fileName))
        {
            throw new FileNotFoundException("The selected SSH identity file was not found in /keys.", fileName);
        }

        return Path.Combine(_directory, fileName);
    }
}
