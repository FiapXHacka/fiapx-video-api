using System.Net;
using System.Text.Json;
using FiapX.VideoApi.Domain.Videos;
using FiapX.VideoApi.IntegrationTests.Infrastructure;
using FiapX.VideoApi.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace FiapX.VideoApi.IntegrationTests;

[Collection(PlatformTests.Name)]
public sealed class HostTests(PlatformFixture fixture)
{
    [Fact]
    public async Task Liveness_does_not_depend_on_anything()
    {
        using var client = fixture.Factory.CreateClient();

        var response = await client.GetAsync(new Uri("/health/live", UriKind.Relative), TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Readiness_checks_postgres_and_rabbitmq()
    {
        using var client = fixture.Factory.CreateClient();

        var response = await client.GetAsync(new Uri("/health/ready", UriKind.Relative), TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        body.RootElement.GetProperty("status").GetString().ShouldBe("Healthy");
        body.RootElement.GetProperty("checks").GetProperty("postgres").GetString().ShouldBe("Healthy");
        body.RootElement.GetProperty("checks").GetProperty("rabbitmq").GetString().ShouldBe("Healthy");
    }

    [Fact]
    public async Task Migration_creates_the_schema_of_db_schema_sql()
    {
        await using var connection = new NpgsqlConnection(fixture.PostgresConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);

        var tables = await QueryAsync(connection, "SELECT table_name FROM information_schema.tables WHERE table_schema = 'public' AND table_name <> '__EFMigrationsHistory'");
        tables.ShouldBe(["inbox_messages", "outbox_messages", "videos"], ignoreOrder: true);

        var indexes = await QueryAsync(connection, "SELECT indexname FROM pg_indexes WHERE schemaname = 'public'");
        indexes.ShouldContain("ix_videos_user_created");
        indexes.ShouldContain("ix_videos_open_status");
        indexes.ShouldContain("ix_outbox_pending");

        var checks = await QueryAsync(connection, "SELECT conname FROM pg_constraint WHERE contype = 'c'");
        checks.ShouldContain("videos_status_check");
        checks.ShouldContain("videos_size_bytes_check");
        checks.ShouldContain("videos_completed_has_result");
        checks.ShouldContain("videos_failed_has_error");

        var payloadType = await QueryAsync(connection, "SELECT data_type FROM information_schema.columns WHERE table_name = 'outbox_messages' AND column_name = 'payload'");
        payloadType.ShouldBe(["jsonb"]);
    }

    [Fact]
    public async Task Video_round_trips_and_updates_bump_the_version()
    {
        var now = DateTimeOffset.UtcNow;
        var video = Video.CreatePendingUpload(Guid.CreateVersion7(), Guid.CreateVersion7(), "a.mp4", "video/mp4", 10, "uploads/a", now);

        await using (var scope = fixture.Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<VideoDbContext>();
            db.Videos.Add(video);
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using (var scope = fixture.Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<VideoDbContext>();
            var loaded = await db.Videos.SingleAsync(v => v.Id == video.Id, TestContext.Current.CancellationToken);
            loaded.Status.ShouldBe(VideoStatus.PendingUpload);
            loaded.Version.ShouldBe(0);

            db.Entry(loaded).Property(v => v.ContentType).CurrentValue = "video/quicktime";
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
            loaded.Version.ShouldBe(1);
        }

        await using var connection = new NpgsqlConnection(fixture.PostgresConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        var status = await QueryAsync(connection, $"SELECT status FROM videos WHERE id = '{video.Id}'");
        status.ShouldBe(["PENDING_UPLOAD"]);
    }

    [Fact]
    public async Task Concurrent_updates_are_detected()
    {
        var video = Video.CreatePendingUpload(Guid.CreateVersion7(), Guid.CreateVersion7(), "b.mp4", "video/mp4", 10, "uploads/b", DateTimeOffset.UtcNow);
        await using (var scope = fixture.Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<VideoDbContext>();
            db.Videos.Add(video);
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using var scopeA = fixture.Factory.Services.CreateAsyncScope();
        await using var scopeB = fixture.Factory.Services.CreateAsyncScope();
        var dbA = scopeA.ServiceProvider.GetRequiredService<VideoDbContext>();
        var dbB = scopeB.ServiceProvider.GetRequiredService<VideoDbContext>();
        var a = await dbA.Videos.SingleAsync(v => v.Id == video.Id, TestContext.Current.CancellationToken);
        var b = await dbB.Videos.SingleAsync(v => v.Id == video.Id, TestContext.Current.CancellationToken);

        dbA.Entry(a).Property(v => v.ContentType).CurrentValue = "video/x-matroska";
        await dbA.SaveChangesAsync(TestContext.Current.CancellationToken);
        dbB.Entry(b).Property(v => v.ContentType).CurrentValue = "video/x-msvideo";

        await Should.ThrowAsync<DbUpdateConcurrencyException>(() => dbB.SaveChangesAsync(TestContext.Current.CancellationToken));
    }

    private static async Task<List<string>> QueryAsync(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        var values = new List<string>();
        while (await reader.ReadAsync(TestContext.Current.CancellationToken))
        {
            values.Add(reader.GetString(0));
        }

        return values;
    }
}
