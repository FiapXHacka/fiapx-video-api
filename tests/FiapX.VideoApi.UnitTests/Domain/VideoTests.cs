using FiapX.VideoApi.Domain.Videos;

namespace FiapX.VideoApi.UnitTests.Domain;

public sealed class VideoTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 23, 18, 0, 0, TimeSpan.Zero);
    private static readonly Guid VideoId = Guid.CreateVersion7(Now);
    private static readonly Guid UserId = Guid.CreateVersion7(Now);

    [Fact]
    public void CreatePendingUpload_ValidInput_StartsAsPendingUpload()
    {
        var video = Video.CreatePendingUpload(VideoId, UserId, "pitch.mp4", "video/mp4", 1024, "uploads/u/v/original.mp4", Now);

        video.Id.ShouldBe(VideoId);
        video.UserId.ShouldBe(UserId);
        video.OriginalFileName.ShouldBe("pitch.mp4");
        video.ContentType.ShouldBe("video/mp4");
        video.SizeBytes.ShouldBe(1024);
        video.StorageKey.ShouldBe("uploads/u/v/original.mp4");
        video.Status.ShouldBe(VideoStatus.PendingUpload);
        video.CreatedAt.ShouldBe(Now);
        video.UpdatedAt.ShouldBe(Now);
        video.Version.ShouldBe(0);
        video.FrameCount.ShouldBeNull();
        video.ResultKey.ShouldBeNull();
        video.ErrorCode.ShouldBeNull();
    }

    public static TheoryData<Guid, Guid, string, string, long, string> InvalidInputs()
    {
        var longName = new string('a', Video.MaxFileNameLength + 1);
        return new()
        {
            { Guid.Empty, UserId, "a.mp4", "video/mp4", 1, "k" },
            { VideoId, Guid.Empty, "a.mp4", "video/mp4", 1, "k" },
            { VideoId, UserId, " ", "video/mp4", 1, "k" },
            { VideoId, UserId, "a.mp4", "", 1, "k" },
            { VideoId, UserId, "a.mp4", "video/mp4", 0, "k" },
            { VideoId, UserId, "a.mp4", "video/mp4", -5, "k" },
            { VideoId, UserId, "a.mp4", "video/mp4", 1, "" },
            { VideoId, UserId, longName, "video/mp4", 1, "k" },
        };
    }

    [Theory]
    [MemberData(nameof(InvalidInputs))]
    public void CreatePendingUpload_InvalidInput_Throws(Guid id, Guid userId, string fileName, string contentType, long size, string key)
    {
        Should.Throw<ArgumentException>(() => Video.CreatePendingUpload(id, userId, fileName, contentType, size, key, Now));
    }
}
