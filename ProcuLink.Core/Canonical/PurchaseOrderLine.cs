using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ProcuLink.Core.Canonical
{
    public class PurchaseOrderLine
    {
        public int LineNumber { get; set; }
        public string BuyerItemCode { get; set; } = string.Empty;
        public string? SupplierItemCode { get; set; }
        public string Description { get; set; } = string.Empty;
        public decimal Quantity { get; set; }
        public decimal UnitPrice { get; set; }
    }
}
