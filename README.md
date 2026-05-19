# VehiclePortal

A full-stack vehicle marketplace built with ASP.NET Core 10, SQLite, RabbitMQ, and Docker.
Buyers can browse listings and make offers. Sellers can list vehicles and manage documents.
SuperAdmins manage users and monitor the system.

---

## Quick start

### Prerequisites

- [Docker Desktop](https://www.docker.com/products/docker-desktop/) (Windows/Mac/Linux)
- [.NET 10 SDK](https://dotnet.microsoft.com/download) (for local development only)
- [Git](https://git-scm.com/)

### Run with Docker

```bash
git clone https://github.com/your-username/VehiclePortal.git
cd VehiclePortal
```

Create your `.env` file (see Environment variables section below), then:

```bash
docker compose up --build
```

The application will be available at:

- **App (HTTPS):** https://localhost:7001
- **App (HTTP):** http://localhost:5000
- **RabbitMQ Management:** http://localhost:15672
- **Swagger API Docs:** https://localhost:7001/swagger

> **Note:** The app uses a self-signed development certificate. Your browser will show a security warning — click "Advanced" and proceed.

---

## Entry points

### Buyer / Seller Portal (unified)

| Page | URL | Description |
|------|-----|-------------|
| Login / Register | https://localhost:7001/portal/login.html | Sign in with email or Google One Tap |
| Browse listings | https://localhost:7001/portal/index.html | Search and filter active vehicle listings |
| Listing detail | https://localhost:7001/portal/listing.html?id=1 | View a listing and make an offer |

**Flow for buyers:**
1. Go to `/portal/login.html` and sign in or create an account
2. Browse listings on the home page
3. Click any listing to view details and submit an offer
4. Click **"Want to sell?"** in the navbar to self-upgrade to a seller account instantly

**Flow for sellers:**
1. Log in at `/portal/login.html`
2. Click **"Want to sell?"** to enable seller access (instant, no approval needed)
3. Click **"Sell my car"** in the navbar to go to the seller dashboard
4. Create a listing, upload documents, publish it
5. View and respond to incoming offers in the **Offers** tab

---

### Seller Dashboard

| Page | URL | Description |
|------|-----|-------------|
| Login | https://localhost:7001/seller/login.html | Seller-only login |
| Dashboard | https://localhost:7001/seller/dashboard.html | Manage listings, documents, offers |

**Seller dashboard tabs:**
- **My Listings** — create, publish, and delete vehicle listings
- **Upload Documents** — upload PDFs/images linked to listings
- **Offers** — view, accept, decline, and complete buyer offers

---

### SuperAdmin Portal

| Page | URL | Description |
|------|-----|-------------|
| Login | https://localhost:7001/admin/login.html | SuperAdmin-only login |
| Dashboard | https://localhost:7001/admin/dashboard.html | User management and system monitoring |

**Admin dashboard tabs:**
- **Users** — create, edit, deactivate users; view uploaded documents per user
- **Audit trail** — complete log of every action in the system
- **Dead Letter Queue** — view and replay failed RabbitMQ events

---

### API Documentation

| URL | Description |
|-----|-------------|
| https://localhost:7001/swagger | Swagger UI — interactive API explorer |

To use Swagger with authentication:
1. Log in at `/admin/login.html` or `/portal/login.html`
2. Open Swagger — the token is auto-injected from your browser session
3. All protected endpoints will work immediately

---

## Test accounts

Set your own credentials in `.env`. The following accounts are created
automatically on first startup using the values you provide:

| Role | Environment variable |
|------|---------------------|
| SuperAdmin | `InitialAdmin__Email` / `InitialAdmin__Password` |
| Seller | `TestSeller__Email` / `TestSeller__Password` |
| Buyer | `TestBuyer__Email` / `TestBuyer__Password` |

Password requirements: minimum 8 characters, at least one uppercase letter, one digit, and one symbol.

---

## Environment variables

Create a `.env` file in the project root with the following variables.
**Never commit this file** — it is listed in `.gitignore`.

```env
# ── JWT ───────────────────────────────────────────────────────────────────────
# Generate a random secret (minimum 32 characters)
# Mac/Linux: openssl rand -base64 32
# Windows:   [Convert]::ToBase64String((1..32 | ForEach-Object { Get-Random -Maximum 256 }))
Jwt__Secret=
Jwt__Issuer=https://localhost:7001
Jwt__Audience=https://localhost:7001

# ── SuperAdmin seed account ───────────────────────────────────────────────────
# Created automatically on first startup
InitialAdmin__Email=
InitialAdmin__Password=

# ── Test accounts (Development mode only) ─────────────────────────────────────
# Created automatically on first startup in Development
TestSeller__Email=
TestSeller__Password=
TestBuyer__Email=
TestBuyer__Password=

# ── RabbitMQ ──────────────────────────────────────────────────────────────────
RabbitMQ__Host=rabbitmq
RabbitMQ__Username=
RabbitMQ__Password=

# ── Google One Tap (optional) ─────────────────────────────────────────────────
# Leave blank to disable Google sign-in
# Get your Client ID from: https://console.cloud.google.com/apis/credentials
# Add https://localhost:7001 to Authorized JavaScript origins
Google__ClientId=

# ── HTTPS development certificate ─────────────────────────────────────────────
# Generate with:
#   dotnet dev-certs https --export-path ./VehiclePortal.pfx --password yourpassword --trust
# Then copy the .pfx file to your user profile HTTPS folder or mount it in docker-compose.yml
ASPNETCORE_Kestrel__Certificates__Default__Path=/https/VehiclePortal.pfx
ASPNETCORE_Kestrel__Certificates__Default__Password=
```

### Generating a JWT secret

```bash
# Mac/Linux
openssl rand -base64 32

# Windows (PowerShell)
[Convert]::ToBase64String((1..32 | ForEach-Object { Get-Random -Maximum 256 }))
```

### Generating the HTTPS certificate

```bash
dotnet dev-certs https --export-path ./VehiclePortal.pfx --password yourpassword --trust
```

---

## Architecture

### Stack

| Layer | Technology |
|-------|-----------|
| Backend | ASP.NET Core 10, C# |
| Database | SQLite + Entity Framework Core |
| Auth | ASP.NET Identity + JWT Bearer |
| Messaging | RabbitMQ (topic exchange) |
| Frontend | Vanilla HTML/CSS/JS (no framework) |
| Container | Docker + Docker Compose |

### Project structure

```
VehiclePortal/
├── Controllers/
│   ├── AuthController.cs       # Login, register
│   ├── AdminController.cs      # SuperAdmin user/document management
│   ├── ListingController.cs    # Vehicle listing CRUD
│   ├── DocumentController.cs   # File upload with MD5 dedup
│   ├── OfferController.cs      # Offer state machine
│   └── PortalController.cs     # Unified portal + Google sign-in
├── Data/
│   └── AppDbContext.cs         # EF Core DbContext
├── Models/
│   ├── ApplicationUser.cs      # Extended Identity user
│   ├── AuditLog.cs             # Audit trail entries
│   ├── Listing.cs              # Vehicle listing
│   ├── Document.cs             # Uploaded file with OCR status
│   ├── Offer.cs                # Buyer offer state machine
│   └── OutboxMessage.cs        # Outbox + DLQ + idempotency
├── Services/
│   ├── IFileStorage.cs         # Storage abstraction (swap for S3)
│   ├── LocalFileStorage.cs     # Disk-based file storage
│   ├── IAuditService.cs        # Audit logging interface
│   ├── AuditService.cs         # Audit logging implementation
│   └── RabbitMqPublisher.cs    # RabbitMQ event publisher
├── Workers/
│   └── OutboxWorker.cs         # Background outbox processor
├── wwwroot/
│   ├── portal/                 # Unified buyer/seller portal
│   │   ├── login.html          # Sign in + Google One Tap
│   │   ├── index.html          # Buyer dashboard
│   │   └── listing.html        # Listing detail + offer form
│   ├── seller/                 # Seller-only dashboard
│   │   ├── login.html
│   │   └── dashboard.html
│   ├── admin/                  # SuperAdmin portal
│   │   ├── login.html
│   │   └── dashboard.html
│   └── js/
│       ├── shared.js           # Shared utilities (auth, fetch, toast)
│       └── swagger-custom.js   # Swagger JWT auto-inject
├── Program.cs                  # App entry point + DI + middleware
├── Dockerfile
└── docker-compose.yml
```

### Event-driven architecture

Every state change writes an `OutboxMessage` in the same database transaction as the business operation. A background `OutboxWorker` polls every 10 seconds and publishes events to RabbitMQ.

```
Business operation (create listing, place offer, etc.)
        ↓
OutboxMessage saved to DB atomically
        ↓
OutboxWorker picks up after 10s
        ↓
Publishes to RabbitMQ exchange: vehicleportal.events
        ↓
Routes to queues by routing key:
  listing.created     → vehicleportal.listing.created
  listing.published   → vehicleportal.listing.published
  listing.deleted     → vehicleportal.listing.deleted
  doc.uploaded        → vehicleportal.doc.uploaded
  offer.placed        → vehicleportal.offer.placed
  offer.accepted      → vehicleportal.offer.accepted
  offer.declined      → vehicleportal.offer.declined
  vin.transferred     → vehicleportal.vin.transferred
        ↓
If publish fails → retry 3x with exponential backoff (10s, 30s, 60s)
        ↓
After 3 failures → DeadLetterMessages table
        ↓
SuperAdmin can replay from DLQ tab in admin dashboard
```

### Role system

| Role | Can do |
|------|--------|
| Buyer | Browse listings, make offers, cancel own offers, self-upgrade to Seller |
| Seller | Everything Buyer can do + create/publish/delete listings, upload documents, accept/decline/complete offers |
| SuperAdmin | Manage all users and roles, delete any document, view audit trail, replay DLQ |

Roles are self-service — any user can upgrade from Buyer to Seller instantly via the **"Want to sell?"** button. SuperAdmin accounts are created manually by other SuperAdmins.

---

## API endpoints

### Public
| Method | Endpoint | Description |
|--------|----------|-------------|
| POST | /api/auth/login | Login with email + password |
| POST | /api/portal/register | Register new Buyer account |
| POST | /api/portal/google | Sign in with Google One Tap |
| GET | /api/portal/config | Returns public config (Google Client ID) |
| GET | /api/listing | Browse active listings (filterable) |
| GET | /api/listing/{id} | Get single listing details |

### Authenticated (any role)
| Method | Endpoint | Description |
|--------|----------|-------------|
| GET | /api/portal/me | Current user profile + roles |
| POST | /api/portal/become-seller | Self-upgrade to Seller role |

### Seller only
| Method | Endpoint | Description |
|--------|----------|-------------|
| GET | /api/listing/my | My listings (includes deleted) |
| POST | /api/listing | Create draft listing |
| PUT | /api/listing/{id} | Update listing |
| POST | /api/listing/{id}/publish | Publish listing (locks VIN) |
| DELETE | /api/listing/{id} | Soft delete listing |
| POST | /api/document/upload | Upload document (MD5 dedup) |
| GET | /api/document/mydocuments | All my documents |
| GET | /api/document/listing/{id} | Documents for a listing |
| POST | /api/document/{id}/link | Link document to listing |
| DELETE | /api/document/{id} | Delete own document |
| GET | /api/offer/listing/{id} | Offers on my listing |
| POST | /api/offer/{id}/accept | Accept an offer |
| POST | /api/offer/{id}/decline | Decline an offer |
| POST | /api/offer/{id}/complete | Complete purchase (VIN transfer) |

### Buyer only
| Method | Endpoint | Description |
|--------|----------|-------------|
| GET | /api/offer/my | My offers |
| POST | /api/offer | Place an offer |
| POST | /api/offer/{id}/cancel | Cancel my offer |

### SuperAdmin only
| Method | Endpoint | Description |
|--------|----------|-------------|
| GET | /api/admin/users | All users with document counts |
| POST | /api/admin/users | Create user |
| PUT | /api/admin/users/{id} | Edit user name/email/roles |
| DELETE | /api/admin/users/{id} | Deactivate user |
| GET | /api/admin/documents | All documents (filterable) |
| DELETE | /api/admin/documents/{id} | Delete any document |
| GET | /api/admin/audit | Audit trail (filterable, paginated) |
| GET | /api/admin/dlq | Dead letter queue |
| POST | /api/admin/dlq/{id}/replay | Replay failed message |

---

## Key design decisions

**Outbox pattern** — Every event is written to the database in the same transaction as the business operation. This guarantees no events are lost even if RabbitMQ is temporarily unavailable.

**Soft deletes** — Users and listings are never hard deleted. `IsDeleted` and `IsActive` flags preserve history for audit purposes.

**MD5 deduplication** — Uploaded documents are hashed. Re-uploading the same file is blocked while the original exists. After deletion the same file can be re-uploaded freely.

**Idempotency keys** — Clients can send `X-Idempotency-Key` headers on offer placement and document upload requests. Duplicate requests within 24 hours return the original response without reprocessing.

**VIN locking** — Publishing a listing locks the VIN to that seller preventing duplicate active listings for the same vehicle. The lock is released when the listing is deleted or the purchase is completed.

**JWT with role claims** — Roles are embedded in the JWT so the server never needs a database lookup to check authorization. When a user upgrades to Seller, a new JWT is issued immediately so the UI updates without re-login.

**IFileStorage abstraction** — File storage is behind an interface. Swap `LocalFileStorage` for `S3FileStorage` in `Program.cs` with one line change. No other code changes needed.

---

## Development

### Running locally (without Docker)

```bash
# Set user secrets
dotnet user-secrets set "Jwt:Secret" "your-secret-min-32-chars"
dotnet user-secrets set "Jwt:Issuer" "https://localhost:7001"
dotnet user-secrets set "Jwt:Audience" "https://localhost:7001"
dotnet user-secrets set "InitialAdmin:Email" "your-admin-email"
dotnet user-secrets set "InitialAdmin:Password" "your-admin-password"
dotnet user-secrets set "TestSeller:Email" "your-seller-email"
dotnet user-secrets set "TestSeller:Password" "your-seller-password"
dotnet user-secrets set "TestBuyer:Email" "your-buyer-email"
dotnet user-secrets set "TestBuyer:Password" "your-buyer-password"
dotnet user-secrets set "RabbitMQ:Host" "localhost"
dotnet user-secrets set "RabbitMQ:Username" "your-rabbitmq-username"
dotnet user-secrets set "RabbitMQ:Password" "your-rabbitmq-password"

# Run
dotnet run
```

### Adding a migration

```powershell
# Rename docker-compose.dcproj first to avoid multi-project error
Rename-Item docker-compose.dcproj docker-compose.dcproj.bak
dotnet ef migrations add YourMigrationName
Rename-Item docker-compose.dcproj.bak docker-compose.dcproj
```

### Resetting the database

```bash
docker compose down -v   # removes all volumes (database + uploads)
docker compose up --build
```

---

## Roadmap

### Phase 4 — Production hardening
- OCR worker (Tesseract locally → AWS Textract)
- Audit trail search/filter UI
- AWS migration (RDS, S3, ECS Fargate, Secrets Manager)

### Phase 5 — Email notifications
- Welcome email on registration
- Offer received/accepted/declined notifications
- VIN transfer confirmation
- AWS SES + MailHog for local development

### Phase 6 — Analytics
- ZIP code geolocation (ZIP → lat/lng)
- Supply/demand heat map by area
- Price trends by make/model/region
- AWS Athena + QuickSight for BI dashboards

### Phase 7 — Chat & messaging
- In-app chat between buyers/sellers and SuperAdmin
- Message model with read/unread status
- Real-time updates via SignalR WebSockets
- Chat history persisted in database
- SuperAdmin inbox — view and respond to all conversations
- Buyer/seller can open a support chat from any page
- Future: direct buyer ↔ seller messaging on accepted offers