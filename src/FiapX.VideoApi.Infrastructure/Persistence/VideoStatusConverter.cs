using System.Collections.Frozen;
using FiapX.VideoApi.Domain.Videos;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace FiapX.VideoApi.Infrastructure.Persistence;

/// <summary>Stores <see cref="VideoStatus"/> as the SCREAMING_SNAKE_CASE text used by the schema and the API.</summary>
internal sealed class VideoStatusConverter() : ValueConverter<VideoStatus, string>(
    status => ToDatabase(status),
    value => FromDatabase(value))
{
    private static readonly FrozenDictionary<VideoStatus, string> Names = new Dictionary<VideoStatus, string>
    {
        [VideoStatus.PendingUpload] = "PENDING_UPLOAD",
        [VideoStatus.Uploaded] = "UPLOADED",
        [VideoStatus.Processing] = "PROCESSING",
        [VideoStatus.Completed] = "COMPLETED",
        [VideoStatus.Failed] = "FAILED",
        [VideoStatus.Expired] = "EXPIRED",
    }.ToFrozenDictionary();

    private static readonly FrozenDictionary<string, VideoStatus> Values =
        Names.ToFrozenDictionary(pair => pair.Value, pair => pair.Key, StringComparer.Ordinal);

    public static string CheckConstraintSql { get; } =
        $"status IN ({string.Join(",", Names.Values.Select(name => $"'{name}'"))})";

    public static string ToDatabase(VideoStatus status) =>
        Names.TryGetValue(status, out var name)
            ? name
            : throw new ArgumentOutOfRangeException(nameof(status), status, "Unknown video status.");

    public static VideoStatus FromDatabase(string value) =>
        Values.TryGetValue(value, out var status)
            ? status
            : throw new InvalidOperationException($"Unknown video status '{value}' in the database.");
}
