// ProcuLink.Api/Contracts/DashboardTopologyDto.cs
namespace ProcuLink.Api.Contracts;

public record TopologyBuyerDto(string Id, string Name, string Code, string Volume);

public record TopologySupplierDto(string Id, string Name, string Code, string Volume, int Health);

public record TopologyWireDto(
    string   BuyerId,
    string   SupplierId,
    int      Weight,
    string   Health,   // "ok" | "risk" | "down"
    int?     Alert
);

public record DashboardTopologyDto(
    IReadOnlyList<TopologyBuyerDto>    Buyers,
    IReadOnlyList<TopologySupplierDto> Suppliers,
    IReadOnlyList<TopologyWireDto>     Wires
);
