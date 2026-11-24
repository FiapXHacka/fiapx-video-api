using System.Text;
using System.Text.Json;
using FiapX.VideoApi.Application.Messaging;
using FiapX.VideoApi.Infrastructure.Messaging;
using FiapX.VideoApi.Infrastructure.Persistence;
using FiapX.VideoApi.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RabbitMQ.Client;

namespace FiapX.VideoApi.IntegrationTests;

[Collection(PlatformTests.Name)]
public sealed class MessagingTests(PlatformFixture fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Outbox_relay_publishes_committed_events_with_confirms()
    {
        await using var connection = await fixture.ConnectToBrokerAsync();
        await using var channel = await connection.CreateChannelAsync(cancellationToken: Ct);
        // Plays the role of the video-processor queue, which does not exist in this test.
        var probe = await channel.QueueDeclareAsync(queue: "", durable: false, exclusive: true, autoDelete: true, cancellationToken: Ct);
        await channel.QueueBindAsync(probe.QueueName, "fiapx.events", EventTypes.VideoUploaded, cancellationToken: Ct);

        var videoId = Guid.CreateVersion7();
        var data = new VideoUploadedData(videoId, Guid.CreateVersion7(), "a.mp4", "video/mp4", 10,
            new StorageRef("bucket", "uploads/a"), new ProcessingOptions(1));
        await using (var scope = fixture.Factory.Services.CreateAsyncScope())
        {
            scope.ServiceProvider.GetRequiredService<IOutbox>().Enqueue(EventTypes.VideoUploaded, videoId.ToString(), data);
            await scope.ServiceProvider.GetRequiredService<VideoDbContext>().SaveChangesAsync(Ct);
        }

        var delivery = await Eventually.ReturnsAsync(
            () => channel.BasicGetAsync(probe.QueueName, autoAck: true, Ct), d => d is not null);

        delivery!.BasicProperties.Persistent.ShouldBeTrue();
        delivery.BasicProperties.ContentType.ShouldBe("application/cloudevents+json");
        delivery.BasicProperties.Type.ShouldBe(EventTypes.VideoUploaded);
        delivery.RoutingKey.ShouldBe(EventTypes.VideoUploaded);
        var envelope = JsonSerializer.Deserialize<CloudEvent<VideoUploadedData>>(delivery.Body.Span, CloudEventJson.Options)!;
        envelope.Data.VideoId.ShouldBe(videoId);
        envelope.Source.ShouldBe("fiapx/video-api");
        delivery.BasicProperties.MessageId.ShouldBe(envelope.Id.ToString());

        await using var verify = fixture.Factory.Services.CreateAsyncScope();
        var row = await verify.ServiceProvider.GetRequiredService<VideoDbContext>()
            .OutboxMessages.AsNoTracking().SingleAsync(m => m.Id == envelope.Id, Ct);
        row.PublishedAt.ShouldNotBeNull();
    }

    [Fact]
    public async Task Consumer_handles_each_message_once_and_records_the_inbox()
    {
        var videoId = Guid.CreateVersion7();
        var messageId = Guid.CreateVersion7();
        var body = ProcessedEvent(messageId, videoId);

        await PublishAsync(EventTypes.VideoProcessed, body);
        await Eventually.ReturnsAsync(() => Task.FromResult(Handled(videoId)), count => count == 1);

        // Redelivery of the same message (same CloudEvent id) must not reach the handler again.
        await PublishAsync(EventTypes.VideoProcessed, body);
        await Eventually.ReturnsAsync(() => InboxCountAsync(messageId), count => count == 1);
        await Eventually.ReturnsAsync(() => QueueDepthAsync(RabbitMqTopology.StatusQueue), depth => depth == 0);
        Handled(videoId).ShouldBe(1);
    }

    [Fact]
    public async Task Unknown_types_and_envelopes_without_id_are_acked_and_not_dead_lettered()
    {
        // Other tests (the poison one) may already have dead-lettered messages: compare with the baseline.
        var deadLettersBefore = await QueueDepthAsync(RabbitMqTopology.StatusDeadLetterQueue);

        var started = JsonSerializer.SerializeToUtf8Bytes(new
        {
            specversion = "1.0",
            id = Guid.CreateVersion7(),
            source = "fiapx/video-processor",
            type = EventTypes.VideoProcessingStarted,
            time = DateTimeOffset.UtcNow,
            data = new { videoId = Guid.CreateVersion7(), userId = Guid.CreateVersion7(), attempt = 1, startedAt = DateTimeOffset.UtcNow },
        });
        await PublishAsync(EventTypes.VideoProcessingStarted, started);
        await PublishAsync(EventTypes.VideoProcessed, Encoding.UTF8.GetBytes("{ not json"));

        await Eventually.ReturnsAsync(() => QueueDepthAsync(RabbitMqTopology.StatusQueue), depth => depth == 0);
        (await QueueDepthAsync(RabbitMqTopology.StatusDeadLetterQueue)).ShouldBe(deadLettersBefore);
    }

    [Fact]
    public async Task Failing_consumer_is_retried_MaxHandlerAttempts_times_then_rejected_to_the_dlq_without_looping()
    {
        var messageId = Guid.CreateVersion7();
        var deadLettersBefore = await QueueDepthAsync(RabbitMqTopology.StatusDeadLetterQueue);
        var attemptsBefore = fixture.PoisonAttempts;

        await PublishAsync(EventTypes.VideoProcessed, ProcessedEvent(messageId, PlatformFixture.PoisonVideoId));

        await Eventually.ReturnsAsync(
            () => QueueDepthAsync(RabbitMqTopology.StatusDeadLetterQueue), depth => depth == deadLettersBefore + 1, TimeSpan.FromSeconds(30));
        (fixture.PoisonAttempts - attemptsBefore).ShouldBe(3, "RabbitMq:MaxHandlerAttempts");

        // No redelivery loop: after the reject, the consumer never sees the message again.
        await Task.Delay(TimeSpan.FromSeconds(2), Ct);
        (fixture.PoisonAttempts - attemptsBefore).ShouldBe(3);
        (await QueueDepthAsync(RabbitMqTopology.StatusQueue)).ShouldBe(0u);
        (await InboxCountAsync(messageId)).ShouldBe(0, "a failed consumer must roll back its transaction");
    }

    [Fact]
    public async Task Invalid_data_for_a_known_type_goes_to_the_dlq_immediately()
    {
        var deadLettersBefore = await QueueDepthAsync(RabbitMqTopology.StatusDeadLetterQueue);
        var attemptsBefore = fixture.PoisonAttempts;
        var body = JsonSerializer.SerializeToUtf8Bytes(new
        {
            specversion = "1.0",
            id = Guid.CreateVersion7(),
            source = "fiapx/video-processor",
            type = EventTypes.VideoProcessed,
            time = DateTimeOffset.UtcNow,
            data = new { videoId = "not-a-guid", frameCount = "three" },
        });

        await PublishAsync(EventTypes.VideoProcessed, body);

        await Eventually.ReturnsAsync(
            () => QueueDepthAsync(RabbitMqTopology.StatusDeadLetterQueue), depth => depth == deadLettersBefore + 1, TimeSpan.FromSeconds(15));
        fixture.PoisonAttempts.ShouldBe(attemptsBefore, "the consumer is never called for invalid data");
    }

    private static byte[] ProcessedEvent(Guid messageId, Guid videoId) => JsonSerializer.SerializeToUtf8Bytes(new
    {
        specversion = "1.0",
        id = messageId,
        source = "fiapx/video-processor",
        type = EventTypes.VideoProcessed,
        time = DateTimeOffset.UtcNow,
        subject = videoId.ToString(),
        data = new
        {
            videoId,
            userId = Guid.CreateVersion7(),
            frameCount = 3,
            result = new { bucket = "b", key = "results/x/frames.zip", sizeBytes = 10 },
        },
    });

    private int Handled(Guid videoId) => fixture.HandledProcessed.Count(e => e.Data.VideoId == videoId);

    private async Task PublishAsync(string routingKey, byte[] body)
    {
        await using var connection = await fixture.ConnectToBrokerAsync();
        await using var channel = await connection.CreateChannelAsync(
            new CreateChannelOptions(publisherConfirmationsEnabled: true, publisherConfirmationTrackingEnabled: true), Ct);
        await channel.BasicPublishAsync("fiapx.events", routingKey, mandatory: true,
            new BasicProperties { Persistent = true }, body, Ct);
    }

    private async Task<uint> QueueDepthAsync(string queue)
    {
        await using var connection = await fixture.ConnectToBrokerAsync();
        await using var channel = await connection.CreateChannelAsync(cancellationToken: Ct);
        var ok = await channel.QueueDeclarePassiveAsync(queue, Ct);
        return ok.MessageCount;
    }

    private async Task<int> InboxCountAsync(Guid messageId)
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<VideoDbContext>()
            .InboxMessages.CountAsync(m => m.MessageId == messageId, Ct);
    }
}
