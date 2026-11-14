using DevOpsAgent.Application.Abstractions;
using DevOpsAgent.Domain.Settings;
using Microsoft.EntityFrameworkCore;

namespace DevOpsAgent.Infrastructure.Persistence;

public sealed class AppSettingRepository(AppDbContext dbContext) : IAppSettingRepository
{
    public async Task<string?> GetAsync(string key, CancellationToken cancellationToken) =>
        await dbContext.AppSettings.AsNoTracking()
            .Where(setting => setting.Key == key)
            .Select(setting => setting.Value)
            .SingleOrDefaultAsync(cancellationToken);

    public async Task SetAsync(string key, string value, CancellationToken cancellationToken)
    {
        var setting = await dbContext.AppSettings.SingleOrDefaultAsync(item => item.Key == key, cancellationToken);
        if (setting is null)
        {
            dbContext.AppSettings.Add(new AppSetting { Key = key, Value = value });
        }
        else
        {
            setting.Value = value;
        }

        await dbContext.SaveChangesAsync(cancellationToken);
    }
}
