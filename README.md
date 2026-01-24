# ProcuLink

Purchase Order Processing Solution - Transform buyer POs into supplier-ready formats.

## Solution Structure

```
ProcuLink/
├── ProcuLink.Api/          # ASP.NET Core REST API
├── ProcuLink.Core/         # Domain models (PurchaseOrder, SupplierProfile, etc.)
├── ProcuLink.Infrastructure/ # File-based repositories
├── ProcuLink.Transform/    # (Future) Transformation logic
├── ProcuLink.Worker/       # (Future) Background processing
└── ProcuLink.Web/          # React frontend (git submodule)
```

## Quick Start

### 1. Clone with Submodules

```bash
git clone --recurse-submodules https://github.com/dimnovare/ProcuLink.git
cd ProcuLink
```

If you already cloned without `--recurse-submodules`:
```bash
git submodule sync --recursive
git submodule update --init --recursive
```

### 2. Run Backend

```bash
cd ProcuLink.Api
dotnet run
```

API runs at: http://localhost:5223
Swagger UI: http://localhost:5223/swagger

### 3. Run Frontend

```bash
cd ProcuLink.Web
cp .env.example .env   # Configure API URL
npm install
npm run dev
```

Frontend runs at: http://localhost:5173

## Configuration

### Supplier Profiles

Create JSON files in `ProcuLink.Api/data/suppliers/`:

**Example: `data/suppliers/AcmeSupplier.json`**
```json
{
  "supplierName": "Acme Supplier",
  "requiresSupplierItemCode": true,
  "requiredFields": [],
  "supportsPartialAutomation": false,
  "acceptedFormats": ["XML", "CSV"]
}
```

### Webhook Delivery (Optional)

Configure in `appsettings.json`:
```json
{
  "Delivery": {
    "WebhookUrl": "https://your-webhook-endpoint.com/receive"
  }
}
```

## API Endpoints

### Purchase Orders

| Method | Endpoint | Description |
|--------|----------|-------------|
| POST | `/api/purchase-orders/upload` | Upload CSV/XLSX file |
| GET | `/api/purchase-orders` | List all orders (summaries) |
| GET | `/api/purchase-orders/{id}` | Get order details |
| POST | `/api/purchase-orders/{id}/transform?format=xml` | Generate outbound file |
| GET | `/api/purchase-orders/{id}/outbound` | Get outbound metadata |
| GET | `/api/purchase-orders/{id}/outbound/download` | Download outbound file |
| POST | `/api/purchase-orders/{id}/send` | Send to webhook |

### Suppliers

| Method | Endpoint | Description |
|--------|----------|-------------|
| GET | `/api/suppliers` | List supplier names |
| GET | `/api/suppliers/profiles` | List all profiles |
| GET | `/api/suppliers/profiles/{name}` | Get profile by name |

### Health

| Method | Endpoint | Description |
|--------|----------|-------------|
| GET | `/health` | Health check |

## End-to-End Demo

### Step 1: Create Supplier Profile

```bash
mkdir -p ProcuLink.Api/data/suppliers
cat > ProcuLink.Api/data/suppliers/TestSupplier.json << 'EOF'
{
  "supplierName": "TestSupplier",
  "requiresSupplierItemCode": true,
  "requiredFields": [],
  "supportsPartialAutomation": false,
  "acceptedFormats": ["XML", "CSV"]
}
EOF
```

### Step 2: Create Test CSV

```bash
cat > /tmp/test-order.csv << 'EOF'
PoNumber,OrderDate,BuyerName,LineNumber,BuyerItemCode,SupplierItemCode,Description,Quantity,UnitPrice,Currency
PO-2024-001,2024-01-20,Acme Corp,1,B001,S001,Widget A,10,25.00,USD
PO-2024-001,2024-01-20,Acme Corp,2,B002,S002,Widget B,5,15.00,USD
EOF
```

### Step 3: Upload Order

```bash
curl -X POST http://localhost:5223/api/purchase-orders/upload \
  -F "file=@/tmp/test-order.csv" \
  -F "supplierName=TestSupplier"
```

Response includes order ID and validation messages.

### Step 4: Transform Order

```bash
# Get order ID from previous response
ORDER_ID="your-order-id-here"

# Generate XML outbound
curl -X POST "http://localhost:5223/api/purchase-orders/$ORDER_ID/transform?format=xml"
```

### Step 5: Download Outbound File

```bash
curl -O http://localhost:5223/api/purchase-orders/$ORDER_ID/outbound/download
```

### Step 6: Send to Webhook (Optional)

```bash
# First configure WebhookUrl in appsettings.json
curl -X POST http://localhost:5223/api/purchase-orders/$ORDER_ID/send
```

## Data Storage

All data is stored in the filesystem under `ProcuLink.Api/data/`:

```
data/
├── orders/          # Purchase order JSON files
│   └── {guid}.json
├── suppliers/       # Supplier profile JSON files
│   └── {name}.json
└── outbound/        # Transform outputs
    └── {orderId}/
        ├── artifact.json
        ├── {supplier}.xml
        └── delivery.json
```

## Automation Status

Orders have one of two automation statuses:

- **Automatable**: All validation checks passed, ready for transform/send
- **NeedsClarification**: Issues found:
  - No supplier profile configured
  - Missing required supplier item codes

## Frontend Environment

Create `.env` from `.env.example`:

```bash
VITE_API_BASE_URL=http://localhost:5223
VITE_USE_MOCK=false
```

## Development

### Prerequisites

- .NET 8 SDK
- Node.js 18+
- npm or bun

### Running Tests

```bash
dotnet test
```

### Building for Production

```bash
dotnet publish -c Release
```

## License

Private - All rights reserved.
