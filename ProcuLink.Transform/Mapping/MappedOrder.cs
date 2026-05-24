namespace ProcuLink.Transform.Mapping;

public record MappedOrder
{
    public string? PoNumber { get; init; }
    public string? OrderDate { get; init; }
    public string? BuyerName { get; init; }
    public string? Currency { get; init; }
    public List<MappedOrderLine> Lines { get; init; } = new();
}

public record MappedOrderLine
{
    public string? LineNumber { get; init; }
    public string? BuyerItemCode { get; init; }
    public string? Description { get; init; }
    public string? Quantity { get; init; }
    public string? Unit { get; init; }
    public string? UnitPrice { get; init; }
}
