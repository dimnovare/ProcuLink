namespace ProcuLink.Core.Documents;

public static class FieldMeanings
{
    public const string DocumentNumber = "document_number";
    public const string IssueDate = "issue_date";
    public const string DueDate = "due_date";
    public const string SenderName = "sender_name";
    public const string SenderVatId = "sender_vat_id";
    public const string ReceiverName = "receiver_name";
    public const string Currency = "currency";
    public const string TotalNet = "total_net";
    public const string TotalTax = "total_tax";
    public const string TotalGross = "total_gross";
    public const string Iban = "iban";
    public const string PaymentReference = "payment_reference";
    public const string ReceiverVatId = "receiver_vat_id";
    public const string BuyerReference = "buyer_reference";
    public const string OrderReference = "order_reference";
    public const string DeliveryDate = "delivery_date";
    public const string SenderCountry = "sender_country";
    public const string ReceiverCountry = "receiver_country";
    /// <summary>A Peppol participant id, scheme and id: <c>0088:0000000000000</c>. Present only in that shape.</summary>
    public const string SenderEndpointId = "sender_endpoint_id";
    public const string ReceiverEndpointId = "receiver_endpoint_id";

    public const string LineDescription = "line_description";
    public const string LineQuantity = "line_quantity";
    public const string LineUnit = "line_unit";
    public const string LineUnitPrice = "line_unit_price";
    public const string LineAmount = "line_amount";
    public const string LineTaxRate = "line_tax_rate";
    public const string LineItemCode = "line_item_code";
    public const string LineNumber = "line_number";

    public static readonly IReadOnlyList<string> All =
    [
        DocumentNumber, IssueDate, DueDate, SenderName, SenderVatId, ReceiverName, Currency,
        TotalNet, TotalTax, TotalGross, Iban, PaymentReference,
        ReceiverVatId, BuyerReference, OrderReference, DeliveryDate, SenderCountry, ReceiverCountry,
        SenderEndpointId, ReceiverEndpointId,
        LineDescription, LineQuantity, LineUnit, LineUnitPrice, LineAmount, LineTaxRate, LineItemCode, LineNumber,
    ];

    /// <summary>Plain names (lower-case letters and digits only) that a column or output leaf is commonly called, per meaning. Data for auto-binding.</summary>
    public static readonly IReadOnlyDictionary<string, string[]> Aliases = new Dictionary<string, string[]>
    {
        [DocumentNumber] = ["documentnumber", "invoicenumber", "invoiceno", "ordernumber", "ponumber", "docnumber", "rechnungsnummer", "arvenumber"],
        [IssueDate] = ["issuedate", "invoicedate", "orderdate", "documentdate", "rechnungsdatum", "kuupaev"],
        [DueDate] = ["duedate", "paymentdue", "payby", "faelligkeitsdatum", "maksetahtaeg"],
        [SenderName] = ["sendername", "sellername", "suppliername", "vendorname", "seller", "lieferant"],
        [SenderVatId] = ["sendervatid", "sellervatid", "suppliervatid", "vendorvatid", "ustidnr"],
        [ReceiverName] = ["receivername", "buyername", "customername", "customer", "buyer", "kaeufer", "ostja"],
        [Currency] = ["currency", "currencycode", "waehrung", "valuuta"],
        [TotalNet] = ["totalnet", "netamount", "nettotal", "subtotal", "netto", "summakmita"],
        [TotalTax] = ["totaltax", "taxamount", "vatamount", "tax", "vat", "mwst", "kaibemaks"],
        [TotalGross] = ["totalgross", "grossamount", "grosstotal", "total", "totalamount", "amountdue", "brutto"],
        [Iban] = ["iban", "bankaccount", "payeriban"],
        [PaymentReference] = ["paymentreference", "paymentref", "remittanceinformation", "viitenumber"],
        [ReceiverVatId] = ["receivervatid", "buyervatid", "customervatid", "customervatnumber", "buyervat"],
        [BuyerReference] = ["buyerreference", "buyerref", "yourreference", "customerreference", "leitwegid"],
        [OrderReference] = ["orderreference", "orderref", "purchaseorder", "purchaseorderreference", "customerponumber"],
        [DeliveryDate] = ["deliverydate", "shipdate", "requesteddeliverydate", "lieferdatum", "tarnekuupaev"],
        [SenderCountry] = ["sendercountry", "sellercountry", "suppliercountry", "vendorcountry"],
        [ReceiverCountry] = ["receivercountry", "buyercountry", "customercountry", "shiptocountry"],
        [SenderEndpointId] = ["senderpeppolid", "sellerpeppolid", "senderendpointid", "sellerendpointid", "senderelectronicaddress", "sellerelectronicaddress"],
        [ReceiverEndpointId] = ["receiverpeppolid", "buyerpeppolid", "receiverendpointid", "buyerendpointid", "receiverelectronicaddress", "buyerelectronicaddress"],
        [LineDescription] = ["description", "itemdescription", "linedescription", "productname", "item", "artikelbezeichnung", "nimetus"],
        [LineQuantity] = ["quantity", "qty", "amountordered", "menge", "kogus"],
        [LineUnit] = ["unit", "uom", "unitofmeasure", "einheit", "uhik"],
        [LineUnitPrice] = ["unitprice", "price", "priceperunit", "netprice", "einzelpreis", "uhikuhind"],
        [LineAmount] = ["lineamount", "linetotal", "amount", "extendedprice", "gesamtpreis", "summa"],
        [LineTaxRate] = ["taxrate", "vatrate", "vatpercent", "taxpercent", "mwstsatz"],
        [LineItemCode] = ["itemcode", "sku", "partnumber", "articlenumber", "productcode", "artikelnummer", "tootekood"],
        [LineNumber] = ["linenumber", "lineno", "position", "pos", "positionsnummer"],
    };

    /// <summary>
    /// What a read field means. Once the flow lists fields its meanings decide (a listed key's, null included;
    /// unlisted extras mean nothing); before that the read's own meaning stands.
    /// </summary>
    public static string? Of(ReadField field, FlowFieldList flow) =>
        flow.Fields.Count == 0 ? field.Meaning : flow.Fields.FirstOrDefault(x => x.Key == field.Key)?.Meaning;

    /// <summary>What a read column means: the flow's once it lists tables, before that the read's own.</summary>
    public static string? Of(ReadTable table, ReadColumn column, FlowFieldList flow) =>
        flow.Tables.Count == 0
            ? column.Meaning
            : flow.Tables.FirstOrDefault(x => x.Key == table.Key)?.Columns.FirstOrDefault(x => x.Key == column.Key)?.Meaning;
}
