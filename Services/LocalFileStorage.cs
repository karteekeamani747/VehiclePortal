namespace VehiclePortal.Services
{
    /// <summary>
    /// Stores files on local disk inside the /uploads folder.
    /// Used in development and Docker.
    /// Replace with S3FileStorage in production by changing the
    /// registration in Program.cs — nothing else changes.
    /// </summary>
    public class LocalFileStorage : IFileStorage
    {
        private readonly string _basePath;
        private readonly ILogger<LocalFileStorage> _logger;

        public LocalFileStorage(IConfiguration config, ILogger<LocalFileStorage> logger)
        {
            _logger = logger;

            // Use config value if set, otherwise default to /uploads next to the app
            _basePath = config["Storage:LocalPath"]
                ?? Path.Combine(Directory.GetCurrentDirectory(), "uploads");

            // Ensure the directory exists on startup
            Directory.CreateDirectory(_basePath);
            _logger.LogInformation("[Storage] Local file storage initialised at: {Path}", _basePath);
        }

        public async Task<string> SaveAsync(
            Stream fileStream,
            string storedName,
            string contentType,
            CancellationToken ct = default)
        {
            var filePath = Path.Combine(_basePath, storedName);

            await using var outputStream = File.Create(filePath);
            await fileStream.CopyToAsync(outputStream, ct);

            _logger.LogInformation("[Storage] Saved file: {Name} ({Size} bytes)",
                storedName, outputStream.Length);

            // Return the relative path used to retrieve this file later
            return storedName;
        }

        public Task<Stream?> GetAsync(string filePath, CancellationToken ct = default)
        {
            var fullPath = Path.Combine(_basePath, filePath);

            if (!File.Exists(fullPath))
            {
                _logger.LogWarning("[Storage] File not found: {Path}", fullPath);
                return Task.FromResult<Stream?>(null);
            }

            Stream stream = File.OpenRead(fullPath);
            return Task.FromResult<Stream?>(stream);
        }

        public Task DeleteAsync(string filePath, CancellationToken ct = default)
        {
            var fullPath = Path.Combine(_basePath, filePath);

            if (File.Exists(fullPath))
            {
                File.Delete(fullPath);
                _logger.LogInformation("[Storage] Deleted file: {Path}", fullPath);
            }

            return Task.CompletedTask;
        }

        public Task<string> GetUrlAsync(string filePath, CancellationToken ct = default)
        {
            // For local storage return a relative URL the browser can fetch
            // In production this returns a pre-signed S3 URL instead
            return Task.FromResult($"/api/documents/file/{filePath}");
        }
    }
}