using DevOpsAgent.Domain.Settings;
using DevOpsAgent.Domain.Llm;
using DevOpsAgent.Domain.Chat;
using DevOpsAgent.Domain.Servers;
using DevOpsAgent.Domain.Discovery;
using Microsoft.EntityFrameworkCore;

namespace DevOpsAgent.Infrastructure.Persistence;

public sealed class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<AppSetting> AppSettings => Set<AppSetting>();
    public DbSet<LlmProvider> LlmProviders => Set<LlmProvider>();
    public DbSet<ChatConversation> ChatConversations => Set<ChatConversation>();
    public DbSet<ChatMessageRecord> ChatMessages => Set<ChatMessageRecord>();
    public DbSet<Server> Servers => Set<Server>();
    public DbSet<Snapshot> Snapshots => Set<Snapshot>();
    public DbSet<CollectorRun> CollectorRuns => Set<CollectorRun>();

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

        modelBuilder.Entity<ChatConversation>(entity =>
        {
            entity.HasKey(conversation => conversation.Id);
            entity.Property(conversation => conversation.Title).HasMaxLength(120).IsRequired();
            entity.HasMany(conversation => conversation.Messages)
                .WithOne(message => message.Conversation)
                .HasForeignKey(message => message.ConversationId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ChatMessageRecord>(entity =>
        {
            entity.HasKey(message => message.Id);
            entity.Property(message => message.Role).HasMaxLength(20).IsRequired();
            entity.Property(message => message.Content).IsRequired();
            entity.HasIndex(message => new { message.ConversationId, message.CreatedAtUtc });
        });

        modelBuilder.Entity<Server>(entity =>
        {
            entity.HasKey(server => server.Id);
            entity.Property(server => server.Name).HasMaxLength(120).IsRequired();
            entity.Property(server => server.Host).HasMaxLength(253).IsRequired();
            entity.Property(server => server.User).HasMaxLength(120).IsRequired();
            entity.Property(server => server.KeyFile).HasMaxLength(260).IsRequired();
            entity.Property(server => server.ProtectedKeyPassphrase).HasMaxLength(4096);
            entity.Property(server => server.Group).HasMaxLength(80).IsRequired();
            entity.Property(server => server.TagsJson).IsRequired();
            entity.Property(server => server.OsInfo).HasMaxLength(500);
            entity.HasIndex(server => server.Name).IsUnique();
            entity.HasOne<Server>()
                .WithMany()
                .HasForeignKey(server => server.JumpServerId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<Snapshot>(entity =>
        {
            entity.HasKey(snapshot => snapshot.Id);
            entity.Property(snapshot => snapshot.Status).HasConversion<string>().HasMaxLength(24).IsRequired();
            entity.Property(snapshot => snapshot.ServerIdsJson).IsRequired();
            entity.HasIndex(snapshot => snapshot.StartedAtUtc);
        });

        modelBuilder.Entity<CollectorRun>(entity =>
        {
            entity.HasKey(run => run.Id);
            entity.Property(run => run.CollectorName).HasMaxLength(120).IsRequired();
            entity.Property(run => run.Status).HasConversion<string>().HasMaxLength(24).IsRequired();
            entity.Property(run => run.RawOutput).IsRequired();
            entity.Property(run => run.StandardError).IsRequired();
            entity.Property(run => run.Error).HasMaxLength(2000);
            entity.Property(run => run.ResultJson);
            entity.HasIndex(run => new { run.SnapshotId, run.ServerId });
            entity.HasOne<Snapshot>().WithMany().HasForeignKey(run => run.SnapshotId).OnDelete(DeleteBehavior.Cascade);
        });
    }
}
