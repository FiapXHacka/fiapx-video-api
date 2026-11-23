using System.Text;
using FiapX.VideoApi.Application.Messaging;
using FiapX.VideoApi.Infrastructure.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace FiapX.VideoApi.UnitTests.Infrastructure;

/// <summary>
/// Paths that must ack without touching the database. The inbox/transaction path is covered by the
/// integration tests (real Postgres and RabbitMQ).
/// </summary>
public sealed class MessageDispatcherTests
{
    private readonly IServiceScopeFactory _scopeFactory = Substitute.For<IServiceScopeFactory>();

    private MessageDispatcher CreateDispatcher(params string[] handledTypes)
    {
        var services = new ServiceCollection();
        foreach (var type in handledTypes)
        {
            services.AddIntegrationEventConsumer<VideoProcessedData, NoopHandler>(type);
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
    public async Task Malformed_messages_are_acked_without_side_effects(string body)
    {
        var dispatcher = CreateDispatcher(EventTypes.VideoProcessed);

        await dispatcher.DispatchAsync(Encoding.UTF8.GetBytes(body), TestContext.Current.CancellationToken);

        _scopeFactory.DidNotReceive().CreateScope();
    }

    [Fact]
    public async Task Unknown_types_are_acked_without_side_effects()
    {
        var dispatcher = CreateDispatcher(EventTypes.VideoProcessed);
        const string body = """{"specversion":"1.0","id":"0193a1b2-0000-7000-8000-00000000abcd","source":"x","type":"video.unknown","time":"2026-11-23T18:00:00Z","data":{}}""";

        await dispatcher.DispatchAsync(Encoding.UTF8.GetBytes(body), TestContext.Current.CancellationToken);

        _scopeFactory.DidNotReceive().CreateScope();
    }

    [Fact]
    public void HasHandlers_reflects_registrations()
    {
        CreateDispatcher().HasHandlers.ShouldBeFalse();
        CreateDispatcher(EventTypes.VideoProcessed).HasHandlers.ShouldBeTrue();
    }

    [Fact]
    public void Registering_a_handler_requires_an_event_type()
    {
        Should.Throw<ArgumentException>(() =>
            new ServiceCollection().AddIntegrationEventConsumer<VideoProcessedData, NoopHandler>(" "));
    }

    internal sealed class NoopHandler : IIntegrationEventConsumer<VideoProcessedData>
    {
        public Task HandleAsync(IncomingEvent<VideoProcessedData> message, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
