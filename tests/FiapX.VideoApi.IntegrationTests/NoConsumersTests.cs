using System.Text.Json;
using FiapX.VideoApi.Application.Messaging;
using FiapX.VideoApi.Infrastructure.Messaging;
using FiapX.VideoApi.IntegrationTests.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using RabbitMQ.Client;
using Testcontainers.PostgreSql;
using Testcontainers.RabbitMq;

namespace FiapX.VideoApi.IntegrationTests;

/// <summary>Host with no event consumers registered (the scaffold as deployed), on its own broker and database.</summary>
public sealed class NoConsumersFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder(TestImages.Postgres).WithDatabase("video_db").Build();
    private readonly RabbitMqContainer _rabbit = new RabbitMqBuilder(TestImages.RabbitMq).Build();

    public WebApplicationFactory<Program> Factory { get; private set; } = null!;

    public async ValueTask InitializeAsync()
    {
        await Task.WhenAll(_postgres.StartAsync(), _rabbit.StartAsync());
        Factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("ConnectionStrings:Postgres", _postgres.GetConnectionString());
            builder.UseSetting("RabbitMq:Uri", _rabbit.GetConnectionString());
            builder.UseSetting("Auth:Issuer", FakeIdentity.Issuer);
            builder.UseSetting("Auth:MetadataAddress", FakeIdentity.MetadataAddress);
            builder.UseSetting("Auth:RequireHttpsMetadata", "false");
            builder.UseSetting("Database:MigrateOnStartup", "true");
        });
        _ = Factory.Server;
    }

    public async Task<IConnection> ConnectToBrokerAsync() =>
        await new ConnectionFactory { Uri = new Uri(_rabbit.GetConnectionString()) }.CreateConnectionAsync();

    public async ValueTask DisposeAsync()
    {
        if (Factory is not null)
        {
            await Factory.DisposeAsync();
        }

        await _rabbit.DisposeAsync();
        await _postgres.DisposeAsync();
    }
}

public sealed class NoConsumersTests(NoConsumersFixture fixture) : IClassFixture<NoConsumersFixture>
{
    [Fact]
    public async Task Without_consumers_the_queue_is_declared_but_not_consumed_and_messages_wait()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var connection = await fixture.ConnectToBrokerAsync();
        await using var channel = await connection.CreateChannelAsync(
            new CreateChannelOptions(publisherConfirmationsEnabled: true, publisherConfirmationTrackingEnabled: true), ct);

        // Declared at startup even without consumers (passive declare throws if the queue does not exist).
        var queue = await Eventually.ReturnsAsync(
            async () =>
            {
                try
                {
                    await using var probe = await connection.CreateChannelAsync(cancellationToken: ct);
                    return await probe.QueueDeclarePassiveAsync(RabbitMqTopology.StatusQueue, ct);
                }
                catch (RabbitMQ.Client.Exceptions.OperationInterruptedException)
                {
                    return null;
                }
            },
            ok => ok is not null);
        queue!.ConsumerCount.ShouldBe(0u);

        var body = JsonSerializer.SerializeToUtf8Bytes(new
        {
            specversion = "1.0",
            id = Guid.CreateVersion7(),
            source = "fiapx/video-processor",
            type = EventTypes.VideoProcessed,
            time = DateTimeOffset.UtcNow,
            data = new { videoId = Guid.CreateVersion7(), userId = Guid.CreateVersion7(), frameCount = 1, result = new { bucket = "b", key = "k" } },
        });
        await channel.BasicPublishAsync("fiapx.events", EventTypes.VideoProcessed, mandatory: true, new BasicProperties { Persistent = true }, body, ct);
        await Task.Delay(TimeSpan.FromSeconds(1), ct);

        var after = await channel.QueueDeclarePassiveAsync(RabbitMqTopology.StatusQueue, ct);
        after.ConsumerCount.ShouldBe(0u);
        after.MessageCount.ShouldBe(1u, "nothing is dropped while there is no consumer");
        (await channel.QueueDeclarePassiveAsync(RabbitMqTopology.StatusDeadLetterQueue, ct)).MessageCount.ShouldBe(0u);
    }
}
