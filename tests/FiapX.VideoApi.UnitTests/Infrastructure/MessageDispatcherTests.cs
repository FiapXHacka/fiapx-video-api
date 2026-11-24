using System.Text;
using FiapX.VideoApi.Application.Messaging;
using FiapX.VideoApi.Infrastructure.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace FiapX.VideoApi.UnitTests.Infrastructure;

/// <summary>
/// Paths decided before touching the database. The inbox/transaction path is covered by the integration tests
/// (real Postgres and RabbitMQ).
/// </summary>
public sealed class MessageDispatcherTests
{
    private readonly IServiceScopeFactory _scopeFactory = Substitute.For<IServiceScopeFactory>();

    private MessageDispatcher CreateDispatcher(params string[] handledTypes)
    {
        var services = new ServiceCollection();
        foreach (var type in handledTypes)
        {
            services.AddIntegrationEventConsumer<VideoProcessedData, NoopConsumer>(type);
        }

        var registrations = services.BuildServiceProvider().GetServices<IntegrationEventRegistration>();
        return new MessageDispatcher(_scopeFactory, registrations, TimeProvider.System, NullLogger<MessageDispatcher>.Instance);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("""{"specversion":"1.0"}""")]
    [InlineData("""{"specversion":"1.0","id":"00000000-0000-0000-0000-000000000000","source":"x","type":"video.processed","time":"2026-11-23T18:00:00Z","data":{}}""")]
    [InlineData("""{"specversion":"1.0","id":"0193a1b2-0000-7000-8000-00000000abcd","source":"x","type":" ","time":"2026-11-23T18:00:00Z","data":{}}""")]
    [InlineData("null")]
    public async Task Envelopes_without_id_or_type_are_ignored(string body)
    {
        var outcome = await CreateDispatcher(EventTypes.VideoProcessed)
            .DispatchAsync(Encoding.UTF8.GetBytes(body), TestContext.Current.CancellationToken);

        outcome.ShouldBe(DispatchOutcome.Ignored);
        _scopeFactory.DidNotReceive().CreateScope();
    }

    [Fact]
    public async Task Unknown_types_are_ignored()
    {
        const string body = """{"specversion":"1.0","id":"0193a1b2-0000-7000-8000-00000000abcd","source":"x","type":"video.unknown","time":"2026-11-23T18:00:00Z","data":{}}""";

        var outcome = await CreateDispatcher(EventTypes.VideoProcessed)
            .DispatchAsync(Encoding.UTF8.GetBytes(body), TestContext.Current.CancellationToken);

        outcome.ShouldBe(DispatchOutcome.Ignored);
        _scopeFactory.DidNotReceive().CreateScope();
    }

    [Theory]
    [InlineData("\"not an object\"")]
    [InlineData("""{"videoId":"not-a-guid"}""")]
    [InlineData("""{"frameCount":"three"}""")]
    [InlineData("null")]
    public async Task Invalid_data_for_a_known_type_is_rejected_without_touching_the_database(string data)
    {
        var body = $$"""{"specversion":"1.0","id":"0193a1b2-0000-7000-8000-00000000abcd","source":"x","type":"video.processed","time":"2026-11-23T18:00:00Z","data":{{data}}}""";

        var outcome = await CreateDispatcher(EventTypes.VideoProcessed)
            .DispatchAsync(Encoding.UTF8.GetBytes(body), TestContext.Current.CancellationToken);

        outcome.ShouldBe(DispatchOutcome.Rejected);
        _scopeFactory.DidNotReceive().CreateScope();
    }

    [Fact]
    public void HasHandlers_reflects_registrations()
    {
        CreateDispatcher().HasHandlers.ShouldBeFalse();
        CreateDispatcher(EventTypes.VideoProcessed).HasHandlers.ShouldBeTrue();
    }

    [Fact]
    public void Registering_a_consumer_requires_an_event_type()
    {
        Should.Throw<ArgumentException>(() =>
            new ServiceCollection().AddIntegrationEventConsumer<VideoProcessedData, NoopConsumer>(" "));
    }

    [Fact]
    public void Retry_delay_is_exponential_with_at_most_20_percent_jitter()
    {
        var options = Options.Create(new RabbitMqOptions { HandlerRetryDelay = TimeSpan.FromMilliseconds(200) });
        var consumer = new RabbitMqConsumer(
            new UnusedConnection(), CreateDispatcher(), options, TimeProvider.System,
            NullLogger<RabbitMqConsumer>.Instance);

        for (var attempt = 1; attempt <= 3; attempt++)
        {
            var expected = 200 * Math.Pow(2, attempt - 1);
            var delay = consumer.RetryDelay(attempt).TotalMilliseconds;
            delay.ShouldBeInRange(expected, expected * 1.2);
        }
    }

    [Fact]
    public void Defaults_follow_the_canonical_configuration()
    {
        var options = new RabbitMqOptions();

        options.PrefetchCount.ShouldBe((ushort)16);
        options.ConsumerConcurrency.ShouldBe((ushort)4);
        options.MaxHandlerAttempts.ShouldBe(3);
        options.HandlerRetryDelay.ShouldBe(TimeSpan.FromMilliseconds(200));
        options.Source.ShouldBe("fiapx/video-api");
        new OutboxOptions().Enabled.ShouldBeTrue();
    }

    private sealed class UnusedConnection : IRabbitMqConnection
    {
        public bool IsOpen => false;

        public Task<RabbitMQ.Client.IConnection> GetAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException("not used by RetryDelay");
    }

    internal sealed class NoopConsumer : IIntegrationEventConsumer<VideoProcessedData>
    {
        public Task HandleAsync(IncomingEvent<VideoProcessedData> message, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
