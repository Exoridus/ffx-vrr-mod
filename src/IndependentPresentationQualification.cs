using System.Text.Json;

namespace Fahrenheit.Mods.Vrr;

internal sealed record IndependentPresentationQualification(string Reason, int ContractCount = 0, int BlockerCount = 0)
{
    internal bool Enabled => false;

    internal static IndependentPresentationQualification ReadEmbedded()
    {
        using var stream = typeof(IndependentPresentationQualification).Assembly.GetManifestResourceStream(
            "Fahrenheit.Mods.Vrr.IndependentPresentationAdmission.json");
        return Evaluate(stream);
    }

    internal static IndependentPresentationQualification Evaluate(Stream? stream)
    {
        if (stream is null) return new("Qualification resource is missing; independent presentation is vetoed.");
        try
        {
            using var document = JsonDocument.Parse(stream);
            var root = document.RootElement;
            if (!HasFields(root, ["schema_version", "policy", "supported_build", "canonical_contract_snapshot_sha256", "required_contracts", "blockers", "source_inputs", "generator_inputs"])
                || root.GetProperty("schema_version").GetString() != "1.0"
                || root.GetProperty("policy").GetString() != "independent_presentation_veto_only"
                || root.GetProperty("supported_build").GetString() != "Steam 25501027"
                || !IsHash(root.GetProperty("canonical_contract_snapshot_sha256"))
                || !UniqueStrings(root.GetProperty("required_contracts"), out int contracts) || contracts == 0
                || !UniqueStrings(root.GetProperty("blockers"), out int blockers) || blockers == 0
                || !ValidInputs(root.GetProperty("source_inputs")) || !ValidInputs(root.GetProperty("generator_inputs")))
                return new("Qualification resource is invalid or unsupported; independent presentation is vetoed.");
            return new($"Independent presentation is vetoed: {contracts} unqualified contracts, {blockers} blockers; no live capability qualification.", contracts, blockers);
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or FormatException)
        {
            return new("Qualification resource is malformed; independent presentation is vetoed.");
        }
    }

    private static bool HasFields(JsonElement element, string[] names)
    {
        if (element.ValueKind != JsonValueKind.Object) return false;
        var fields = element.EnumerateObject().Select(p => p.Name).ToArray();
        return fields.Length == names.Length && fields.Distinct(StringComparer.Ordinal).Count() == names.Length
            && names.All(n => fields.Contains(n, StringComparer.Ordinal));
    }

    private static bool IsHash(JsonElement element) => element.ValueKind == JsonValueKind.String
        && element.GetString() is { Length: 64 } hash && hash.All(Uri.IsHexDigit);

    private static bool UniqueStrings(JsonElement element, out int count)
    {
        count = 0;
        if (element.ValueKind != JsonValueKind.Array) return false;
        var values = new HashSet<string>(StringComparer.Ordinal);
        foreach (var value in element.EnumerateArray())
            if (value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString()) || !values.Add(value.GetString()!)) return false;
        count = values.Count;
        return true;
    }

    private static bool ValidInputs(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Array || element.GetArrayLength() == 0) return false;
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var input in element.EnumerateArray())
            if (!HasFields(input, ["path", "sha256"]) || !IsHash(input.GetProperty("sha256"))
                || input.GetProperty("path").ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(input.GetProperty("path").GetString())
                || !paths.Add(input.GetProperty("path").GetString()!)) return false;
        return true;
    }
}
