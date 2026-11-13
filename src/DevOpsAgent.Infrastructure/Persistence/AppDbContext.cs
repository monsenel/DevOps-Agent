using DevOpsAgent.Domain.Settings;
using DevOpsAgent.Domain.Llm;
using Microsoft.EntityFrameworkCore;

namespace DevOpsAgent.Infrastructure.Persistence;

public sealed class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<AppSetting> AppSettings => Set<AppSetting>();
    public DbSet<LlmProvider> LlmProviders => Set<LlmProvider>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<AppSetting>(entity =>
        {
            entity.HasKey(setting => setting.Key);
            entity.Property(setting => setting.Key).HasMaxLength(200);
            entity.Property(setting => setting.Value).IsRequired();
        });

        modelBuilder.Entity<LlmProvider>(entity =>
        {
            entity.HasKey(provider => provider.Id);
            entity.Property(provider => provider.Name).HasMaxLength(120).IsRequired();
            entity.Property(provider => provider.BaseUrl).HasMaxLength(1000).IsRequired();
            entity.Property(provider => provider.Model).HasMaxLength(300).IsRequired();
            entity.Property(provider => provider.ProtectedApiKey).HasMaxLength(4096);
            entity.Property(provider => provider.LastTestError).HasMaxLength(500);
            entity.Property(provider => provider.Type).HasConversion<string>().HasMaxLength(40);
            entity.HasIndex(provider => provider.IsDefault).IsUnique().HasFilter("\"IsDefault\" = 1");
        });
    }
}
