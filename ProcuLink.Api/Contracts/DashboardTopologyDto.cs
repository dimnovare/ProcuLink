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
    /// <summary>
    /// Delivery success rate as a percentage 0–100, over the last 30 days:
    /// <c>100 * delivered / (delivered + failed)</c>.
    /// <para><b>Null when no order in the window has reached a known outcome</b> — the supplier has
    /// orders, but every one of them is still in flight, parked, or held, so there is no rate to
    /// report. It was previously a non-nullable int that answered 100 in that case, which is how a
    /// supplier whose orders were ALL parked in <c>delivery_unconfirmed</c> after a crash rendered a
    /// green 100% delivery success rate. This slot holds a measurement or nothing.</para>
    /// </summary>
    int? Health
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
