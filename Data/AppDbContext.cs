using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using VehiclePortal.Models;

namespace VehiclePortal.Data
{
    public class AppDbContext : IdentityDbContext<ApplicationUser>
    {
        public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

        public DbSet<AuditLog> AuditLogs { get; set; }

        protected override void OnModelCreating(ModelBuilder builder)
        {
            base.OnModelCreating(builder);

            // ── ApplicationUser ───────────────────────────────────────────────
            builder.Entity<ApplicationUser>(entity =>
            {
                entity.Property(u => u.FirstName)
                      .HasMaxLength(100)
                      .IsRequired();

                entity.Property(u => u.LastName)
                      .HasMaxLength(100)
                      .IsRequired();

                entity.Property(u => u.Role)
                      .HasMaxLength(20)
                      .IsRequired();

                // Index on IsActive so filtering active users is fast
                entity.HasIndex(u => u.IsActive);

                // Index on CreatedAt for sorting
                entity.HasIndex(u => u.CreatedAt);
            });

            // ── AuditLog ──────────────────────────────────────────────────────
            builder.Entity<AuditLog>(entity =>
            {
                entity.HasKey(a => a.Id);

                entity.Property(a => a.UserId)
                      .HasMaxLength(450)
                      .IsRequired();

                entity.Property(a => a.UserEmail)
                      .HasMaxLength(256)
                      .IsRequired();

                entity.Property(a => a.Action)
                      .HasMaxLength(50)
                      .IsRequired();

                entity.Property(a => a.EntityType)
                      .HasMaxLength(50)
                      .IsRequired();

                entity.Property(a => a.EntityId)
                      .HasMaxLength(450)
                      .IsRequired();

                // Index on UserId — SuperAdmin will query "all actions by this user"
                entity.HasIndex(a => a.UserId);

                // Index on Action — SuperAdmin will query "all login failures"
                entity.HasIndex(a => a.Action);

                // Index on CreatedAt — SuperAdmin will query by date range
                entity.HasIndex(a => a.CreatedAt);

                // Composite index for the most common admin query:
                // "show me all actions on this entity"
                entity.HasIndex(a => new { a.EntityType, a.EntityId });
            });
        }
    }
}