using RabbitMQ.Client;
using System.Text;

namespace VehiclePortal.Services
{
    /// <summary>
    /// Publishes events to RabbitMQ using a topic exchange.
    /// Each event type becomes a routing key e.g. "doc.uploaded", "listing.created".
    /// In Phase 4 (AWS) this gets swapped for an SQS/SNS publisher via IMessageBus.
    /// </summary>
    public class RabbitMqPublisher : IAsyncDisposable
    {
        private readonly IConfiguration _config;
        private readonly ILogger<RabbitMqPublisher> _logger;
        private IConnection? _connection;
        private IChannel? _channel;
        private bool _isConnected = false;

        public const string ExchangeName = "vehicleportal.events";

        public RabbitMqPublisher(
            IConfiguration config,
            ILogger<RabbitMqPublisher> logger)
        {
            _config = config;
            _logger = logger;
        }

        /// <summary>
        /// Connects to RabbitMQ, declares the exchange, and binds all queues.
        /// Called once on first publish — subsequent calls are no-ops.
        /// </summary>
        private async Task EnsureConnectedAsync()
        {
            if (_isConnected && _channel != null && _channel.IsOpen) return;

            var host = _config["RabbitMQ:Host"] ?? "localhost";
            var port = int.Parse(_config["RabbitMQ:Port"] ?? "5672");
            var username = _config["RabbitMQ:Username"] ?? "guest";
            var password = _config["RabbitMQ:Password"] ?? "guest";

            _logger.LogInformation("[RabbitMQ] Connecting to {Host}:{Port}", host, port);

            var factory = new ConnectionFactory
            {
                HostName = host,
                Port = port,
                UserName = username,
                Password = password
            };

            _connection = await factory.CreateConnectionAsync();
            _channel = await _connection.CreateChannelAsync();

            // ── Main events exchange ──────────────────────────────────────────
            // Topic exchange — routing keys like "listing.*", "doc.*"
            await _channel.ExchangeDeclareAsync(
                exchange: ExchangeName,
                type: ExchangeType.Topic,
                durable: true,
                autoDelete: false);

            // ── DLQ exchange and queue ────────────────────────────────────────
            await _channel.ExchangeDeclareAsync(
                exchange: "vehicleportal.dlq",
                type: ExchangeType.Fanout,
                durable: true,
                autoDelete: false);

            await _channel.QueueDeclareAsync(
                queue: "vehicleportal.dead-letters",
                durable: true,
                exclusive: false,
                autoDelete: false);

            await _channel.QueueBindAsync(
                queue: "vehicleportal.dead-letters",
                exchange: "vehicleportal.dlq",
                routingKey: "#");

            // ── Consumer queues — declared once on startup ────────────────────
            // Each queue is bound to a specific routing key on the events exchange.
            // Consumers (OCR worker, notification service etc) subscribe to these.
            var queues = new[]
            {
                ("vehicleportal.listing.created",   "listing.created"),
                ("vehicleportal.listing.published", "listing.published"),
                ("vehicleportal.listing.deleted",   "listing.deleted"),
                ("vehicleportal.doc.uploaded",      "doc.uploaded"),
                ("vehicleportal.offer.placed",      "offer.placed"),
                ("vehicleportal.vin.transferred",   "vin.transferred"),
            };

            foreach (var (queueName, routingKey) in queues)
            {
                await _channel.QueueDeclareAsync(
                    queue: queueName,
                    durable: true,
                    exclusive: false,
                    autoDelete: false);

                await _channel.QueueBindAsync(
                    queue: queueName,
                    exchange: ExchangeName,
                    routingKey: routingKey);
            }

            _isConnected = true;
            _logger.LogInformation(
                "[RabbitMQ] Connected. Exchange '{Exchange}' ready with {Count} queues.",
                ExchangeName, queues.Length);
        }

        /// <summary>
        /// Publishes an event to the topic exchange.
        /// The routing key is the event type e.g. "doc.uploaded".
        /// </summary>
        public async Task PublishAsync(string eventType, string payload)
        {
            await EnsureConnectedAsync();

            if (_channel == null)
                throw new InvalidOperationException("RabbitMQ channel is not open.");

            var body = Encoding.UTF8.GetBytes(payload);
            var props = new BasicProperties
            {
                Persistent = true,
                ContentType = "application/json",
                MessageId = Guid.NewGuid().ToString(),
                Timestamp = new AmqpTimestamp(DateTimeOffset.UtcNow.ToUnixTimeSeconds()),
                Headers = new Dictionary<string, object?> { { "event-type", eventType } }
            };

            await _channel.BasicPublishAsync(
                exchange: ExchangeName,
                routingKey: eventType,
                mandatory: false,
                basicProperties: props,
                body: body);

            _logger.LogInformation(
                "[RabbitMQ] Published '{EventType}' ({Bytes} bytes)",
                eventType, body.Length);
        }

        public async ValueTask DisposeAsync()
        {
            if (_channel != null)
            {
                await _channel.CloseAsync();
                _channel.Dispose();
            }
            if (_connection != null)
            {
                await _connection.CloseAsync();
                _connection.Dispose();
            }
        }
    }
}