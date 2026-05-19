using Microsoft.EntityFrameworkCore;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using System.Text;
using System.Text.Json;
using VehiclePortal.Data;
using VehiclePortal.Models;
using VehiclePortal.Services;

namespace VehiclePortal.Workers
{
    /// <summary>
    /// Listens to the vehicleportal.doc.uploaded RabbitMQ queue.
    /// When a document is uploaded it runs OCR and updates the document
    /// with the extracted VIN, make, model, and year.
    ///
    /// In Phase 4 (AWS) this worker is replaced by a Lambda function
    /// triggered by SQS, using AWS Textract instead of Tesseract.
    /// </summary>
    public class OcrWorker : BackgroundService
    {
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly IConfiguration _config;
        private readonly ILogger<OcrWorker> _logger;
        private IConnection? _connection;
        private IChannel? _channel;

        private const string QueueName = "vehicleportal.doc.uploaded";

        public OcrWorker(
            IServiceScopeFactory scopeFactory,
            IConfiguration config,
            ILogger<OcrWorker> logger)
        {
            _scopeFactory = scopeFactory;
            _config = config;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("[OcrWorker] Starting…");

            // Wait for RabbitMQ to be ready
            await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken);

            try
            {
                await ConnectAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[OcrWorker] Failed to connect to RabbitMQ. OCR will not run.");
                return;
            }

            // Keep running until cancelled
            await Task.Delay(Timeout.Infinite, stoppingToken);
        }

        private async Task ConnectAsync(CancellationToken ct)
        {
            var host = _config["RabbitMQ:Host"] ?? "localhost";
            var port = int.Parse(_config["RabbitMQ:Port"] ?? "5672");
            var username = _config["RabbitMQ:Username"] ?? "guest";
            var password = _config["RabbitMQ:Password"] ?? "guest";

            var factory = new ConnectionFactory
            {
                HostName = host,
                Port = port,
                UserName = username,
                Password = password
            };

            _connection = await factory.CreateConnectionAsync(ct);
            _channel = await _connection.CreateChannelAsync(cancellationToken: ct);

            // Declare the queue (idempotent — safe to call if already exists)
            await _channel.QueueDeclareAsync(
                queue: QueueName,
                durable: true,
                exclusive: false,
                autoDelete: false,
                cancellationToken: ct);

            // Process one message at a time
            await _channel.BasicQosAsync(
                prefetchSize: 0,
                prefetchCount: 1,
                global: false,
                cancellationToken: ct);

            var consumer = new AsyncEventingBasicConsumer(_channel);
            consumer.ReceivedAsync += OnMessageReceived;

            await _channel.BasicConsumeAsync(
                queue: QueueName,
                autoAck: false,
                consumer: consumer,
                cancellationToken: ct);

            _logger.LogInformation(
                "[OcrWorker] Connected to RabbitMQ. Listening on queue: {Queue}",
                QueueName);
        }

        private async Task OnMessageReceived(
            object sender, BasicDeliverEventArgs args)
        {
            var body = Encoding.UTF8.GetString(args.Body.ToArray());
            var channel = ((AsyncEventingBasicConsumer)sender).Channel;

            _logger.LogInformation("[OcrWorker] Received message: {Body}", body);

            try
            {
                var payload = JsonSerializer.Deserialize<DocUploadedPayload>(body,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

                if (payload?.DocumentId > 0)
                {
                    await ProcessDocumentAsync(payload.DocumentId);
                }

                // Acknowledge the message
                await channel.BasicAckAsync(args.DeliveryTag, false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "[OcrWorker] Failed to process message. Rejecting.");

                // Reject and don't requeue — let it go to DLQ
                await channel.BasicNackAsync(args.DeliveryTag, false, false);
            }
        }

        private async Task ProcessDocumentAsync(int documentId)
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var storage = scope.ServiceProvider.GetRequiredService<IFileStorage>();
            var ocrService = scope.ServiceProvider.GetRequiredService<IOcrService>();

            var document = await db.Documents.FindAsync(documentId);
            if (document == null)
            {
                _logger.LogWarning(
                    "[OcrWorker] Document {Id} not found — skipping.", documentId);
                return;
            }

            // Skip if already processed
            if (document.OcrStatus == OcrStatus.Completed ||
                document.OcrStatus == OcrStatus.Failed)
            {
                _logger.LogInformation(
                    "[OcrWorker] Document {Id} already processed ({Status}) — skipping.",
                    documentId, document.OcrStatus);
                return;
            }

            // Check if content type is supported for OCR
            var supportedTypes = new[]
            {
                "image/jpeg", "image/jpg", "image/png",
                "image/webp", "application/pdf"
            };

            if (!supportedTypes.Contains(document.ContentType.ToLower()))
            {
                document.OcrStatus = OcrStatus.Skipped;
                document.ProcessedAt = DateTime.UtcNow;
                await db.SaveChangesAsync();

                _logger.LogInformation(
                    "[OcrWorker] Document {Id} type {Type} not supported for OCR — skipped.",
                    documentId, document.ContentType);
                return;
            }

            // Mark as processing
            document.OcrStatus = OcrStatus.Processing;
            await db.SaveChangesAsync();

            _logger.LogInformation(
                "[OcrWorker] Processing document {Id} ({FileName})",
                documentId, document.FileName);

            try
            {
                // Get the file from storage
                var stream = await storage.GetAsync(document.FilePath);
                if (stream == null)
                {
                    throw new FileNotFoundException(
                        $"File not found in storage: {document.FilePath}");
                }

                // Run OCR
                var result = await ocrService.ProcessAsync(stream, document.ContentType);

                if (!result.Success)
                {
                    document.OcrStatus = OcrStatus.Failed;
                    document.OcrError = result.Error;
                    document.ProcessedAt = DateTime.UtcNow;

                    _logger.LogWarning(
                        "[OcrWorker] OCR failed for document {Id}: {Error}",
                        documentId, result.Error);
                }
                else
                {
                    document.OcrStatus = OcrStatus.Completed;
                    document.OcrRawText = result.RawText;
                    document.ExtractedVin = result.ExtractedVin;
                    document.ProcessedAt = DateTime.UtcNow;

                    // Store all extracted data as JSON
                    document.ExtractedData = JsonSerializer.Serialize(new
                    {
                        Vin = result.ExtractedVin,
                        Make = result.ExtractedMake,
                        Model = result.ExtractedModel,
                        Year = result.ExtractedYear
                    });

                    _logger.LogInformation(
                        "[OcrWorker] OCR completed for document {Id}. " +
                        "VIN: {Vin} Make: {Make} Year: {Year}",
                        documentId,
                        result.ExtractedVin ?? "not found",
                        result.ExtractedMake ?? "not found",
                        result.ExtractedYear?.ToString() ?? "not found");
                }

                await db.SaveChangesAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "[OcrWorker] Exception processing document {Id}", documentId);

                document.OcrStatus = OcrStatus.Failed;
                document.OcrError = ex.Message;
                document.ProcessedAt = DateTime.UtcNow;
                await db.SaveChangesAsync();
            }
        }

        public override async void Dispose()
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
            base.Dispose();
        }
    }

    internal class DocUploadedPayload
    {
        public int DocumentId { get; set; }
        public string? FileName { get; set; }
        public string? UploadedBy { get; set; }
        public int? ListingId { get; set; }
        public string? FileHash { get; set; }
    }
}