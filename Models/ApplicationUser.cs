using Microsoft.AspNetCore.Identity;

namespace VehiclePortal.Models
{
	public class ApplicationUser : IdentityUser
	{
		public string FirstName { get; set; } = string.Empty;
		public string LastName { get; set; } = string.Empty;
		public string Role { get; set; } = string.Empty; // SuperAdmin | Seller | Buyer

		// Soft delete — we never hard delete users, we deactivate them
		public bool IsActive { get; set; } = true;
		public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
		public DateTime? DeactivatedAt { get; set; }
	}
}