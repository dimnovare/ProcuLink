namespace ProcuLink.Core.Documents;

/// <summary>Turns an OCR text line and its pixel rectangle into word boxes (0..1, origin top-left).</summary>
public static class OcrWords
{
    public static IReadOnlyList<PageWord> FromLine(
        string text, float left, float top, float right, float bottom, int imageWidth, int imageHeight, int page)
    {
        var words = new List<PageWord>();
        if (string.IsNullOrWhiteSpace(text) || imageWidth <= 0 || imageHeight <= 0) return words;

        var len = text.Length;
        var y = Clamp(top / imageHeight);
        var h = Math.Min(Clamp((bottom - top) / imageHeight), 1 - y);
        for (int i = 0; i < len;)
        {
            if (char.IsWhiteSpace(text[i])) { i++; continue; }
            var start = i;
            while (i < len && !char.IsWhiteSpace(text[i])) i++;
            var x0 = Clamp((left + (right - left) * start / len) / imageWidth);
            var x1 = Clamp((left + (right - left) * i / len) / imageWidth);
            words.Add(new PageWord(text[start..i], page, new Box(x0, y, x1 - x0, h)));
        }
        return words;
    }

    private static double Clamp(double v) => Math.Clamp(v, 0, 1);
}
