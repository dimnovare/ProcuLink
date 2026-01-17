export type AutomationStatus = "Automatable" | "NeedsClarification";

export interface PurchaseOrderLine {
  lineNumber: number;
  buyerItemCode: string;
  supplierItemCode?: string | null;
  description: string;
  quantity: number;
  unitPrice: number;
}

export interface PurchaseOrder {
  id: string;
  buyerName: string;
  supplierName: string;
  poNumber: string;
  orderDate: string; // ISO
  currency: string;
  automationStatus: AutomationStatus;
  automationReason?: string | null;
  lines: PurchaseOrderLine[];
  createdAt: string; // ISO
}

export interface PurchaseOrderSummary {
  id: string;
  poNumber: string;
  supplierName: string;
  buyerName: string;
  orderDate: string;
  automationStatus: AutomationStatus;
  createdAt: string;
  lineCount: number;
  totalValue: number;
  currency: string;
}

export interface SupplierProfile {
  name: string;
  requiredFields: string[];
  requiresSupplierItemCode: boolean;
  supportsPartialAutomation: boolean;
}

export interface UploadResult {
  order: PurchaseOrder;
  validationMessages: string[];
}
