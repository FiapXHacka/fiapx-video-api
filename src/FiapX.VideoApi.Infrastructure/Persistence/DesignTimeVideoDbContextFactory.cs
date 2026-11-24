using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace FiapX.VideoApi.Infrastructure.Persistence;

/// <summary>Used only by <c>dotnet ef</c> to create migrations; never at runtime.</summary>
internal sealed class DesignTimeVideoDbContextFactory : IDesignTimeDbContextFactory<VideoDbContext>
{
    public VideoDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<VideoDbContext>()
            .UseNpgsql("Host=localhost;Database=video_db")
            .UseSnakeCaseNamingConvention()
            .Options;
        return new VideoDbContext(options, TimeProvider.System);
    }
}
