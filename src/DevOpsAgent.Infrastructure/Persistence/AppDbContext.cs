using DevOpsAgent.Domain.Settings;
using DevOpsAgent.Domain.Llm;
using DevOpsAgent.Domain.Chat;
using DevOpsAgent.Domain.Servers;
using DevOpsAgent.Domain.Discovery;
using DevOpsAgent.Domain.Graph;
using DevOpsAgent.Domain.Agent;
using DevOpsAgent.Domain.Updates;
using DevOpsAgent.Domain.Monitoring;
using Microsoft.EntityFrameworkCore;

namespace DevOpsAgent.Infrastructure.Persistence;

public sealed class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<AppSetting> AppSettings => Set<AppSetting>();
    public DbSet<LlmProvider> LlmProviders => Set<LlmProvider>();
    public DbSet<ChatConversation> ChatConversations => Set<ChatConversation>();
    public DbSet<ChatMessageRecord> ChatMessages => Set<ChatMessageRecord>();
    public DbSet<ChatAttachment> ChatAttachments => Set<ChatAttachment>();
    public DbSet<Server> Servers => Set<Server>();
    public DbSet<Snapshot> Snapshots => Set<Snapshot>();
    public DbSet<CollectorRun> CollectorRuns => Set<CollectorRun>();
    public DbSet<GraphNode> GraphNodes => Set<GraphNode>();
    public DbSet<GraphEdge> GraphEdges => Set<GraphEdge>();
    public DbSet<GraphListener> GraphListeners => Set<GraphListener>();
    public DbSet<GraphEvidence> GraphEvidence => Set<GraphEvidence>();
    public DbSet<ActionRecord> ActionRecords => Set<ActionRecord>();
    public DbSet<UpdatePackage> UpdatePackages => Set<UpdatePackage>();
    public DbSet<UpdateScan> UpdateScans => Set<UpdateScan>();
    public DbSet<MetricSample> MetricSamples => Set<MetricSample>();
    public DbSet<MetricRollup> MetricRollups => Set<MetricRollup>();
    public DbSet<HealthCheck> HealthChecks => Set<HealthCheck>();
    public DbSet<Alert> Alerts => Set<Alert>();

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
            // No database default: Low is the enum's CLR default and would be taken for "not set" on insert.
            entity.Property(conversation => conversation.Effort).HasConversion<string>().HasMaxLength(16);
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
            entity.HasMany(message => message.Attachments)
                .WithOne()
                .HasForeignKey(attachment => attachment.MessageId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ChatAttachment>(entity =>
        {
            entity.HasKey(attachment => attachment.Id);
            entity.Property(attachment => attachment.FileName).HasMaxLength(255).IsRequired();
            entity.Property(attachment => attachment.ContentType).HasMaxLength(100).IsRequired();
            entity.Property(attachment => attachment.Kind).HasConversion<string>().HasMaxLength(16);
            entity.Property(attachment => attachment.Content).IsRequired();
        });

        modelBuilder.Entity<Server>(entity =>
        {
            entity.HasKey(server => server.Id);
            entity.Property(server => server.Name).HasMaxLength(120).IsRequired();
            entity.Property(server => server.Host).HasMaxLength(253).IsRequired();
            entity.Property(server => server.User).HasMaxLength(120).IsRequired();
            entity.Property(server => server.KeyFile).HasMaxLength(260).IsRequired();
            entity.Property(server => server.ProtectedKeyPassphrase).HasMaxLength(4096);
            entity.Property(server => server.ProtectedSudoPassword).HasMaxLength(4096);
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

        modelBuilder.Entity<GraphNode>(entity =>
        {
            entity.HasKey(node => node.Id);
            entity.Property(node => node.Key).HasMaxLength(500).IsRequired();
            entity.Property(node => node.Kind).HasConversion<string>().HasMaxLength(24).IsRequired();
            entity.Property(node => node.Name).HasMaxLength(300).IsRequired();
            entity.Property(node => node.AttributesJson).IsRequired();
            entity.HasIndex(node => new { node.SnapshotId, node.Key }).IsUnique();
            entity.HasOne<Snapshot>().WithMany().HasForeignKey(node => node.SnapshotId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<GraphEdge>(entity =>
        {
            entity.HasKey(edge => edge.Id);
            entity.Property(edge => edge.Kind).HasConversion<string>().HasMaxLength(24).IsRequired();
            entity.Property(edge => edge.Protocol).HasMaxLength(20);
            entity.HasIndex(edge => new { edge.SnapshotId, edge.SourceNodeId, edge.TargetNodeId, edge.Kind });
            entity.HasOne<Snapshot>().WithMany().HasForeignKey(edge => edge.SnapshotId).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne<GraphNode>().WithMany().HasForeignKey(edge => edge.SourceNodeId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<GraphNode>().WithMany().HasForeignKey(edge => edge.TargetNodeId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<GraphListener>(entity =>
        {
            entity.HasKey(listener => listener.Id);
            entity.Property(listener => listener.Address).HasMaxLength(128).IsRequired();
            entity.Property(listener => listener.Protocol).HasMaxLength(20).IsRequired();
            entity.Property(listener => listener.ProcessName).HasMaxLength(300);
            entity.HasIndex(listener => new { listener.SnapshotId, listener.Port, listener.Protocol });
            entity.HasOne<Snapshot>().WithMany().HasForeignKey(listener => listener.SnapshotId).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne<GraphNode>().WithMany().HasForeignKey(listener => listener.NodeId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<GraphEvidence>(entity =>
        {
            entity.HasKey(item => item.Id);
            entity.Property(item => item.Collector).HasMaxLength(120).IsRequired();
            entity.Property(item => item.Description).HasMaxLength(1000).IsRequired();
            entity.HasIndex(item => item.SnapshotId);
            entity.HasOne<Snapshot>().WithMany().HasForeignKey(item => item.SnapshotId).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne<GraphNode>().WithMany().HasForeignKey(item => item.NodeId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<GraphEdge>().WithMany().HasForeignKey(item => item.EdgeId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<ActionRecord>(entity =>
        {
            entity.HasKey(action => action.Id);
            entity.Property(action => action.ToolName).HasMaxLength(80).IsRequired();
            entity.Property(action => action.Group).HasMaxLength(500).IsRequired();
            entity.Property(action => action.ServerIdsJson).IsRequired();
            entity.Property(action => action.ArgumentsJson).IsRequired();
            entity.Property(action => action.Status).HasConversion<string>().HasMaxLength(24).IsRequired();
            entity.Property(action => action.ApprovedBy).HasMaxLength(120);
            entity.Property(action => action.CommandsJson).IsRequired();
            entity.Property(action => action.ResultsJson).IsRequired();
            entity.HasIndex(action => new { action.Status, action.CreatedAtUtc });
        });

        modelBuilder.Entity<UpdatePackage>(entity =>
        {
            entity.HasKey(item => item.Id);
            entity.Property(item => item.Name).HasMaxLength(256).IsRequired();
            entity.Property(item => item.CurrentVersion).HasMaxLength(256).IsRequired();
            entity.Property(item => item.CandidateVersion).HasMaxLength(256).IsRequired();
            entity.Property(item => item.Repository).HasMaxLength(256).IsRequired();
            entity.HasIndex(item => new { item.ServerId, item.Name }).IsUnique();
            entity.HasOne<Server>().WithMany().HasForeignKey(item => item.ServerId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<UpdateScan>(entity =>
        {
            entity.HasKey(item => item.ServerId);
            entity.Property(item => item.PackageManager).HasMaxLength(24);
            entity.Property(item => item.Error).HasMaxLength(2000);
            entity.HasOne<Server>().WithOne().HasForeignKey<UpdateScan>(item => item.ServerId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<MetricSample>(entity =>
        {
            entity.HasKey(sample => sample.Id);
            entity.Property(sample => sample.Metric).HasMaxLength(120).IsRequired();
            entity.HasIndex(sample => new { sample.ServerId, sample.Metric, sample.AtUtc });
            entity.HasOne<Server>().WithMany().HasForeignKey(sample => sample.ServerId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<MetricRollup>(entity =>
        {
            entity.HasKey(sample => sample.Id);
            entity.Property(sample => sample.Metric).HasMaxLength(120).IsRequired();
            entity.HasIndex(sample => new { sample.ServerId, sample.Metric, sample.BucketStartUtc }).IsUnique();
            entity.HasOne<Server>().WithMany().HasForeignKey(sample => sample.ServerId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<HealthCheck>(entity =>
        {
            entity.HasKey(item => item.Id);
            entity.Property(item => item.Name).HasMaxLength(120).IsRequired();
            entity.Property(item => item.Type).HasConversion<string>().HasMaxLength(16).IsRequired();
            entity.Property(item => item.Target).HasMaxLength(2048).IsRequired();
            entity.Property(item => item.LastStatus).HasConversion<string>().HasMaxLength(16).IsRequired();
            entity.Property(item => item.LastError).HasMaxLength(500);
            entity.HasIndex(item => new { item.ServerId, item.IsEnabled });
            entity.HasOne<Server>().WithMany().HasForeignKey(item => item.ServerId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<Alert>(entity =>
        {
            entity.HasKey(item => item.Id);
            entity.Property(item => item.Key).HasMaxLength(500).IsRequired();
            entity.Property(item => item.Rule).HasMaxLength(80).IsRequired();
            entity.Property(item => item.Severity).HasConversion<string>().HasMaxLength(16).IsRequired();
            entity.Property(item => item.State).HasConversion<string>().HasMaxLength(16).IsRequired();
            entity.Property(item => item.Target).HasMaxLength(500).IsRequired();
            entity.Property(item => item.Title).HasMaxLength(240).IsRequired();
            entity.Property(item => item.Details).HasMaxLength(2000).IsRequired();
            entity.HasIndex(item => new { item.State, item.LastSeenAtUtc });
            entity.HasIndex(item => item.Key).IsUnique();
            entity.HasOne<Server>().WithMany().HasForeignKey(item => item.ServerId).OnDelete(DeleteBehavior.SetNull);
        });
    }
}
