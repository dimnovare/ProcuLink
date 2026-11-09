using ProcuLink.Core.Services.Mapping;

namespace ProcuLink.Transform.Documents;

public sealed record OutputLeaf(string Path, OutputNode Node, bool InTable);

public static class OutputLeafPaths
{
    /// <summary>Field and Attribute nodes depth-first; the path is the node names below the root joined by "/".</summary>
    public static IReadOnlyList<OutputLeaf> Of(OutputNodeTemplate t)
    {
        var leaves = new List<OutputLeaf>();
        Walk(t.Root, "", t.Root.NodeType == OutputNodeType.Array, leaves);
        return leaves;
    }

    private static void Walk(OutputNode parent, string prefix, bool inTable, List<OutputLeaf> leaves)
    {
        foreach (var c in parent.Children)
        {
            var path = prefix.Length == 0 ? c.Name : $"{prefix}/{c.Name}";
            if (c.NodeType is OutputNodeType.Field or OutputNodeType.Attribute) leaves.Add(new(path, c, inTable));
            else Walk(c, path, inTable || c.NodeType == OutputNodeType.Array, leaves);
        }
    }
}
