import type { PurchaseOrder, PurchaseOrderSummary, UploadResult } from "@/types/procurement";

const API_BASE_URL = import.meta.env.VITE_API_BASE_URL || "http://localhost:5000";
const USE_MOCK = import.meta.env.VITE_USE_MOCK === "true" || true; // Default to mock for MVP

// Mock data store
let mockOrders: PurchaseOrder[] = [
  {
    id: "ord-001",
    buyerName: "Acme Corp",
    supplierName: "FastParts Inc",
    poNumber: "PO-2024-001234",
    orderDate: "2024-01-10T00:00:00Z",
    currency: "USD",
    automationStatus: "Automatable",
    automationReason: null,
    lines: [
      { lineNumber: 1, buyerItemCode: "ACM-BOLT-001", supplierItemCode: "FP-B001", description: "Steel Bolt M10x50", quantity: 500, unitPrice: 0.45 },
      { lineNumber: 2, buyerItemCode: "ACM-NUT-001", supplierItemCode: "FP-N001", description: "Steel Nut M10", quantity: 500, unitPrice: 0.25 },
      { lineNumber: 3, buyerItemCode: "ACM-WASHER-001", supplierItemCode: "FP-W001", description: "Steel Washer M10", quantity: 1000, unitPrice: 0.10 },
    ],
    createdAt: "2024-01-10T14:30:00Z",
  },
  {
    id: "ord-002",
    buyerName: "TechBuild Ltd",
    supplierName: "ElectroSupply Co",
    poNumber: "PO-2024-005678",
    orderDate: "2024-01-12T00:00:00Z",
    currency: "EUR",
    automationStatus: "NeedsClarification",
    automationReason: "Missing supplier item codes for 2 line items: lines 2 and 4. Supplier requires all item codes for automated processing.",
    lines: [
      { lineNumber: 1, buyerItemCode: "TB-CAP-100", supplierItemCode: "ES-CAP-100UF", description: "Capacitor 100µF", quantity: 200, unitPrice: 0.35 },
      { lineNumber: 2, buyerItemCode: "TB-RES-220", supplierItemCode: null, description: "Resistor 220Ω", quantity: 500, unitPrice: 0.02 },
      { lineNumber: 3, buyerItemCode: "TB-LED-RED", supplierItemCode: "ES-LED-R5MM", description: "LED Red 5mm", quantity: 100, unitPrice: 0.15 },
      { lineNumber: 4, buyerItemCode: "TB-WIRE-22", supplierItemCode: null, description: "Wire 22AWG Black 100m", quantity: 5, unitPrice: 12.50 },
    ],
    createdAt: "2024-01-12T09:15:00Z",
  },
  {
    id: "ord-003",
    buyerName: "MegaMfg Industries",
    supplierName: "FastParts Inc",
    poNumber: "PO-2024-009012",
    orderDate: "2024-01-14T00:00:00Z",
    currency: "USD",
    automationStatus: "Automatable",
    automationReason: null,
    lines: [
      { lineNumber: 1, buyerItemCode: "MM-SHAFT-A1", supplierItemCode: "FP-SH-A1", description: "Drive Shaft Type A", quantity: 25, unitPrice: 145.00 },
      { lineNumber: 2, buyerItemCode: "MM-BEARING-6205", supplierItemCode: "FP-BR-6205", description: "Ball Bearing 6205-2RS", quantity: 50, unitPrice: 8.75 },
    ],
    createdAt: "2024-01-14T11:45:00Z",
  },
];

const MOCK_SUPPLIERS = [
  { name: "FastParts Inc", requiresSupplierItemCode: true },
  { name: "ElectroSupply Co", requiresSupplierItemCode: true },
  { name: "GlobalComponents", requiresSupplierItemCode: false },
  { name: "PrecisionMfg", requiresSupplierItemCode: true },
];

