import { useState, useEffect } from "react";
import { useNavigate } from "react-router-dom";
import { Loader2, ArrowRight, AlertCircle } from "lucide-react";
import { apiClient } from "@/lib/api-client";
import type { PurchaseOrder } from "@/types/procurement";
import { FileUploadZone } from "@/components/orders/FileUploadZone";
import { StatusBadge } from "@/components/ui/status-badge";
import { OrderLineTable } from "@/components/orders/OrderLineTable";
import { Button } from "@/components/ui/button";
import { Card, CardContent, CardHeader, CardTitle, CardDescription } from "@/components/ui/card";
import {
  Select,
  SelectContent,
  SelectItem,
  SelectTrigger,
  SelectValue,
} from "@/components/ui/select";
import { useToast } from "@/hooks/use-toast";

export default function UploadPage() {
  const [file, setFile] = useState<File | null>(null);
  const [supplier, setSupplier] = useState<string>("");
  const [suppliers, setSuppliers] = useState<string[]>([]);
  const [uploading, setUploading] = useState(false);
  const [result, setResult] = useState<PurchaseOrder | null>(null);
  const navigate = useNavigate();
  const { toast } = useToast();

  useEffect(() => {
    apiClient.getSuppliers().then(setSuppliers);
  }, []);

  const handleUpload = async () => {
    if (!file || !supplier) return;

    setUploading(true);
    setResult(null);

    try {
      const uploadResult = await apiClient.uploadPurchaseOrder(file, supplier);
      setResult(uploadResult.order);
      
      toast({
        title: "Order processed successfully",
        description: `PO ${uploadResult.order.poNumber} has been validated`,
      });
    } catch (error) {
      toast({
        title: "Upload failed",
        description: error instanceof Error ? error.message : "Unknown error occurred",
        variant: "destructive",
      });
    } finally {
      setUploading(false);
    }
  };

  const handleReset = () => {
    setFile(null);
    setSupplier("");
    setResult(null);
  };

  return (
    <div className="page-container animate-fade-in">
      <div className="mb-8">
        <h1 className="text-2xl font-bold text-foreground mb-1">Upload Purchase Order</h1>
        <p className="text-muted-foreground">
          Upload a CSV or Excel file to validate and convert to canonical format
        </p>
      </div>

      <div className="grid grid-cols-1 lg:grid-cols-2 gap-6">
        {/* Upload Form */}
        <div className="space-y-6">
          <Card>
            <CardHeader>
              <CardTitle>1. Select File</CardTitle>
              <CardDescription>
                Upload your purchase order in CSV or Excel format
              </CardDescription>
            </CardHeader>
            <CardContent>
              <FileUploadZone
                selectedFile={file}
                onFileSelect={setFile}
                onClear={() => setFile(null)}
                disabled={uploading}
              />
            </CardContent>
          </Card>

          <Card>
            <CardHeader>
              <CardTitle>2. Select Supplier</CardTitle>
              <CardDescription>
                Choose the destination supplier for validation rules
              </CardDescription>
            </CardHeader>
            <CardContent>
              <Select value={supplier} onValueChange={setSupplier} disabled={uploading}>
                <SelectTrigger className="w-full">
                  <SelectValue placeholder="Select a supplier..." />
                </SelectTrigger>
                <SelectContent>
                  {suppliers.map((s) => (
                    <SelectItem key={s} value={s}>
                      {s}
                    </SelectItem>
                  ))}
                </SelectContent>
              </Select>
            </CardContent>
          </Card>

          <div className="flex gap-3">
            <Button
              onClick={handleUpload}
              disabled={!file || !supplier || uploading}
              className="flex-1"
            >
              {uploading ? (
                <>
                  <Loader2 className="mr-2 h-4 w-4 animate-spin" />
                  Processing...
                </>
              ) : (
                <>
                  Upload & Validate
                  <ArrowRight className="ml-2 h-4 w-4" />
                </>
              )}
            </Button>
            {result && (
              <Button variant="outline" onClick={handleReset}>
                Upload Another
              </Button>
            )}
          </div>
        </div>

        {/* Result Panel */}
        <div>
          {result ? (
            <Card className="animate-slide-up">
              <CardHeader className="flex flex-row items-start justify-between space-y-0">
                <div>
                  <CardTitle className="mb-2">Validation Result</CardTitle>
                  <StatusBadge status={result.automationStatus} size="lg" />
                </div>
                <Button
                  variant="outline"
                  size="sm"
                  onClick={() => navigate(`/orders/${result.id}`)}
                >
                  View Details
                  <ArrowRight className="ml-1.5 h-3.5 w-3.5" />
                </Button>
              </CardHeader>
              <CardContent className="space-y-6">
                {result.automationReason && (
                  <div className="flex gap-3 p-4 rounded-lg bg-warning-muted border border-warning/20">
                    <AlertCircle className="h-5 w-5 text-warning shrink-0 mt-0.5" />
                    <div>
                      <p className="font-medium text-warning-foreground text-sm mb-1">
                        Clarification Required
                      </p>
                      <p className="text-sm text-warning-foreground/80">
                        {result.automationReason}
                      </p>
                    </div>
                  </div>
                )}

                <div>
                  <h4 className="text-sm font-medium text-muted-foreground mb-3">Order Header</h4>
                  <div className="grid grid-cols-2 gap-4 text-sm">
                    <div>
                      <span className="text-muted-foreground">Order ID</span>
                      <p className="font-mono font-medium">{result.id}</p>
                    </div>
                    <div>
                      <span className="text-muted-foreground">PO Number</span>
                      <p className="font-medium">{result.poNumber}</p>
                    </div>
                    <div>
                      <span className="text-muted-foreground">Buyer</span>
                      <p className="font-medium">{result.buyerName}</p>
                    </div>
                    <div>
                      <span className="text-muted-foreground">Supplier</span>
                      <p className="font-medium">{result.supplierName}</p>
                    </div>
                    <div>
                      <span className="text-muted-foreground">Order Date</span>
                      <p className="font-medium">
                        {new Date(result.orderDate).toLocaleDateString()}
                      </p>
                    </div>
                    <div>
                      <span className="text-muted-foreground">Currency</span>
                      <p className="font-medium">{result.currency}</p>
                    </div>
                  </div>
                </div>

                <div>
                  <h4 className="text-sm font-medium text-muted-foreground mb-3">
                    Line Items ({result.lines.length})
                  </h4>
                  <OrderLineTable
                    lines={result.lines}
                    currency={result.currency}
                  />
                </div>
              </CardContent>
            </Card>
          ) : (
            <Card className="h-full flex items-center justify-center min-h-[400px]">
              <CardContent className="text-center py-12">
                <div className="w-16 h-16 rounded-full bg-muted flex items-center justify-center mx-auto mb-4">
                  <ArrowRight className="h-7 w-7 text-muted-foreground" />
                </div>
                <h3 className="text-lg font-medium text-foreground mb-2">
                  Results will appear here
                </h3>
                <p className="text-sm text-muted-foreground max-w-xs mx-auto">
                  Upload a file and select a supplier to see validation results
                </p>
              </CardContent>
            </Card>
          )}
        </div>
      </div>
    </div>
  );
}
