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
    public const string SenderStreet = "sender_street";
    public const string SenderCity = "sender_city";
    public const string SenderPostcode = "sender_postcode";
    public const string ReceiverStreet = "receiver_street";
    public const string ReceiverCity = "receiver_city";
    public const string ReceiverPostcode = "receiver_postcode";
    public const string SenderContactName = "sender_contact_name";
    public const string SenderContactPhone = "sender_contact_phone";
    public const string SenderContactEmail = "sender_contact_email";
    public const string DeliveryName = "delivery_name";
    public const string DeliveryStreet = "delivery_street";
    public const string DeliveryCity = "delivery_city";
    public const string DeliveryPostcode = "delivery_postcode";
    public const string DeliveryCountry = "delivery_country";
    public const string PaymentTerms = "payment_terms";

    public const string LineDescription = "line_description";
    public const string LineQuantity = "line_quantity";
    public const string LineUnit = "line_unit";
    public const string LineUnitPrice = "line_unit_price";
    public const string LineAmount = "line_amount";
    public const string LineTaxRate = "line_tax_rate";
    public const string LineItemCode = "line_item_code";
    public const string LineBuyerItemCode = "line_buyer_item_code";
    public const string LineNumber = "line_number";

    public static readonly IReadOnlyList<string> All =
    [
        DocumentNumber, IssueDate, DueDate, SenderName, SenderVatId, ReceiverName, Currency,
        TotalNet, TotalTax, TotalGross, Iban, PaymentReference,
        ReceiverVatId, BuyerReference, OrderReference, DeliveryDate, SenderCountry, ReceiverCountry,
        SenderEndpointId, ReceiverEndpointId,
        SenderStreet, SenderCity, SenderPostcode, ReceiverStreet, ReceiverCity, ReceiverPostcode,
        SenderContactName, SenderContactPhone, SenderContactEmail,
        DeliveryName, DeliveryStreet, DeliveryCity, DeliveryPostcode, DeliveryCountry, PaymentTerms,
        LineDescription, LineQuantity, LineUnit, LineUnitPrice, LineAmount, LineTaxRate, LineItemCode, LineBuyerItemCode, LineNumber,
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
        [ReceiverCountry] = ["receivercountry", "buyercountry", "customercountry"],
        [SenderEndpointId] = ["senderpeppolid", "sellerpeppolid", "senderendpointid", "sellerendpointid", "senderelectronicaddress", "sellerelectronicaddress"],
        [ReceiverEndpointId] = ["receiverpeppolid", "buyerpeppolid", "receiverendpointid", "buyerendpointid", "receiverelectronicaddress", "buyerelectronicaddress"],
        [SenderStreet] = ["senderstreet", "sellerstreet", "supplierstreet", "vendorstreet", "senderaddress", "selleraddress"],
        [SenderCity] = ["sendercity", "sellercity", "suppliercity", "vendorcity"],
        [SenderPostcode] = ["senderpostcode", "sellerpostcode", "supplierpostcode", "senderzip", "sellerzip", "senderpostalcode", "sellerpostalcode"],
        [ReceiverStreet] = ["receiverstreet", "buyerstreet", "customerstreet", "receiveraddress", "buyeraddress"],
        [ReceiverCity] = ["receivercity", "buyercity", "customercity"],
        [ReceiverPostcode] = ["receiverpostcode", "buyerpostcode", "customerpostcode", "receiverzip", "buyerzip", "receiverpostalcode", "buyerpostalcode"],
        [SenderContactName] = ["sendercontactname", "sellercontactname", "suppliercontactname", "sendercontact", "sellercontact"],
        [SenderContactPhone] = ["sendercontactphone", "sellercontactphone", "suppliercontactphone", "sendertelephone", "sellertelephone"],
        [SenderContactEmail] = ["sendercontactemail", "sellercontactemail", "suppliercontactemail", "senderemail", "selleremail"],
        [DeliveryName] = ["deliveryname", "shiptoname", "shipto", "consignee", "lieferanschrift"],
        [DeliveryStreet] = ["deliverystreet", "deliveryaddress", "shiptostreet", "shiptoaddress", "lieferstrasse"],
        [DeliveryCity] = ["deliverycity", "shiptocity", "consigneecity", "lieferort"],
        [DeliveryPostcode] = ["deliverypostcode", "deliveryzip", "deliverypostalcode", "shiptopostcode", "shiptozip"],
        [DeliveryCountry] = ["deliverycountry", "shiptocountry", "consigneecountry", "lieferland"],
        [PaymentTerms] = ["paymentterms", "termsofpayment", "paymentconditions", "zahlungsbedingungen", "maksetingimused"],
        [LineDescription] = ["description", "itemdescription", "linedescription", "productname", "item", "artikelbezeichnung", "nimetus"],
        [LineQuantity] = ["quantity", "qty", "amountordered", "menge", "kogus"],
        [LineUnit] = ["unit", "uom", "unitofmeasure", "einheit", "uhik"],
        [LineUnitPrice] = ["unitprice", "price", "priceperunit", "netprice", "einzelpreis", "uhikuhind"],
        [LineAmount] = ["lineamount", "linetotal", "amount", "extendedprice", "gesamtpreis", "summa"],
        [LineTaxRate] = ["taxrate", "vatrate", "vatpercent", "taxpercent", "mwstsatz"],
        [LineItemCode] = ["itemcode", "sku", "partnumber", "articlenumber", "productcode", "artikelnummer", "tootekood"],
        [LineBuyerItemCode] = ["buyeritemcode", "buyerpartnumber", "customeritemcode", "customerpartnumber", "customerarticlenumber"],
        [LineNumber] = ["linenumber", "lineno", "position", "pos", "positionsnummer"],
    };

    /// <summary>What each meaning is, in one sentence. The reader's prompt lists these.</summary>
    public static readonly IReadOnlyDictionary<string, string> Definitions = new Dictionary<string, string>
    {
        [DocumentNumber] = "the document's own number: the order number on an order, the invoice number on an invoice. Never the number of another document it refers to.",
        [IssueDate] = "the date the document was issued: the order date on an order, the invoice date on an invoice.",
        [DueDate] = "the date payment is due.",
        [SenderName] = "the party that issued the document: the buyer on an order, the seller on an invoice. Often named only in the letterhead, the logo or the footer, next to its bank details and registration or VAT number.",
        [SenderVatId] = "the sender's VAT or tax registration number.",
        [ReceiverName] = "the party the document is addressed to: the supplier on an order, the customer on an invoice. Not the party at a delivery or ship-to address, unless no other party is named.",
        [Currency] = "the currency of the amounts, as printed (code or symbol), also when it is printed only next to the amounts. Name it for the currency in the document's language (e.g. 'Valuuta', 'Currency'), never with the label of the amount it is printed next to.",
        [TotalNet] = "the document's total before tax, including charges such as shipping, handling or fees: the amount the tax is calculated on.",
        [TotalTax] = "the document's total tax.",
        [TotalGross] = "the document's total including tax: the amount to pay.",
        [Iban] = "the bank account (IBAN) to pay into.",
        [PaymentReference] = "the reference to quote when paying.",
        [ReceiverVatId] = "the receiver's VAT or tax registration number.",
        [BuyerReference] = "a reference the buyer asks to be quoted, such as 'your reference', a cost centre or a routing id.",
        [OrderReference] = "the number of the order this document refers to, when the document is not that order itself.",
        [DeliveryDate] = "the requested or actual delivery date.",
        [SenderCountry] = "the country of the sender's address.",
        [ReceiverCountry] = "the country of the receiver's address.",
        [SenderEndpointId] = "a Peppol participant id of the sender, scheme and id, such as 0088:0000000000000. Only in that shape.",
        [ReceiverEndpointId] = "a Peppol participant id of the receiver, scheme and id. Only in that shape.",
        [SenderStreet] = "the street line of the sender's address.",
        [SenderCity] = "the city of the sender's address.",
        [SenderPostcode] = "the postcode of the sender's address.",
        [ReceiverStreet] = "the street line of the receiver's address.",
        [ReceiverCity] = "the city of the receiver's address.",
        [ReceiverPostcode] = "the postcode of the receiver's address.",
        [SenderContactName] = "the sender's contact person.",
        [SenderContactPhone] = "the sender's contact phone number.",
        [SenderContactEmail] = "the sender's contact email address.",
        [DeliveryName] = "the name at the delivery or ship-to address.",
        [DeliveryStreet] = "the street line of the delivery or ship-to address.",
        [DeliveryCity] = "the city of the delivery or ship-to address.",
        [DeliveryPostcode] = "the postcode of the delivery or ship-to address.",
        [DeliveryCountry] = "the country of the delivery or ship-to address.",
        [PaymentTerms] = "the payment terms as printed, such as '14 days net'.",
        [LineDescription] = "a line's full description. A description continues on the printed lines below it until the next line starts: join the pieces.",
        [LineQuantity] = "a line's quantity: one number, without its unit. When several quantities are printed (ordered, delivered, back-ordered), the one the line amount is charged for.",
        [LineUnit] = "a line's unit of measure.",
        [LineUnitPrice] = "a line's price per unit: one number, without a unit or price basis printed next to it. When a list price and a discounted price are both printed, the price the line amount is charged at.",
        [LineAmount] = "a line's total amount: one number.",
        [LineTaxRate] = "a line's tax rate.",
        [LineItemCode] = "a line's item, article or part number.",
        [LineBuyerItemCode] = "a line's item code in the buyer's own numbering, such as 'our item no.' on an order or 'your item no.' on an invoice. Not the supplier's article number.",
        [LineNumber] = "a line's position number.",
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
