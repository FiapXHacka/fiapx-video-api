namespace FiapX.VideoApi.Domain.Videos;

/// <summary>
/// Lifecycle of a video (docs/architecture/05-dados.md). Persisted as SCREAMING_SNAKE_CASE text.
/// The transitions between these states are implemented in issue #2.
/// </summary>
public enum VideoStatus
{
    PendingUpload,
    Uploaded,
    Processing,
    Completed,
    Failed,
    Expired,
}
