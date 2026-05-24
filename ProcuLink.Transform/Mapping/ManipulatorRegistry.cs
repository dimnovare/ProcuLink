using ProcuLink.Transform.Mapping.Manipulators;

namespace ProcuLink.Transform.Mapping;

public static class ManipulatorRegistry
{
    public static IFieldManipulator Resolve(string type, IReadOnlyList<string> @params)
        => type switch
        {
            "Replace"    => new ReplaceManipulator(@params),
            "Trim"       => new TrimManipulator(@params),
            "DateFormat" => new DateFormatManipulator(@params),
            "Concat"     => new ConcatManipulator(@params),
            "Fallback"   => new FallbackManipulator(@params),
            "Split"      => new SplitManipulator(@params),
            "Multiply"   => new MultiplyManipulator(@params),
            "Divide"     => new DivideManipulator(@params),
            _            => throw new InvalidOperationException($"Unknown manipulator type: {type}")
        };
}
