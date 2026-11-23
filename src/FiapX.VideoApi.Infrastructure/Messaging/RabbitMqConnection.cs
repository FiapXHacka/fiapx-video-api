using Microsoft.Extensions.Options;
using RabbitMQ.Client;

namespace FiapX.VideoApi.Infrastructure.Messaging;

internal interface IRabbitMqConnection
{
    bool IsOpen { get; }

    Task<IConnection> GetAsync(CancellationToken cancellationToken);
}

/// <summary>One long-lived connection per process, created lazily and recovered by the client library.</summary>
internal sealed class RabbitMqConnection(IOptions<RabbitMqOptions> options) : IRabbitMqConnection, IAsyncDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IConnection? _connection;

    public bool IsOpen => _connection is { IsOpen: true };

    public async Task<IConnection> GetAsync(CancellationToken cancellationToken)
    {
        if (_connection is { IsOpen: true } open)
        {
            return open;
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_connection is { IsOpen: true } current)
            {
                return current;
            }

            if (_connection is not null)
            {
                await _connection.DisposeAsync();
            }

            var factory = new ConnectionFactory
            {
                Uri = new Uri(options.Value.Uri),
                ClientProvidedName = options.Value.ClientName,
                AutomaticRecoveryEnabled = true,
                TopologyRecoveryEnabled = true,
            };
            _connection = await factory.CreateConnectionAsync(cancellationToken);
            return _connection;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_connection is not null)
        {
            await _connection.DisposeAsync();
        }

        _gate.Dispose();
    }
}
