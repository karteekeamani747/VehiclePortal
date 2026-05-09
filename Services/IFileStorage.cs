namespace VehiclePortal.Services
{
    /// <summary>
    /// Abstraction over file storage.
    /// Local disk in development, AWS S3 in production.
    /// Swap implementations in Program.cs — no other code changes needed.
    /// </summary>
    public interface IFileStorage
    {
        /// <summary>
        /// Save a file from an uploaded stream.
        /// Returns the stored file path (relative key used to retrieve later).
        /// </summary>
        Task<string> SaveAsync(Stream fileStream, string storedName, string contentType, CancellationToken ct = default);

        /// <summary>
        /// Retrieve a file as a stream for download.
        /// </summary>
        Task<Stream?> GetAsync(string filePath, CancellationToken ct = default);

        /// <summary>
        /// Delete a file from storage.
        /// </summary>
        Task DeleteAsync(string filePath, CancellationToken ct = default);

        /// <summary>
        /// Returns a URL or path the client can use to access the file.
        /// Local: relative path. S3: pre-signed URL.
        /// </summary>
        Task<string> GetUrlAsync(string filePath, CancellationToken ct = default);
    }
}