// Mock API implementations
async function mockUploadPurchaseOrder(file: File, supplierName: string): Promise<UploadResult> {
  // Simulate network delay
  await new Promise(resolve => setTimeout(resolve, 1500));

  const supplier = MOCK_SUPPLIERS.find(s => s.name === supplierName);
  const requiresItemCode = supplier?.requiresSupplierItemCode ?? true;

  // Generate mock order from "parsed" file
  const lines = [
    { lineNumber: 1, buyerItemCode: "ITEM-001", supplierItemCode: requiresItemCode ? null : "SUP-001", description: "Sample Part A", quantity: 100, unitPrice: 25.00 },
    { lineNumber: 2, buyerItemCode: "ITEM-002", supplierItemCode: "SUP-002", description: "Sample Part B", quantity: 50, unitPrice: 15.50 },
    { lineNumber: 3, buyerItemCode: "ITEM-003", supplierItemCode: requiresItemCode ? null : "SUP-003", description: "Sample Part C", quantity: 200, unitPrice: 8.25 },
  ];

  const missingCodes = lines.filter(l => !l.supplierItemCode && requiresItemCode);
  const needsClarification = missingCodes.length > 0;

  const newOrder: PurchaseOrder = {
    id: `ord-${Date.now()}`,
    buyerName: "Your Company",
    supplierName,
    poNumber: `PO-${new Date().getFullYear()}-${String(Math.floor(Math.random() * 100000)).padStart(6, '0')}`,
    orderDate: new Date().toISOString(),
    currency: "USD",
    automationStatus: needsClarification ? "NeedsClarification" : "Automatable",
    automationReason: needsClarification 
      ? `Missing supplier item codes for ${missingCodes.length} line item(s): lines ${missingCodes.map(l => l.lineNumber).join(', ')}. Supplier "${supplierName}" requires all item codes for automated processing.`
      : null,
    lines,
    createdAt: new Date().toISOString(),
  };

  mockOrders.unshift(newOrder);

  return {
    order: newOrder,
    validationMessages: needsClarification 
      ? [`${missingCodes.length} line(s) require supplier item codes`]
      : ["All validation checks passed"],
  };
}

async function mockGetOrders(): Promise<PurchaseOrderSummary[]> {
  await new Promise(resolve => setTimeout(resolve, 300));
  
  return mockOrders.map(order => ({
    id: order.id,
    poNumber: order.poNumber,
    supplierName: order.supplierName,
    buyerName: order.buyerName,
    orderDate: order.orderDate,
    automationStatus: order.automationStatus,
    createdAt: order.createdAt,
    lineCount: order.lines.length,
    totalValue: order.lines.reduce((sum, line) => sum + (line.quantity * line.unitPrice), 0),
    currency: order.currency,
  }));
}

async function mockGetOrderById(id: string): Promise<PurchaseOrder | null> {
  await new Promise(resolve => setTimeout(resolve, 200));
  return mockOrders.find(o => o.id === id) || null;
}

// Real API implementations (for when backend is ready)
async function realUploadPurchaseOrder(file: File, supplierName: string): Promise<UploadResult> {
  const formData = new FormData();
  formData.append("file", file);
  formData.append("supplierName", supplierName);

  const response = await fetch(`${API_BASE_URL}/api/purchase-orders/upload`, {
    method: "POST",
    body: formData,
  });

  if (!response.ok) {
    throw new Error(`Upload failed: ${response.statusText}`);
  }

  return response.json();
}

async function realGetOrders(): Promise<PurchaseOrderSummary[]> {
  const response = await fetch(`${API_BASE_URL}/api/purchase-orders`);
  if (!response.ok) {
    throw new Error(`Failed to fetch orders: ${response.statusText}`);
  }
  return response.json();
}

async function realGetOrderById(id: string): Promise<PurchaseOrder | null> {
  const response = await fetch(`${API_BASE_URL}/api/purchase-orders/${id}`);
  if (response.status === 404) {
    return null;
  }
  if (!response.ok) {
    throw new Error(`Failed to fetch order: ${response.statusText}`);
  }
  return response.json();
}

// Exported API client
export const apiClient = {
  uploadPurchaseOrder: USE_MOCK ? mockUploadPurchaseOrder : realUploadPurchaseOrder,
  getOrders: USE_MOCK ? mockGetOrders : realGetOrders,
  getOrderById: USE_MOCK ? mockGetOrderById : realGetOrderById,
  
  getSuppliers: async () => {
    if (USE_MOCK) {
      return MOCK_SUPPLIERS.map(s => s.name);
    }
    const response = await fetch(`${API_BASE_URL}/api/suppliers`);
    return response.json();
  },
};
