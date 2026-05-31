// ProcuLink.Api/Contracts/DashboardTopologyDto.cs
namespace ProcuLink.Api.Contracts;

public record TopologyBuyerDto(
    string Id,
    string Name,
    string Code,
    /// <summary>Pre-formatted display label, e.g. "12 ord" or "410/wk". Not a numeric aggregate.</summary>
    string Volume
);

public record TopologySupplierDto(
    string Id,
    string Name,
    string Code,
    /// <summary>Pre-formatted display label, e.g. "12 ord" or "410/wk". Not a numeric aggregate.</summary>
    string Volume,
    /// <summary>Success-rate percentage 0–100: 100*(total-failed)/total.</summary>
    int Health
);

public record TopologyWireDto(
    string   BuyerId,
    string   SupplierId,
    int      Weight,
    /// <summary>Categorical health: "ok" | "risk" | "down".</summary>
    string   Health,
    int?     Alert
);

public record DashboardTopologyDto(
    IReadOnlyList<TopologyBuyerDto>    Buyers,
    IReadOnlyList<TopologySupplierDto> Suppliers,
    IReadOnlyList<TopologyWireDto>     Wires
);
