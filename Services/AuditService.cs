using VehiclePortal.Data;
using VehiclePortal.Models;

namespace VehiclePortal.Services
{
    public interface IAuditService
    {
        Task LogAsync(
            string userId,
            string userEmail,
            string userRole,
            string action,
            string entityType,
            string entityId,
            string? details = null,
            string? ipAddress = null);
    }

    public class AuditService : IAuditService
    {
        private readonly AppDbContext _db;
        private readonly ILogger<AuditService> _logger;

        public AuditService(AppDbContext db, ILogger<AuditService> logger)
        {
            _db = db;
            _logger = logger;
        }

        public async Task LogAsync(
            string userId,
            string userEmail,
            string userRole,
            string action,
            string entityType,
            string entityId,
            string? details = null,
            string? ipAddress = null)
        {
            _db.AuditLogs.Add(new AuditLog
            {
                UserId = userId,
                UserEmail = userEmail,
                UserRole = userRole,
                Action = action,
                EntityType = entityType,
                EntityId = entityId,
                Details = details,
                IpAddress = ipAddress
            });

            await _db.SaveChangesAsync();

            _logger.LogInformation(
                "[Audit] {Action} on {EntityType} {EntityId} by {Email}",
                action, entityType, entityId, userEmail);
        }
    }
}