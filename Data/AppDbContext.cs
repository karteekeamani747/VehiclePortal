using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using VehiclePortal.Models;

namespace VehiclePortal.Data
{
    public class AppDbContext : IdentityDbContext<ApplicationUser>
    {
        public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

        public DbSet<AuditLog> AuditLogs { get; set; }
        public DbSet<Listing> Listings { get; set; }
        public DbSet<Document> Documents { get; set; }
        public DbSet<OutboxMessage> OutboxMessages { get; set; }
        public DbSet<DeadLetterMessage> DeadLetterMessages { get; set; }
        public DbSet<IdempotencyLog> IdempotencyLogs { get; set; }
        public DbSet<Offer> Offers { get; set; }

        protected override void OnModelCreating(ModelBuilder builder)
        {
            base.OnModelCreating(builder);

            // ── ApplicationUser ───────────────────────────────────────────────
            builder.Entity<ApplicationUser>(entity =>
            {
                entity.Property(u => u.FirstName).HasMaxLength(100).IsRequired();
                entity.Property(u => u.LastName).HasMaxLength(100).IsRequired();
                entity.Property(u => u.Role).HasMaxLength(20).IsRequired();
                entity.HasIndex(u => u.IsActive);
                entity.HasIndex(u => u.CreatedAt);
            });

            // ── AuditLog ──────────────────────────────────────────────────────
            builder.Entity<AuditLog>(entity =>
            {
                entity.HasKey(a => a.Id);
                entity.Property(a => a.UserId).HasMaxLength(450).IsRequired();
                entity.Property(a => a.UserEmail).HasMaxLength(256).IsRequired();
                entity.Property(a => a.Action).HasMaxLength(50).IsRequired();
                entity.Property(a => a.EntityType).HasMaxLength(50).IsRequired();
                entity.Property(a => a.EntityId).HasMaxLength(450).IsRequired();
                entity.HasIndex(a => a.UserId);
                entity.HasIndex(a => a.Action);
                entity.HasIndex(a => a.CreatedAt);
                entity.HasIndex(a => new { a.EntityType, a.EntityId });
            });

            // ── Listing ───────────────────────────────────────────────────────
            builder.Entity<Listing>(entity =>
            {
                entity.HasKey(l => l.Id);
                entity.Property(l => l.SellerId).HasMaxLength(450).IsRequired();
                entity.Property(l => l.Vin).HasMaxLength(17).IsRequired();
                entity.Property(l => l.Make).HasMaxLength(100).IsRequired();
                entity.Property(l => l.Model).HasMaxLength(100).IsRequired();
                entity.Property(l => l.Color).HasMaxLength(50).IsRequired();
                entity.Property(l => l.ZipCode).HasMaxLength(10).IsRequired();
                entity.Property(l => l.AskingPrice).HasPrecision(18, 2);
                entity.Property(l => l.Status).HasConversion<int>();
                entity.HasIndex(l => l.Vin);
                entity.HasIndex(l => l.SellerId);
                entity.HasIndex(l => l.Status);
                entity.HasIndex(l => new { l.Make, l.Model, l.Year });
                entity.HasIndex(l => l.ZipCode);
                entity.HasQueryFilter(l => !l.IsDeleted);
                entity.HasOne(l => l.Seller)
                      .WithMany()
                      .HasForeignKey(l => l.SellerId)
                      .OnDelete(DeleteBehavior.Restrict);
                entity.HasMany(l => l.Documents)
                      .WithOne(d => d.Listing)
                      .HasForeignKey(d => d.ListingId)
                      .OnDelete(DeleteBehavior.SetNull);
            });

            // ── Document ──────────────────────────────────────────────────────
            builder.Entity<Document>(entity =>
            {
                entity.HasKey(d => d.Id);
                entity.Property(d => d.UploadedBy).HasMaxLength(450).IsRequired();
                entity.Property(d => d.FileName).HasMaxLength(255).IsRequired();
                entity.Property(d => d.StoredName).HasMaxLength(255).IsRequired();
                entity.Property(d => d.FilePath).HasMaxLength(500).IsRequired();
                entity.Property(d => d.ContentType).HasMaxLength(100).IsRequired();
                entity.Property(d => d.FileHash).HasMaxLength(64);
                entity.Property(d => d.IdempotencyKey).HasMaxLength(100);
                entity.Property(d => d.Type).HasConversion<int>();
                entity.Property(d => d.OcrStatus).HasConversion<int>();
                entity.HasIndex(d => d.UploadedBy);
                entity.HasIndex(d => d.ListingId);
                entity.HasIndex(d => d.OcrStatus);
                // Index for fast duplicate detection by hash + uploader
                entity.HasIndex(d => new { d.FileHash, d.UploadedBy });
                // Index for idempotency key lookup
                entity.HasIndex(d => d.IdempotencyKey);
            });

            // ── OutboxMessage ─────────────────────────────────────────────────
            builder.Entity<OutboxMessage>(entity =>
            {
                entity.HasKey(o => o.Id);
                entity.Property(o => o.EventType).HasMaxLength(100).IsRequired();
                entity.Property(o => o.CreatedBy).HasMaxLength(450).IsRequired();
                entity.Property(o => o.Status).HasConversion<int>();
                // Worker polls for Pending messages — this index is critical
                entity.HasIndex(o => o.Status);
                entity.HasIndex(o => o.NextRetryAt);
                entity.HasIndex(o => o.CreatedAt);
            });

            // ── DeadLetterMessage ─────────────────────────────────────────────
            builder.Entity<DeadLetterMessage>(entity =>
            {
                entity.HasKey(d => d.Id);
                entity.Property(d => d.EventType).HasMaxLength(100).IsRequired();
                entity.Property(d => d.CreatedBy).HasMaxLength(450).IsRequired();
                entity.HasIndex(d => d.EventType);
                entity.HasIndex(d => d.IsReplayed);
                entity.HasIndex(d => d.CreatedAt);
            });

            // ── IdempotencyLog ────────────────────────────────────────────────
            builder.Entity<IdempotencyLog>(entity =>
            {
                entity.HasKey(i => i.Id);
                entity.Property(i => i.IdempotencyKey).HasMaxLength(100).IsRequired();
                entity.Property(i => i.UserId).HasMaxLength(450).IsRequired();
                entity.Property(i => i.Endpoint).HasMaxLength(200).IsRequired();
                // Unique constraint — same key can only be used once per user
                entity.HasIndex(i => new { i.IdempotencyKey, i.UserId }).IsUnique();
                // Index for cleanup job that removes expired entries
                entity.HasIndex(i => i.ExpiresAt);
            });

            // ── Offer ─────────────────────────────────────────────────────────────────
            builder.Entity<Offer>(entity =>
            {
                entity.HasKey(o => o.Id);
                entity.Property(o => o.BuyerId).HasMaxLength(450).IsRequired();
                entity.Property(o => o.SellerId).HasMaxLength(450).IsRequired();
                entity.Property(o => o.Amount).HasPrecision(18, 2);
                entity.Property(o => o.Status).HasConversion<int>();
                entity.Property(o => o.IdempotencyKey).HasMaxLength(100);

                // Buyer can have multiple offers on different listings
                entity.HasIndex(o => o.BuyerId);

                // Seller queries all offers on their listings
                entity.HasIndex(o => o.SellerId);

                // Query offers by listing
                entity.HasIndex(o => o.ListingId);

                // Query offers by status
                entity.HasIndex(o => o.Status);

                // Idempotency key lookup
                entity.HasIndex(o => o.IdempotencyKey);

                entity.HasOne(o => o.Listing)
                      .WithMany()
                      .HasForeignKey(o => o.ListingId)
                      .OnDelete(DeleteBehavior.Restrict);

                entity.HasOne(o => o.Buyer)
                      .WithMany()
                      .HasForeignKey(o => o.BuyerId)
                      .OnDelete(DeleteBehavior.Restrict);

                entity.HasOne(o => o.Seller)
                      .WithMany()
                      .HasForeignKey(o => o.SellerId)
                      .OnDelete(DeleteBehavior.Restrict);
            });
        }
    }
}