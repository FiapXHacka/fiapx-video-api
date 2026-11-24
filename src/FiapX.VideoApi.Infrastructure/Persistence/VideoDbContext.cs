using FiapX.VideoApi.Domain.Videos;
using FiapX.VideoApi.Infrastructure.Messaging;
using Microsoft.EntityFrameworkCore;

namespace FiapX.VideoApi.Infrastructure.Persistence;

/// <summary>EF Core context of <c>video_db</c> (owned only by video-api, ADR-0009).</summary>
public sealed class VideoDbContext(DbContextOptions<VideoDbContext> options, TimeProvider timeProvider) : DbContext(options)
{
    public DbSet<Video> Videos => Set<Video>();
    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();
    public DbSet<InboxMessage> InboxMessages => Set<InboxMessage>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(VideoDbContext).Assembly);
    }

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        TouchModifiedVideos();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        TouchModifiedVideos();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    // Every update bumps the optimistic concurrency token and updated_at, so two consumers applying
    // events to the same video concurrently cannot silently overwrite each other.
    private void TouchModifiedVideos()
    {
        var now = timeProvider.GetUtcNow();
        foreach (var entry in ChangeTracker.Entries<Video>().Where(e => e.State == EntityState.Modified))
        {
            entry.Property(v => v.Version).CurrentValue = entry.Property(v => v.Version).OriginalValue + 1;
            entry.Property(v => v.UpdatedAt).CurrentValue = now;
        }
    }
}
