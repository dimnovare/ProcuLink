import { useEffect, useState } from "react";
import { Link } from "react-router-dom";
import { FileText, Upload, CheckCircle2, AlertTriangle, ArrowUpRight } from "lucide-react";
import { apiClient } from "@/lib/api-client";
import type { PurchaseOrderSummary } from "@/types/procurement";
import { StatusBadge } from "@/components/ui/status-badge";
import { Button } from "@/components/ui/button";
import { Card, CardContent, CardHeader, CardTitle } from "@/components/ui/card";

export default function Dashboard() {
  const [orders, setOrders] = useState<PurchaseOrderSummary[]>([]);
  const [loading, setLoading] = useState(true);

  useEffect(() => {
    apiClient.getOrders()
      .then(setOrders)
      .finally(() => setLoading(false));
  }, []);

  const stats = {
    total: orders.length,
    automatable: orders.filter(o => o.automationStatus === "Automatable").length,
    needsClarification: orders.filter(o => o.automationStatus === "NeedsClarification").length,
    totalValue: orders.reduce((sum, o) => sum + o.totalValue, 0),
  };

  const recentOrders = orders.slice(0, 5);

  return (
    <div className="page-container animate-fade-in">
      <div className="mb-8">
        <h1 className="text-2xl font-bold text-foreground mb-1">Dashboard</h1>
        <p className="text-muted-foreground">
          Monitor your purchase order processing at a glance
        </p>
      </div>

      {/* Stats Grid */}
      <div className="grid grid-cols-1 sm:grid-cols-2 lg:grid-cols-4 gap-4 mb-8">
        <Card>
          <CardHeader className="flex flex-row items-center justify-between space-y-0 pb-2">
            <CardTitle className="text-sm font-medium text-muted-foreground">Total Orders</CardTitle>
            <FileText className="h-4 w-4 text-muted-foreground" />
          </CardHeader>
          <CardContent>
            <div className="text-2xl font-bold">{stats.total}</div>
          </CardContent>
        </Card>

        <Card>
          <CardHeader className="flex flex-row items-center justify-between space-y-0 pb-2">
            <CardTitle className="text-sm font-medium text-muted-foreground">Automatable</CardTitle>
            <CheckCircle2 className="h-4 w-4 text-success" />
          </CardHeader>
          <CardContent>
            <div className="text-2xl font-bold text-success">{stats.automatable}</div>
            <p className="text-xs text-muted-foreground mt-1">
              {stats.total > 0 ? Math.round((stats.automatable / stats.total) * 100) : 0}% success rate
            </p>
          </CardContent>
        </Card>

        <Card>
          <CardHeader className="flex flex-row items-center justify-between space-y-0 pb-2">
            <CardTitle className="text-sm font-medium text-muted-foreground">Needs Clarification</CardTitle>
            <AlertTriangle className="h-4 w-4 text-warning" />
          </CardHeader>
          <CardContent>
            <div className="text-2xl font-bold text-warning">{stats.needsClarification}</div>
            <p className="text-xs text-muted-foreground mt-1">
              Requires manual review
            </p>
          </CardContent>
        </Card>

        <Card>
          <CardHeader className="flex flex-row items-center justify-between space-y-0 pb-2">
            <CardTitle className="text-sm font-medium text-muted-foreground">Total Value</CardTitle>
            <span className="text-xs font-medium text-muted-foreground">USD</span>
          </CardHeader>
          <CardContent>
            <div className="text-2xl font-bold">
              ${stats.totalValue.toLocaleString('en-US', { minimumFractionDigits: 2 })}
            </div>
          </CardContent>
        </Card>
      </div>

      {/* Quick Actions */}
      <div className="grid grid-cols-1 lg:grid-cols-3 gap-6 mb-8">
        <Card className="lg:col-span-2">
          <CardHeader className="flex flex-row items-center justify-between">
            <CardTitle className="text-lg">Recent Orders</CardTitle>
            <Link to="/orders">
              <Button variant="ghost" size="sm" className="gap-1">
                View all
                <ArrowUpRight className="h-4 w-4" />
              </Button>
            </Link>
          </CardHeader>
          <CardContent>
            {loading ? (
              <div className="space-y-3">
                {[...Array(3)].map((_, i) => (
                  <div key={i} className="h-16 bg-muted rounded-lg animate-pulse" />
                ))}
              </div>
            ) : recentOrders.length === 0 ? (
              <div className="text-center py-8 text-muted-foreground">
                <FileText className="h-10 w-10 mx-auto mb-3 opacity-50" />
                <p>No orders yet</p>
                <Link to="/upload" className="text-accent hover:underline text-sm">
                  Upload your first order
                </Link>
              </div>
            ) : (
              <div className="space-y-3">
                {recentOrders.map((order) => (
                  <Link
                    key={order.id}
                    to={`/orders/${order.id}`}
                    className="flex items-center justify-between p-3 rounded-lg border border-border hover:bg-muted/50 transition-colors"
                  >
                    <div className="flex-1 min-w-0">
                      <p className="font-medium text-sm truncate">{order.poNumber}</p>
                      <p className="text-xs text-muted-foreground truncate">
                        {order.supplierName} • {order.lineCount} items
                      </p>
                    </div>
                    <StatusBadge status={order.automationStatus} size="sm" />
                  </Link>
                ))}
              </div>
            )}
          </CardContent>
        </Card>

        <Card>
          <CardHeader>
            <CardTitle className="text-lg">Quick Actions</CardTitle>
          </CardHeader>
          <CardContent className="space-y-3">
            <Link to="/upload" className="block">
              <Button className="w-full justify-start gap-3" variant="default">
                <Upload className="h-4 w-4" />
                Upload Purchase Order
              </Button>
            </Link>
            <Link to="/orders" className="block">
              <Button className="w-full justify-start gap-3" variant="outline">
                <FileText className="h-4 w-4" />
                View All Orders
              </Button>
            </Link>
          </CardContent>
        </Card>
      </div>

      {/* Info Banner */}
      <Card className="bg-primary/5 border-primary/10">
        <CardContent className="flex items-start gap-4 py-4">
          <div className="flex h-10 w-10 shrink-0 items-center justify-center rounded-full bg-primary/10">
            <CheckCircle2 className="h-5 w-5 text-primary" />
          </div>
          <div>
            <h3 className="font-medium text-foreground mb-1">MVP Demo Mode</h3>
            <p className="text-sm text-muted-foreground">
              This frontend is running with mock data. Connect to your .NET backend by setting{" "}
              <code className="px-1 py-0.5 rounded bg-muted text-xs font-mono">VITE_USE_MOCK=false</code>{" "}
              and <code className="px-1 py-0.5 rounded bg-muted text-xs font-mono">VITE_API_BASE_URL</code>.
            </p>
          </div>
        </CardContent>
      </Card>
    </div>
  );
}
