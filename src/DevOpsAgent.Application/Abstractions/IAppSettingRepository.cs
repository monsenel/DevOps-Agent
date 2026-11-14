namespace DevOpsAgent.Application.Abstractions;

public interface IAppSettingRepository
{
    Task<string?> GetAsync(string key, CancellationToken cancellationToken);
    Task SetAsync(string key, string value, CancellationToken cancellationToken);
}
