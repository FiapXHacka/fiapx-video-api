using System.Text.Json;
using FiapX.VideoApi.Application.Messaging;
using FiapX.VideoApi.Infrastructure.Messaging;

namespace FiapX.VideoApi.UnitTests.Infrastructure;

public sealed class CloudEventTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 23, 18, 4, 5, TimeSpan.Zero);

    [Fact]
    public void OutboxMessageFactory_builds_a_cloudevent_matching_the_contract()
    {
        var videoId = Guid.Parse("01936f5e-7f00-7aaa-8bbb-ccccdddd0001");
        var userId = Guid.Parse("01936f50-0000-7000-8000-000000000042");
        var data = new VideoUploadedData(
            videoId, userId, "pitch.mp4", "video/mp4", 62914560,
            new StorageRef("fiapx-videos-dev", "uploads/u/v/original.mp4"), new ProcessingOptions(1));
        const string traceParent = "00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01";

        var message = OutboxMessageFactory.Create(EventTypes.VideoUploaded, videoId.ToString(), data, "fiapx/video-api", Now, traceParent);

        message.Type.ShouldBe("video.uploaded");
        message.OccurredAt.ShouldBe(Now);
        message.PublishedAt.ShouldBeNull();
        message.Attempts.ShouldBe(0);
        message.Id.Version.ShouldBe(7);

        using var json = JsonDocument.Parse(message.Payload);
        var root = json.RootElement;
        root.GetProperty("specversion").GetString().ShouldBe("1.0");
        root.GetProperty("id").GetGuid().ShouldBe(message.Id);
        root.GetProperty("source").GetString().ShouldBe("fiapx/video-api");
        root.GetProperty("type").GetString().ShouldBe("video.uploaded");
        root.GetProperty("time").GetDateTimeOffset().ShouldBe(Now);
        root.GetProperty("subject").GetString().ShouldBe(videoId.ToString());
        root.GetProperty("dataschema").GetString().ShouldBe("fiapx/video.uploaded/v1");
        root.GetProperty("datacontenttype").GetString().ShouldBe("application/json");
        root.GetProperty("traceparent").GetString().ShouldBe(traceParent);

        var payload = root.GetProperty("data");
        payload.GetProperty("videoId").GetGuid().ShouldBe(videoId);
        payload.GetProperty("userId").GetGuid().ShouldBe(userId);
        payload.GetProperty("fileName").GetString().ShouldBe("pitch.mp4");
        payload.GetProperty("sizeBytes").GetInt64().ShouldBe(62914560);
        payload.GetProperty("storage").GetProperty("bucket").GetString().ShouldBe("fiapx-videos-dev");
        payload.GetProperty("storage").TryGetProperty("sizeBytes", out _).ShouldBeFalse("nulls are omitted");
        payload.GetProperty("options").GetProperty("fps").GetDouble().ShouldBe(1);
    }

    [Fact]
    public void OutboxMessageFactory_omits_traceparent_when_there_is_no_activity()
    {
        var message = OutboxMessageFactory.Create("video.uploaded", "s", new { a = 1 }, "fiapx/video-api", Now, traceParent: null);

        using var json = JsonDocument.Parse(message.Payload);
        json.RootElement.TryGetProperty("traceparent", out _).ShouldBeFalse();
    }

    [Theory]
    [InlineData("", "s", "src")]
    [InlineData("t", " ", "src")]
    [InlineData("t", "s", "")]
    public void OutboxMessageFactory_requires_type_subject_and_source(string type, string subject, string source)
    {
        Should.Throw<ArgumentException>(() => OutboxMessageFactory.Create(type, subject, new { a = 1 }, source, Now, null));
    }

    [Fact]
    public void Consumed_events_from_the_processor_deserialize()
    {
        // Example in the shape the Go video-processor publishes (docs/contracts/asyncapi.yaml).
        const string json = """
        {
          "specversion": "1.0",
          "id": "0193a1b2-0000-7000-8000-00000000abcd",
          "source": "fiapx/video-processor",
          "type": "video.processed",
          "time": "2026-11-23T18:10:00Z",
          "subject": "01936f5e-7f00-7aaa-8bbb-ccccdddd0001",
          "dataschema": "fiapx/video.processed/v1",
          "traceparent": "00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01",
          "unknownExtension": "ignored",
          "data": {
            "videoId": "01936f5e-7f00-7aaa-8bbb-ccccdddd0001",
            "userId": "01936f50-0000-7000-8000-000000000042",
            "frameCount": 180,
            "durationSeconds": 180.04,
            "result": { "bucket": "fiapx-videos-dev", "key": "results/u/v/frames.zip", "sizeBytes": 450000000 },
            "processingMs": 41000
          }
        }
        """;

        var envelope = JsonSerializer.Deserialize<CloudEvent<VideoProcessedData>>(json, CloudEventJson.Options);

        envelope.ShouldNotBeNull();
        envelope.Type.ShouldBe(EventTypes.VideoProcessed);
        envelope.DataContentType.ShouldBe("application/json");
        envelope.Data.FrameCount.ShouldBe(180);
        envelope.Data.Result.Key.ShouldBe("results/u/v/frames.zip");
        envelope.Data.Result.SizeBytes.ShouldBe(450000000);
    }

    [Fact]
    public void Failed_event_deserializes()
    {
        const string json = """
        {"specversion":"1.0","id":"0193a1b2-0000-7000-8000-00000000abce","source":"fiapx/video-processor",
         "type":"video.processing-failed","time":"2026-11-23T18:10:00Z",
         "data":{"videoId":"01936f5e-7f00-7aaa-8bbb-ccccdddd0001","userId":"01936f50-0000-7000-8000-000000000042",
                 "errorCode":"INVALID_VIDEO","errorMessage":"o arquivo enviado não é um vídeo válido","attempt":1}}
        """;

        var envelope = JsonSerializer.Deserialize<CloudEvent<VideoProcessingFailedData>>(json, CloudEventJson.Options);

        envelope!.Data.ErrorCode.ShouldBe("INVALID_VIDEO");
        envelope.Subject.ShouldBeNull();
    }

    [Fact]
    public void Envelope_without_required_fields_is_rejected()
    {
        const string json = """{"specversion":"1.0","source":"x","type":"video.processed","time":"2026-11-23T18:10:00Z","data":{}}""";

        Should.Throw<JsonException>(() => JsonSerializer.Deserialize<CloudEvent<JsonElement>>(json, CloudEventJson.Options));
    }
}
