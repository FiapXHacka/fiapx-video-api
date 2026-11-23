using System.Collections.Concurrent;
using FiapX.VideoApi.Application.Messaging;
using FiapX.VideoApi.Infrastructure.Messaging;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using RabbitMQ.Client;
using Testcontainers.PostgreSql;
using Testcontainers.RabbitMq;

namespace FiapX.VideoApi.IntegrationTests.Infrastructure;

/// <summary>
/// Real Postgres and RabbitMQ (Testcontainers) plus the API host. The host registers test handlers so the
/// consumer starts; the handler for video.processed throws when the video id is <see cref="PoisonVideoId"/>.
/// </summary>
public sealed class PlatformFixture : IAsyncLifetime
{
    public static readonly Guid PoisonVideoId = Guid.Parse("00000000-0000-7000-8000-0000000000ff");

    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:17-alpine")
        .WithDatabase("video_db")
        .Build();

    private readonly RabbitMqContainer _rabbit = new RabbitMqBuilder("rabbitmq:4-management-alpine").Build();

    public WebApplicationFactory<Program> Factory { get; private set; } = null!;

    public ConcurrentQueue<IncomingEvent<VideoProcessedData>> HandledProcessed { get; } = new();

    private int _poisonAttempts;

    public int PoisonAttempts => Volatile.Read(ref _poisonAttempts);

    public string PostgresConnectionString => _postgres.GetConnectionString();

    public async ValueTask InitializeAsync()
    {
        await Task.WhenAll(_postgres.StartAsync(), _rabbit.StartAsync());

        Factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("ConnectionStrings:Postgres", _postgres.GetConnectionString());
            builder.UseSetting("RabbitMq:Uri", _rabbit.GetConnectionString());
            builder.UseSetting("Auth:Issuer", "https://issuer.test");
            builder.UseSetting("Auth:MetadataAddress", "http://identity.test/.well-known/openid-configuration");
            builder.UseSetting("Auth:RequireHttpsMetadata", "false");
            builder.UseSetting("RabbitMq:HandlerRetryDelay", "00:00:00.050");
            builder.UseSetting("Database:MigrateOnStartup", "true");
            builder.UseSetting("Outbox:PollInterval", "00:00:00.200");
            builder.ConfigureTestServices(services =>
            {
                services.AddSingleton(this);
                services.AddIntegrationEventConsumer<VideoProcessedData, RecordingProcessedHandler>(EventTypes.VideoProcessed);
            });
        });

        // Starts the host: migrations run, the relay and the consumer start.
        _ = Factory.Server;
    }

    public async Task<IConnection> ConnectToBrokerAsync() =>
        await new ConnectionFactory { Uri = new Uri(_rabbit.GetConnectionString()) }.CreateConnectionAsync();

    public async ValueTask DisposeAsync()
    {
        if (Factory is not null)
        {
            await Factory.DisposeAsync();
        }

        await _rabbit.DisposeAsync();
        await _postgres.DisposeAsync();
    }

    internal sealed class RecordingProcessedHandler(PlatformFixture fixture) : IIntegrationEventConsumer<VideoProcessedData>
    {
        public Task HandleAsync(IncomingEvent<VideoProcessedData> message, CancellationToken cancellationToken)
        {
            if (message.Data.VideoId == PoisonVideoId)
            {
                Interlocked.Increment(ref fixture._poisonAttempts);
                throw new InvalidOperationException("poison message for the DLQ test");
            }

            fixture.HandledProcessed.Enqueue(message);
            return Task.CompletedTask;
        }
    }
}

[CollectionDefinition(Name)]
public sealed class PlatformTests : ICollectionFixture<PlatformFixture>
{
    public const string Name = "platform";
}

internal static class Eventually
{
    public static async Task<T> ReturnsAsync<T>(Func<Task<T>> probe, Func<T, bool> done, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(30));
        while (true)
        {
            var value = await probe();
            if (done(value))
            {
                return value;
            }

            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException($"Condition not met in time; last value: {value}");
            }

            await Task.Delay(200);
        }
    }
}
