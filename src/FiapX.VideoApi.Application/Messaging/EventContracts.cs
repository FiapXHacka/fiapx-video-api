namespace FiapX.VideoApi.Application.Messaging;

/// <summary>Event types (routing keys) from docs/contracts/asyncapi.yaml in the fiapx repo.</summary>
public static class EventTypes
{
    public const string VideoUploaded = "video.uploaded";
    public const string VideoProcessingStarted = "video.processing-started";
    public const string VideoProcessed = "video.processed";
    public const string VideoProcessingFailed = "video.processing-failed";
}

public sealed record StorageRef(string Bucket, string Key, long? SizeBytes = null);

public sealed record ProcessingOptions(double Fps);

/// <summary>Published by video-api when the upload is confirmed.</summary>
public sealed record VideoUploadedData(
    Guid VideoId,
    Guid UserId,
    string FileName,
    string ContentType,
    long SizeBytes,
    StorageRef Storage,
    ProcessingOptions Options);

/// <summary>Consumed: published by video-processor.</summary>
public sealed record VideoProcessingStartedData(Guid VideoId, Guid UserId, int Attempt, DateTimeOffset StartedAt);

/// <summary>Consumed: published by video-processor.</summary>
public sealed record VideoProcessedData(
    Guid VideoId,
    Guid UserId,
    int FrameCount,
    double? DurationSeconds,
    StorageRef Result,
    long? ProcessingMs);

/// <summary>Consumed: published by video-processor (or by the video-api reconciler).</summary>
public sealed record VideoProcessingFailedData(
    Guid VideoId,
    Guid UserId,
    string ErrorCode,
    string ErrorMessage,
    int? Attempt);
