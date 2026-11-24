using FiapX.VideoApi.Domain.Videos;
using FiapX.VideoApi.Infrastructure.Messaging;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FiapX.VideoApi.Infrastructure.Persistence;

// Mappings mirror db/schema.sql (and fiapx/docs/database/02-video_db.sql): names, defaults, checks and indexes.

internal sealed class VideoConfiguration : IEntityTypeConfiguration<Video>
{
    public void Configure(EntityTypeBuilder<Video> builder)
    {
        builder.ToTable("videos", t =>
        {
            t.HasCheckConstraint("videos_size_bytes_check", "size_bytes > 0");
            t.HasCheckConstraint("videos_status_check", VideoStatusConverter.CheckConstraintSql);
            t.HasCheckConstraint("videos_completed_has_result", "status <> 'COMPLETED' OR result_key IS NOT NULL");
            t.HasCheckConstraint("videos_failed_has_error", "status <> 'FAILED' OR error_code IS NOT NULL");
        });

        builder.HasKey(v => v.Id);
        builder.Property(v => v.Id).ValueGeneratedNever();
        builder.Property(v => v.OriginalFileName).IsRequired();
        builder.Property(v => v.ContentType).IsRequired();
        builder.Property(v => v.StorageKey).IsRequired();
        builder.Property(v => v.Status)
            .HasConversion(new VideoStatusConverter())
            .HasDefaultValueSql("'PENDING_UPLOAD'")
            .IsRequired();
        builder.Property(v => v.CreatedAt).HasDefaultValueSql("now()");
        builder.Property(v => v.UpdatedAt).HasDefaultValueSql("now()");
        builder.Property(v => v.Version).HasDefaultValue(0).IsConcurrencyToken();

        builder.HasIndex(v => new { v.UserId, v.CreatedAt })
            .IsDescending(false, true)
            .HasDatabaseName("ix_videos_user_created");
        builder.HasIndex(v => new { v.Status, v.UpdatedAt })
            .HasFilter("status IN ('PENDING_UPLOAD','UPLOADED','PROCESSING')")
            .HasDatabaseName("ix_videos_open_status");
    }
}

internal sealed class OutboxMessageConfiguration : IEntityTypeConfiguration<OutboxMessage>
{
    public void Configure(EntityTypeBuilder<OutboxMessage> builder)
    {
        builder.ToTable("outbox_messages");
        builder.HasKey(m => m.Id);
        builder.Property(m => m.Id).ValueGeneratedNever();
        builder.Property(m => m.Type).IsRequired();
        builder.Property(m => m.Payload).HasColumnType("jsonb").IsRequired();
        builder.Property(m => m.OccurredAt).HasDefaultValueSql("now()");
        builder.Property(m => m.Attempts).HasDefaultValue(0);
        builder.HasIndex(m => m.OccurredAt)
            .HasFilter("published_at IS NULL")
            .HasDatabaseName("ix_outbox_pending");
    }
}

internal sealed class InboxMessageConfiguration : IEntityTypeConfiguration<InboxMessage>
{
    public void Configure(EntityTypeBuilder<InboxMessage> builder)
    {
        builder.ToTable("inbox_messages");
        builder.HasKey(m => new { m.MessageId, m.Consumer });
        builder.Property(m => m.Consumer).IsRequired();
        builder.Property(m => m.ProcessedAt).HasDefaultValueSql("now()");
    }
}
