using Microsoft.EntityFrameworkCore;
using VehiclePortal.Data;
using VehiclePortal.Models;
using VehiclePortal.Services;

namespace VehiclePortal.Workers
{
    /// <summary>
    /// Background worker that polls the OutboxMessages table every 10 seconds
    /// and publishes pending events to RabbitMQ.
    ///
    /// Retry logic:
    ///   Attempt 1 fails → retry in 10s
    ///   Attempt 2 fails → retry in 30s
    ///   Attempt 3 fails → move to DeadLetterMessages
    /// </summary>
    public class OutboxWorker : BackgroundService
    {
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly RabbitMqPublisher _publisher;
        private readonly ILogger<OutboxWorker> _logger;

        private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(10);

        private static readonly TimeSpan[] RetryDelays =
        [
            TimeSpan.FromSeconds(10),
            TimeSpan.FromSeconds(30),
            TimeSpan.FromSeconds(60)
        ];

        public OutboxWorker(
            IServiceScopeFactory scopeFactory,
            RabbitMqPublisher publisher,
            ILogger<OutboxWorker> logger)
        {
            _scopeFactory = scopeFactory;
            _publisher = publisher;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("[OutboxWorker] Started. Polling every {Interval}s",
                PollInterval.TotalSeconds);

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await ProcessPendingMessagesAsync(stoppingToken);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "[OutboxWorker] Unexpected error in poll loop");
                }

                await Task.Delay(PollInterval, stoppingToken);
            }
        }

        private async Task ProcessPendingMessagesAsync(CancellationToken ct)
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            var messages = await db.OutboxMessages
                .Where(m => m.Status == OutboxStatus.Pending
                         && m.NextRetryAt <= DateTime.UtcNow)
                .OrderBy(m => m.CreatedAt)
                .Take(20)
                .ToListAsync(ct);

            if (!messages.Any()) return;

            _logger.LogInformation("[OutboxWorker] Processing {Count} pending messages",
                messages.Count);

            foreach (var message in messages)
            {
                await ProcessMessageAsync(db, message, ct);
            }

            await db.SaveChangesAsync(ct);
        }

        private async Task ProcessMessageAsync(
            AppDbContext db,
            OutboxMessage message,
            CancellationToken ct)
        {
            try
            {
                // Publish to RabbitMQ
                await _publisher.PublishAsync(message.EventType, message.Payload);

                message.Status = OutboxStatus.Sent;
                message.ProcessedAt = DateTime.UtcNow;
                message.LastError = null;

                _logger.LogInformation(
                    "[OutboxWorker] Published '{EventType}' (OutboxId: {Id})",
                    message.EventType, message.Id);
            }
            catch (Exception ex)
            {
                message.RetryCount++;
                message.LastError = ex.Message;

                _logger.LogWarning(
                    "[OutboxWorker] Failed to publish '{EventType}' " +
                    "(attempt {Retry}/{Max}): {Error}",
                    message.EventType, message.RetryCount,
                    OutboxMessage.MaxRetries, ex.Message);

                if (message.RetryCount >= OutboxMessage.MaxRetries)
                {
                    message.Status = OutboxStatus.Failed;

                    db.DeadLetterMessages.Add(new DeadLetterMessage
                    {
                        OutboxId = message.Id,
                        EventType = message.EventType,
                        Payload = message.Payload,
                        CreatedBy = message.CreatedBy,
                        FailureReason = ex.Message,
                        RetryCount = message.RetryCount
                    });

                    _logger.LogError(
                        "[OutboxWorker] Message {Id} moved to DLQ after {Retries} failures. " +
                        "EventType: {EventType}",
                        message.Id, message.RetryCount, message.EventType);
                }
                else
                {
                    var delay = RetryDelays[
                        Math.Min(message.RetryCount - 1, RetryDelays.Length - 1)];
                    message.NextRetryAt = DateTime.UtcNow.Add(delay);

                    _logger.LogInformation(
                        "[OutboxWorker] Will retry message {Id} at {NextRetry}",
                        message.Id, message.NextRetryAt);
                }
            }
        }
    }
}