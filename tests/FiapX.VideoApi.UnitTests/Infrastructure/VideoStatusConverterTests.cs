using FiapX.VideoApi.Domain.Videos;
using FiapX.VideoApi.Infrastructure.Persistence;

namespace FiapX.VideoApi.UnitTests.Infrastructure;

public sealed class VideoStatusConverterTests
{
    [Theory]
    [InlineData(VideoStatus.PendingUpload, "PENDING_UPLOAD")]
    [InlineData(VideoStatus.Uploaded, "UPLOADED")]
    [InlineData(VideoStatus.Processing, "PROCESSING")]
    [InlineData(VideoStatus.Completed, "COMPLETED")]
    [InlineData(VideoStatus.Failed, "FAILED")]
    [InlineData(VideoStatus.Expired, "EXPIRED")]
    public void RoundTrip_UsesSchemaNames(VideoStatus status, string name)
    {
        VideoStatusConverter.ToDatabase(status).ShouldBe(name);
        VideoStatusConverter.FromDatabase(name).ShouldBe(status);

        var converter = new VideoStatusConverter();
        converter.ConvertToProvider(status).ShouldBe(name);
        converter.ConvertFromProvider(name).ShouldBe(status);
    }

    [Fact]
    public void Every_enum_value_has_a_name()
    {
        foreach (var status in Enum.GetValues<VideoStatus>())
        {
            Should.NotThrow(() => VideoStatusConverter.ToDatabase(status));
        }
    }

    [Fact]
    public void Unknown_values_throw()
    {
        Should.Throw<ArgumentOutOfRangeException>(() => VideoStatusConverter.ToDatabase((VideoStatus)99));
        Should.Throw<InvalidOperationException>(() => VideoStatusConverter.FromDatabase("DELETED"));
    }

    [Fact]
    public void CheckConstraint_matches_schema()
    {
        VideoStatusConverter.CheckConstraintSql.ShouldBe(
            "status IN ('PENDING_UPLOAD','UPLOADED','PROCESSING','COMPLETED','FAILED','EXPIRED')");
    }
}
