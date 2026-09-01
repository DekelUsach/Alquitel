using System.Text.Json;

namespace Alquitel.Core.Privacy;

public sealed record AiTechnicalNote(int Index, string Text);

public static class AiTechnicalNoteValidator
{
    public static bool TryParse(
        string? json,
        IReadOnlySet<int> allowedIndexes,
        out IReadOnlyList<AiTechnicalNote> notes)
    {
        notes = Array.Empty<AiTechnicalNote>();
        if (string.IsNullOrWhiteSpace(json) || json.Length > 32_000 || allowedIndexes.Count > 200)
            return false;

        try
        {
            using var document = JsonDocument.Parse(
                json, new JsonDocumentOptions { MaxDepth = 8 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                root.EnumerateObject().Any(p => p.Name != "notas") ||
                !root.TryGetProperty("notas", out var array) ||
                array.ValueKind != JsonValueKind.Array ||
                array.GetArrayLength() > allowedIndexes.Count)
                return false;

            var parsed = new List<AiTechnicalNote>();
            var seen = new HashSet<int>();
            foreach (var element in array.EnumerateArray())
            {
                if (element.ValueKind != JsonValueKind.Object ||
                    element.EnumerateObject().Any(p => p.Name is not "idx" and not "nota") ||
                    !element.TryGetProperty("idx", out var indexElement) ||
                    !indexElement.TryGetInt32(out var index) ||
                    !allowedIndexes.Contains(index) || !seen.Add(index) ||
                    !element.TryGetProperty("nota", out var noteElement) ||
                    noteElement.ValueKind != JsonValueKind.String)
                    return false;

                var text = noteElement.GetString()?.Trim();
                if (string.IsNullOrWhiteSpace(text) || text.Length > 120 ||
                    text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length > 15 ||
                    text.Any(char.IsControl))
                    return false;
                parsed.Add(new AiTechnicalNote(index, text));
            }

            notes = parsed;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
