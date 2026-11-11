namespace ProcuLink.Core.Services;

/// <summary>Whether an org has stopped creating new legacy purchase orders (it is on the document engine).</summary>
public interface IOrderIntakeSwitch
{
    bool IsRetired(Guid orgId);
}
