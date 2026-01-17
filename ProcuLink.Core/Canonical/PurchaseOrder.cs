using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ProcuLink.Core.Canonical
{
    public enum AutomationStatus
    {
        Automatable,
        NeedsClarification
    }

    public class PurchaseOrder
    {
        public Guid Id { get; set; }
        public string BuyerName { get; set; } = string.Empty;
        public string SupplierName { get; set; } = string.Empty;
        public string PoNumber { get; set; } = string.Empty;
        public DateTime OrderDate { get; set; }
        public string Currency { get; set; } = string.Empty;
        public List<PurchaseOrderLine> Lines { get; set; } = new List<PurchaseOrderLine>();
        public AutomationStatus AutomationStatus { get; set; }
        public string? AutomationReason { get; set; }
        public DateTime CreatedAt { get; set; }
    }
}
