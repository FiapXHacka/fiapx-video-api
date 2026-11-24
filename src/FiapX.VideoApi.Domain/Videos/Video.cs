namespace FiapX.VideoApi.Domain.Videos;

/// <summary>
/// Persistence skeleton of the Video aggregate. Only creation is modelled here; the state machine
/// (PENDING_UPLOAD → UPLOADED → PROCESSING → COMPLETED | FAILED, EXPIRED) comes with issue #2.
/// </summary>
public sealed class Video
{
    public const int MaxFileNameLength = 255;

    private Video()
    {
        OriginalFileName = string.Empty;
        ContentType = string.Empty;
        StorageKey = string.Empty;
    }

    public Guid Id { get; private set; }
    public Guid UserId { get; private set; }
    public string OriginalFileName { get; private set; }
    public string ContentType { get; private set; }
    public long SizeBytes { get; private set; }
    public string StorageKey { get; private set; }
    public VideoStatus Status { get; private set; }
    public int? FrameCount { get; private set; }
    public string? ResultKey { get; private set; }
    public long? ResultSizeBytes { get; private set; }
    public string? ErrorCode { get; private set; }
    public string? ErrorMessage { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset? UploadedAt { get; private set; }
    public DateTimeOffset? ProcessingStartedAt { get; private set; }
    public DateTimeOffset? FinishedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }

    /// <summary>Optimistic concurrency token, incremented on every update.</summary>
    public int Version { get; private set; }

    public static Video CreatePendingUpload(
        Guid id,
        Guid userId,
        string originalFileName,
        string contentType,
        long sizeBytes,
        string storageKey,
        DateTimeOffset now)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("Id is required.", nameof(id));
        }

        if (userId == Guid.Empty)
        {
            throw new ArgumentException("User id is required.", nameof(userId));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(originalFileName);
        ArgumentException.ThrowIfNullOrWhiteSpace(contentType);
        ArgumentException.ThrowIfNullOrWhiteSpace(storageKey);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sizeBytes);
        if (originalFileName.Length > MaxFileNameLength)
        {
            throw new ArgumentException($"File name must have at most {MaxFileNameLength} characters.", nameof(originalFileName));
        }

        return new Video
        {
            Id = id,
            UserId = userId,
            OriginalFileName = originalFileName,
            ContentType = contentType,
            SizeBytes = sizeBytes,
            StorageKey = storageKey,
            Status = VideoStatus.PendingUpload,
            CreatedAt = now,
            UpdatedAt = now,
        };
    }
}
