import { useEffect, useState } from "react";
import { Link } from "react-router-dom";
import { Search, FileText, ArrowUpDown } from "lucide-react";
import { apiClient } from "@/lib/api-client";
import type { PurchaseOrderSummary, AutomationStatus } from "@/types/procurement";
import { StatusBadge } from "@/components/ui/status-badge";
import { Input } from "@/components/ui/input";
import { Button } from "@/components/ui/button";
import {
  Select,
  SelectContent,
  SelectItem,
  SelectTrigger,
  SelectValue,
} from "@/components/ui/select";

export default function OrdersPage() {
  const [orders, setOrders] = useState<PurchaseOrderSummary[]>([]);
  const [loading, setLoading] = useState(true);
  const [search, setSearch] = useState("");
  const [statusFilter, setStatusFilter] = useState<AutomationStatus | "all">("all");
  const [sortField, setSortField] = useState<"createdAt" | "poNumber" | "totalValue">("createdAt");
  const [sortDir, setSortDir] = useState<"asc" | "desc">("desc");

  useEffect(() => {
    apiClient.getOrders()
      .then(setOrders)
      .finally(() => setLoading(false));
  }, []);

  const filteredOrders = orders
    .filter((order) => {
      const matchesSearch =
        order.poNumber.toLowerCase().includes(search.toLowerCase()) ||
        order.supplierName.toLowerCase().includes(search.toLowerCase()) ||
        order.buyerName.toLowerCase().includes(search.toLowerCase());
      const matchesStatus = statusFilter === "all" || order.automationStatus === statusFilter;
      return matchesSearch && matchesStatus;
    })
    .sort((a, b) => {
      let comparison = 0;
      if (sortField === "createdAt") {
        comparison = new Date(a.createdAt).getTime() - new Date(b.createdAt).getTime();
      } else if (sortField === "poNumber") {
        comparison = a.poNumber.localeCompare(b.poNumber);
      } else if (sortField === "totalValue") {
        comparison = a.totalValue - b.totalValue;
      }
      return sortDir === "desc" ? -comparison : comparison;
    });

  const toggleSort = (field: typeof sortField) => {
    if (sortField === field) {
      setSortDir(sortDir === "asc" ? "desc" : "asc");
    } else {
      setSortField(field);
      setSortDir("desc");
    }
  };

  const formatCurrency = (value: number, currency: string) => {
    return new Intl.NumberFormat('en-US', {
      style: 'currency',
      currency,
    }).format(value);
  };

  const formatDate = (iso: string) => {
    return new Date(iso).toLocaleDateString('en-US', {
      year: 'numeric',
      month: 'short',
      day: 'numeric',
    });
  };

  return (
    <div className="page-container animate-fade-in">
      <div className="mb-8">
        <h1 className="text-2xl font-bold text-foreground mb-1">Purchase Orders</h1>
        <p className="text-muted-foreground">
          View and manage all processed purchase orders
        </p>
      </div>

      {/* Filters */}
      <div className="card-elevated p-4 mb-6">
        <div className="flex flex-col sm:flex-row gap-4">
          <div className="relative flex-1">
            <Search className="absolute left-3 top-1/2 -translate-y-1/2 h-4 w-4 text-muted-foreground" />
            <Input
              placeholder="Search by PO number, supplier, or buyer..."
              value={search}
              onChange={(e) => setSearch(e.target.value)}
              className="pl-10"
            />
          </div>
          <Select
            value={statusFilter}
            onValueChange={(v) => setStatusFilter(v as typeof statusFilter)}
          >
            <SelectTrigger className="w-full sm:w-48">
              <SelectValue placeholder="Filter by status" />
            </SelectTrigger>
            <SelectContent>
              <SelectItem value="all">All Statuses</SelectItem>
              <SelectItem value="Automatable">Automatable</SelectItem>
              <SelectItem value="NeedsClarification">Needs Clarification</SelectItem>
            </SelectContent>
          </Select>
        </div>
      </div>

      {/* Orders Table */}
      <div className="card-elevated overflow-hidden">
        {loading ? (
          <div className="p-8 space-y-4">
            {[...Array(5)].map((_, i) => (
              <div key={i} className="h-16 bg-muted rounded animate-pulse" />
            ))}
          </div>
        ) : filteredOrders.length === 0 ? (
          <div className="text-center py-16">
            <FileText className="h-12 w-12 mx-auto mb-4 text-muted-foreground/50" />
            <h3 className="text-lg font-medium text-foreground mb-1">No orders found</h3>
            <p className="text-sm text-muted-foreground mb-4">
              {search || statusFilter !== "all"
                ? "Try adjusting your filters"
                : "Upload your first purchase order to get started"}
            </p>
            {!search && statusFilter === "all" && (
              <Link to="/upload">
                <Button>Upload Order</Button>
              </Link>
            )}
          </div>
        ) : (
          <div className="overflow-x-auto">
            <table className="data-table">
              <thead>
                <tr>
                  <th>
                    <button
                      onClick={() => toggleSort("poNumber")}
                      className="flex items-center gap-1 hover:text-foreground transition-colors"
                    >
                      PO Number
                      <ArrowUpDown className="h-3.5 w-3.5" />
                    </button>
                  </th>
                  <th>Supplier</th>
                  <th>Buyer</th>
                  <th>Order Date</th>
                  <th>Lines</th>
                  <th>
                    <button
                      onClick={() => toggleSort("totalValue")}
                      className="flex items-center gap-1 hover:text-foreground transition-colors"
                    >
                      Total Value
                      <ArrowUpDown className="h-3.5 w-3.5" />
                    </button>
                  </th>
                  <th>Status</th>
                  <th>
                    <button
                      onClick={() => toggleSort("createdAt")}
                      className="flex items-center gap-1 hover:text-foreground transition-colors"
                    >
                      Created
                      <ArrowUpDown className="h-3.5 w-3.5" />
                    </button>
                  </th>
                </tr>
              </thead>
              <tbody>
                {filteredOrders.map((order) => (
                  <tr key={order.id}>
                    <td>
                      <Link
                        to={`/orders/${order.id}`}
                        className="font-medium text-accent hover:underline"
                      >
                        {order.poNumber}
                      </Link>
                    </td>
                    <td className="font-medium">{order.supplierName}</td>
                    <td className="text-muted-foreground">{order.buyerName}</td>
                    <td className="text-muted-foreground">{formatDate(order.orderDate)}</td>
                    <td className="font-mono">{order.lineCount}</td>
                    <td className="font-mono">
                      {formatCurrency(order.totalValue, order.currency)}
                    </td>
                    <td>
                      <StatusBadge status={order.automationStatus} size="sm" />
                    </td>
                    <td className="text-muted-foreground text-sm">
                      {formatDate(order.createdAt)}
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}
      </div>

      {/* Stats Footer */}
      {!loading && filteredOrders.length > 0 && (
        <div className="mt-4 flex items-center justify-between text-sm text-muted-foreground">
          <span>
            Showing {filteredOrders.length} of {orders.length} orders
          </span>
          <span>
            Total value:{" "}
            <span className="font-medium text-foreground">
              ${filteredOrders.reduce((sum, o) => sum + o.totalValue, 0).toLocaleString('en-US', { minimumFractionDigits: 2 })}
            </span>
          </span>
        </div>
      )}
    </div>
  );
}
