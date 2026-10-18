using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using Yojana.Serialisation.Contracts;

namespace Yojana.Serialisation.Certificates;

/// <summary>
/// Language-sensitive presentation is behind one replaceable catalogue so tenant overrides can be introduced without
/// teaching the template parser about tenant settings.
/// </summary>
public interface ICertificateTemplateLanguageCatalogue
{
    string FormatDate(string language, DateTimeOffset value, string datePattern);
    string FormatDateTime(string language, DateTimeOffset value, string datePattern, string timePattern,
        string zoneAbbreviation);
    string FormatMoney(string language, decimal value, string currency);
    string Label(string language, string value);
}

public sealed class BuiltInCertificateTemplateLanguageCatalogue : ICertificateTemplateLanguageCatalogue
{
    private static readonly CultureInfo English = CultureInfo.GetCultureInfo("en-GB");
    private static readonly Dictionary<string, string> EnglishLabels = new(StringComparer.OrdinalIgnoreCase)
    {
        [IdentifierType.InternalSerial] = "Serial number",
        [IdentifierType.ManufacturerSerial] = "Manufacturer serial number",
        [IdentifierType.VendorSerial] = "Vendor serial number",
        [IdentifierType.CustomerSerial] = "Customer serial number",
        [IdentifierType.CustomerAssetTag] = "Customer asset tag",
        [IdentifierType.ChannelOrderRef] = "Channel order reference",
        [IdentifierType.SoftwareLicense] = "Software licence",
        [IdentifierType.Imei] = "IMEI",
        [IdentifierType.Mac] = "MAC address",
        [IdentifierType.UdiDi] = "UDI-DI",
        [IdentifierType.UdiPi] = "UDI-PI",
        [IdentifierType.Uii] = "UII",
        [IdentifierType.Vin] = "VIN",
        [IdentifierType.Sgtin] = "SGTIN",
        [IdentifierType.Giai] = "GIAI",
        [CoverageFamilyCode.Base] = "Base warranty",
        [CoverageFamilyCode.Supplier] = "Supplier warranty",
        ["service_level"] = "Service level",
        ["accidental_damage"] = "Accidental damage",
        ["extended_term"] = "Extended term",
        ["battery_retention"] = "Battery retention",
        [EntitlementType.Manufacturer] = "Manufacturer warranty",
        [EntitlementType.Dealer] = "Dealer warranty",
        [EntitlementType.Extended] = "Extended warranty",
        [EntitlementType.Contract] = "Service contract",
        [EntitlementType.Statutory] = "Statutory cover",
        [EntitlementType.RecallCampaign] = "Recall campaign",
        [EntitlementType.Goodwill] = "Goodwill cover",
        [StartTrigger.Receipt] = "Receipt date",
        [StartTrigger.Ship] = "Shipment date",
        [StartTrigger.Deliver] = "Delivery date",
        [StartTrigger.Install] = "Installation date",
        [StartTrigger.Register] = "Registration date",
        [StartTrigger.Invoice] = "Invoice date",
        [StartTrigger.ConsumptionFromConsignment] = "Consignment use date",
        [StartTrigger.FirstExternalSale] = "First sale date",
        [StartTrigger.PlanPurchase] = "Plan purchase date",
        [StartTrigger.BaseExpiry] = "Base warranty expiry",
        ["return_to_base"] = "Return to base",
        [ConditionGrades.New] = "New",
        [ConditionGrades.OpenBox] = "Open box",
        [ConditionGrades.RefurbishedA] = "Refurbished — grade A",
        [ConditionGrades.RefurbishedB] = "Refurbished — grade B",
        [ConditionGrades.RefurbishedC] = "Refurbished — grade C",
        [ConditionGrades.Used] = "Used",
        [ConditionGrades.Remanufactured] = "Remanufactured",
        [ConditionGrades.Defective] = "Defective",
        [ConditionGrades.Scrap] = "Scrap",
        [ReleaseStatus.Repaired] = "Repaired",
        [ReleaseStatus.Overhauled] = "Overhauled",
        [ReleaseStatus.RebuiltOemOnly] = "Rebuilt by the original manufacturer",
        [RequiredSerializedComponentSnapshot.Captured] = "Captured",
        [RequiredSerializedComponentSnapshot.Missing] = "Missing",
        [RequiredSerializedComponentSnapshot.UnexpectedItem] = "Unexpected item",
        [CertificateSupersedeReason.Repair] = "Repair",
        [CertificateSupersedeReason.Refurbishment] = "Refurbishment",
        [CertificateSupersedeReason.ComponentReplacement] = "Component replacement",
        [CertificateSupersedeReason.Correction] = "Correction",
        [CertificateSupersedeReason.Reassembly] = "Reassembly",
        [CertificateSupersedeReason.IdentifierChange] = "Identifier change",
        [CertificateSupersedeReason.EntitlementChange] = "Warranty change",
        [PartyType.Customer] = "Customer",
        [PartyType.Us] = "Issuing company",
        ["built_in_house"] = "Built by the issuer",
        ["purchased"] = "Purchased unit",
        ["pass"] = "Pass",
        ["fail"] = "Fail",
    };

    public string FormatDate(string language, DateTimeOffset value, string datePattern) =>
        value.DateTime.ToString(datePattern, CertificateDateFormatSettings.Culture(language));

    public string FormatDateTime(string language, DateTimeOffset value, string datePattern, string timePattern,
        string zoneAbbreviation) =>
        $"{value.DateTime.ToString($"{datePattern}, {timePattern}", CertificateDateFormatSettings.Culture(language))} {zoneAbbreviation}";

    public string FormatMoney(string language, decimal value, string currency)
    {
        var culture = IsEnglish(language) ? English : CultureInfo.InvariantCulture;
        return $"{currency.ToUpperInvariant()} {value.ToString("N2", culture)}";
    }

    public string Label(string language, string value)
    {
        if (IsEnglish(language) && EnglishLabels.TryGetValue(value, out var label)) return label;
        var words = value.Trim().Replace('-', '_').Split('_', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0) return value;
        return string.Join(' ', words.Select((word, index) => index == 0
            ? char.ToUpperInvariant(word[0]) + word[1..].ToLowerInvariant()
            : word.ToLowerInvariant()));
    }

    private static bool IsEnglish(string language) => language.Equals("en", StringComparison.OrdinalIgnoreCase)
        || language.StartsWith("en-", StringComparison.OrdinalIgnoreCase);
}

public sealed record CertificateTemplateAnalysis(IReadOnlyList<string> Fields,
    IReadOnlyList<string> UnknownHelpers, IReadOnlyList<string> Errors);

public sealed class CertificateTemplateValidator(CertificateTemplateEngine engine)
{
    public CertificateTemplateValidation Validate(string documentKind, IReadOnlyCollection<string> catalogue,
        string layoutBody, string? headerBody, string? footerBody, IReadOnlyList<string> declaredFields)
    {
        var allowed = catalogue.ToHashSet(StringComparer.Ordinal);
        var analysis = engine.Analyse(string.Join('\n', new[] { layoutBody, headerBody, footerBody }
            .Where(value => value is not null)!));
        var used = analysis.Fields;
        var all = used.Concat(declaredFields).Distinct(StringComparer.Ordinal).ToList();
        var unknown = all.Where(path => !allowed.Contains(path)).Order().ToList();
        var party = documentKind == CertificateDocumentKind.ReleaseRecord
            ? all.Where(IsPartyPath).Order().ToList() : [];
        var unused = declaredFields.Where(path => !used.Contains(path, StringComparer.Ordinal)).Distinct().Order().ToList();
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(layoutBody)) errors.Add("Layout body is required.");
        if (unknown.Count > 0) errors.Add("The layout contains placeholders outside the fixed payload catalogue.");
        if (party.Count > 0) errors.Add("Release-record layouts cannot bind party fields.");
        foreach (var helper in analysis.UnknownHelpers) errors.Add($"Unknown template helper '{helper}'.");
        errors.AddRange(analysis.Errors);
        return new CertificateTemplateValidation(errors.Count == 0, unknown, party, unused,
            analysis.UnknownHelpers, errors);
    }

    private static bool IsPartyPath(string path) => !path.StartsWith("issuer.", StringComparison.Ordinal)
        && path.Split('.').Any(part => part.Contains("party", StringComparison.OrdinalIgnoreCase)
            || part.Contains("owner", StringComparison.OrdinalIgnoreCase) || part.Contains("customer", StringComparison.OrdinalIgnoreCase)
            || part.Contains("address", StringComparison.OrdinalIgnoreCase));
}

/// <summary>A small, inert template language shared by HTML preview and PDF composition.</summary>
public sealed class CertificateTemplateEngine(ICertificateTemplateLanguageCatalogue languages)
{
    private static readonly HashSet<string> Helpers = new(["date", "datetime", "label", "money"], StringComparer.Ordinal);
    private static readonly HashSet<string> Blocks = new(["each", "if", "unless"], StringComparer.Ordinal);

    public CertificateTemplateAnalysis Analyse(string? template)
    {
        var parsed = Parse(template ?? "");
        var fields = new HashSet<string>(StringComparer.Ordinal);
        var unknown = new HashSet<string>(StringComparer.Ordinal);
        Analyse(parsed.Nodes, null, fields, unknown);
        return new CertificateTemplateAnalysis(fields.Order().ToList(), unknown.Order().ToList(), parsed.Errors);
    }

    public string Render(string? template, JsonElement payload, CertificateRenderRequest request, string language)
    {
        var parsed = Parse(template ?? "");
        if (parsed.Errors.Count > 0) throw new InvalidOperationException(string.Join(' ', parsed.Errors));
        var presentation = Presentation(payload, language);
        return Render(parsed.Nodes, new RenderScope(payload, payload), request, language,
            ResolveTimeZone(presentation.TimeZone), presentation.DatePattern, presentation.TimePattern);
    }

    public string FormatDateTime(string language, DateTime value, JsonElement payload)
    {
        var presentation = Presentation(payload, language);
        return FormatDateTime(language, new DateTimeOffset(Utc(value), TimeSpan.Zero),
            ResolveTimeZone(presentation.TimeZone), presentation.DatePattern, presentation.TimePattern);
    }

    private string Render(IReadOnlyList<Node> nodes, RenderScope scope, CertificateRenderRequest request,
        string language, TimeZoneInfo timeZone, string datePattern, string timePattern)
    {
        var output = new StringBuilder();
        foreach (var node in nodes)
        {
            switch (node)
            {
                case TextNode text:
                    output.Append(text.Value);
                    break;
                case ValueNode value:
                    output.Append(Encode(Value(scope, request, value.Path)));
                    break;
                case HelperNode helper:
                    output.Append(Encode(Format(helper, scope, request, language, timeZone, datePattern, timePattern)));
                    break;
                case BlockNode block:
                    var resolved = IsRawReferencePath(block.Path) ? null : Resolve(scope, request, block.Path);
                    if (block.Name == "each")
                    {
                        if (resolved is { ValueKind: JsonValueKind.Array } array && array.GetArrayLength() > 0)
                            foreach (var item in array.EnumerateArray())
                                output.Append(Render(block.Body, scope with { Current = item }, request, language,
                                    timeZone, datePattern, timePattern));
                        else output.Append(Render(block.ElseBody, scope, request, language, timeZone, datePattern, timePattern));
                    }
                    else
                    {
                        var show = Truthy(resolved);
                        if (block.Name == "unless") show = !show;
                        output.Append(Render(show ? block.Body : block.ElseBody, scope, request, language, timeZone,
                            datePattern, timePattern));
                    }
                    break;
            }
        }
        return output.ToString();
    }

    private string Format(HelperNode helper, RenderScope scope, CertificateRenderRequest request, string language,
        TimeZoneInfo timeZone, string datePattern, string timePattern)
    {
        if (!Helpers.Contains(helper.Name) || helper.Arguments.Count == 0) return "";
        var value = Value(scope, request, helper.Arguments[0]);
        if (string.IsNullOrWhiteSpace(value)) return "";
        return helper.Name switch
        {
            "date" when Date(value) is { } date => languages.FormatDate(language, date, datePattern),
            "datetime" when Date(value) is { } date => FormatDateTime(language, date, timeZone, datePattern, timePattern),
            "label" => languages.Label(language, value),
            "money" when helper.Arguments.Count == 2 && decimal.TryParse(value, NumberStyles.Number,
                CultureInfo.InvariantCulture, out var amount) => languages.FormatMoney(language, amount,
                    Value(scope, request, helper.Arguments[1]) is { Length: > 0 } currency ? currency : helper.Arguments[1]),
            _ => "",
        };
    }

    private string FormatDateTime(string language, DateTimeOffset value, TimeZoneInfo timeZone, string datePattern,
        string timePattern)
    {
        var local = TimeZoneInfo.ConvertTime(value.ToUniversalTime(), timeZone);
        return languages.FormatDateTime(language, local, datePattern, timePattern, Abbreviation(timeZone, local));
    }

    private static CertificatePresentationSnapshot Presentation(JsonElement payload, string language)
    {
        var fallback = CertificateDateFormatSettings.DefaultFor(language);
        if (payload.ValueKind == JsonValueKind.Object
            && payload.TryGetProperty("presentation", out var presentation)
            && presentation.ValueKind == JsonValueKind.Object)
        {
            var timeZone = String(presentation, "time_zone") ?? "UTC";
            var datePattern = String(presentation, "date_pattern");
            var timePattern = String(presentation, "time_pattern");
            return new(timeZone,
                CertificateDatePattern.All.Contains(datePattern ?? "", StringComparer.Ordinal)
                    ? datePattern! : fallback.DatePattern,
                CertificateTimePattern.All.Contains(timePattern ?? "", StringComparer.Ordinal)
                    ? timePattern! : fallback.TimePattern);
        }
        return new("UTC", fallback.DatePattern, fallback.TimePattern);
    }

    private static string? String(JsonElement root, string property) =>
        root.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() : null;

    private static TimeZoneInfo ResolveTimeZone(string? timeZone) =>
        timeZone is not null && TimeZoneInfo.TryFindSystemTimeZoneById(timeZone, out var resolved)
            ? resolved : TimeZoneInfo.Utc;

    private static string Abbreviation(TimeZoneInfo timeZone, DateTimeOffset local)
    {
        if (timeZone.Equals(TimeZoneInfo.Utc) || timeZone.Id.Equals("UTC", StringComparison.OrdinalIgnoreCase))
            return "UTC";
        var name = timeZone.IsDaylightSavingTime(local) ? timeZone.DaylightName : timeZone.StandardName;
        if (name.Contains("Coordinated Universal", StringComparison.OrdinalIgnoreCase)) return "UTC";
        var letters = name.Split([' ', '-', '_'], StringSplitOptions.RemoveEmptyEntries)
            .Where(word => !word.Equals("of", StringComparison.OrdinalIgnoreCase))
            .Select(word => char.ToUpperInvariant(word[0])).ToArray();
        if (letters.Length is > 1 and <= 5) return new string(letters);
        var sign = local.Offset < TimeSpan.Zero ? "-" : "+";
        return $"UTC{sign}{local.Offset.Duration():hh\\:mm}";
    }

    private static DateTimeOffset? Date(string value)
    {
        if (DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var timestamp)) return timestamp;
        return DateOnly.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? new DateTimeOffset(date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc)) : null;
    }

    private static bool Truthy(JsonElement? value) => value?.ValueKind switch
    {
        JsonValueKind.True => true,
        JsonValueKind.False or JsonValueKind.Null or JsonValueKind.Undefined => false,
        JsonValueKind.String => !string.IsNullOrWhiteSpace(value.Value.GetString()),
        JsonValueKind.Array => value.Value.GetArrayLength() > 0,
        JsonValueKind.Number => !value.Value.TryGetDecimal(out var number) || number != 0,
        _ => value is not null,
    };

    private static string Value(RenderScope scope, CertificateRenderRequest request, string path)
    {
        // Opaque database references stay in the signed payload for provenance but are never printable template data.
        // Customer layouts use the adjacent display snapshot or document number instead.
        if (IsRawReferencePath(path)) return "";
        if (path.StartsWith("certificate.", StringComparison.Ordinal))
        {
            var metadata = request.Metadata;
            return path[12..] switch
            {
                "tracking_number" => metadata?.TrackingNumber ?? "",
                "version" => metadata?.Version.ToString(CultureInfo.InvariantCulture) ?? "",
                "issued_at" => metadata is null ? "" : Utc(metadata.IssuedAt).ToString("O", CultureInfo.InvariantCulture),
                "supersedes_tracking_number" => metadata?.SupersedesTrackingNumber ?? "",
                "supersede_reason" => metadata?.SupersedeReason ?? "",
                "payload_hash" => metadata?.PayloadHash ?? "",
                "verify_reference" => metadata?.VerifyReference ?? "",
                _ => "",
            };
        }
        if (path.StartsWith("signature.", StringComparison.Ordinal))
        {
            var signature = request.Signature;
            return path[10..] switch
            {
                "printed_name" => signature?.PrintedName ?? "",
                "meaning" => signature?.Meaning ?? "",
                "signed_at" => signature is null ? "" : Utc(signature.SignedAt).ToString("O", CultureInfo.InvariantCulture),
                "method" => signature?.Method ?? "",
                _ => "",
            };
        }
        return Resolve(scope, request, path)?.ToString() ?? "";
    }

    internal static bool IsRawReferencePath(string path)
    {
        var leaf = path.Split('.').LastOrDefault() ?? path;
        return leaf.EndsWith("_id", StringComparison.Ordinal)
            || leaf is "superseded_lineage" or "source_lots" or "lot_reference";
    }

    private static DateTime Utc(DateTime value) => value.Kind == DateTimeKind.Unspecified
        ? DateTime.SpecifyKind(value, DateTimeKind.Utc)
        : value.ToUniversalTime();

    private static JsonElement? Resolve(RenderScope scope, CertificateRenderRequest request, string path)
    {
        if (path.StartsWith("certificate.", StringComparison.Ordinal) || path.StartsWith("signature.", StringComparison.Ordinal))
        {
            using var value = JsonDocument.Parse(JsonSerializer.Serialize(Value(scope, request, path)));
            return value.RootElement.Clone();
        }
        return Read(scope.Current, path) ?? (scope.Current.ValueKind != scope.Root.ValueKind || scope.Current.GetRawText() != scope.Root.GetRawText()
            ? Read(scope.Root, path) : null);
    }

    private static JsonElement? Read(JsonElement root, string path)
    {
        if (path == "this") return root;
        var current = root;
        foreach (var part in path.Trim().Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(part, out current)) return null;
        }
        return current;
    }

    private static string Encode(string? value) => WebUtility.HtmlEncode(value ?? "");

    private static void Analyse(IReadOnlyList<Node> nodes, string? prefix, HashSet<string> fields,
        HashSet<string> unknown)
    {
        foreach (var node in nodes)
        {
            switch (node)
            {
                case ValueNode value:
                    fields.Add(Qualify(value.Path, prefix));
                    break;
                case HelperNode helper:
                    if (!Helpers.Contains(helper.Name)) unknown.Add(helper.Name);
                    if (helper.Arguments.Count > 0) fields.Add(Qualify(helper.Arguments[0], prefix));
                    if (helper.Name == "money" && helper.Arguments.Count > 1
                        && !IsCurrencyLiteral(helper.Arguments[1])) fields.Add(Qualify(helper.Arguments[1], prefix));
                    break;
                case BlockNode block:
                    if (!Blocks.Contains(block.Name)) unknown.Add(block.Name);
                    fields.Add(Qualify(block.Path, prefix));
                    Analyse(block.Body, block.Name == "each" ? Qualify(block.Path, prefix) : prefix, fields, unknown);
                    Analyse(block.ElseBody, prefix, fields, unknown);
                    break;
            }
        }
    }

    private static string Qualify(string path, string? prefix) => prefix is null
        || path.StartsWith("certificate.", StringComparison.Ordinal) || path.StartsWith("signature.", StringComparison.Ordinal)
        ? path : path == "this" ? prefix : prefix + "." + path;

    private static bool IsCurrencyLiteral(string value) => value.Length == 3 && value.All(char.IsAsciiLetterUpper);

    private static ParsedTemplate Parse(string text)
    {
        var errors = new List<string>();
        var tokens = Tokenise(text, errors);
        var index = 0;
        var nodes = ParseSequence(tokens, ref index, null, errors, out _);
        return new ParsedTemplate(nodes, errors);
    }

    private static List<Node> ParseSequence(IReadOnlyList<Token> tokens, ref int index, string? closing,
        List<string> errors, out bool metElse)
    {
        metElse = false;
        var nodes = new List<Node>();
        while (index < tokens.Count)
        {
            var token = tokens[index++];
            if (!token.Tag)
            {
                nodes.Add(new TextNode(token.Value));
                continue;
            }
            var value = token.Value.Trim();
            if (value == "else")
            {
                if (closing is null) errors.Add("Template contains {{else}} outside a block.");
                else metElse = true;
                return nodes;
            }
            if (value.StartsWith('/'))
            {
                var name = value[1..].Trim();
                if (closing is null) errors.Add($"Template closes '{name}' without opening it.");
                else if (!name.Equals(closing, StringComparison.Ordinal))
                    errors.Add($"Template closes '{name}' while '{closing}' is open.");
                return nodes;
            }
            if (value.StartsWith('#'))
            {
                var parts = Parts(value[1..]);
                var name = parts.FirstOrDefault() ?? "";
                var path = parts.Skip(1).FirstOrDefault() ?? "";
                if (name.Length == 0 || path.Length == 0)
                    errors.Add($"Template block '{value}' requires a helper name and path.");
                var body = ParseSequence(tokens, ref index, name, errors, out var hasElse);
                var alternative = hasElse ? ParseSequence(tokens, ref index, name, errors, out _) : [];
                nodes.Add(new BlockNode(name, path, body, alternative));
                continue;
            }
            var arguments = Parts(value);
            if (arguments.Count == 1) nodes.Add(new ValueNode(arguments[0]));
            else
            {
                var name = arguments[0];
                var helperArguments = arguments.Skip(1).ToList();
                if (name == "money" && helperArguments.Count != 2)
                    errors.Add("Template helper 'money' requires a value path and a currency path or code.");
                else if (name != "money" && helperArguments.Count != 1)
                    errors.Add($"Template helper '{name}' requires exactly one path.");
                nodes.Add(new HelperNode(name, helperArguments));
            }
        }
        if (closing is not null) errors.Add($"Template block '{closing}' is not closed.");
        return nodes;
    }

    private static List<Token> Tokenise(string text, List<string> errors)
    {
        var result = new List<Token>();
        var offset = 0;
        while (offset < text.Length)
        {
            var start = text.IndexOf("{{", offset, StringComparison.Ordinal);
            if (start < 0)
            {
                result.Add(new Token(false, text[offset..]));
                break;
            }
            if (start > offset) result.Add(new Token(false, text[offset..start]));
            var end = text.IndexOf("}}", start + 2, StringComparison.Ordinal);
            if (end < 0)
            {
                errors.Add("Template contains an unterminated '{{' token.");
                result.Add(new Token(false, text[start..]));
                break;
            }
            result.Add(new Token(true, text[(start + 2)..end]));
            offset = end + 2;
        }
        return result;
    }

    private static List<string> Parts(string value) => value.Split((char[]?)null,
        StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    private sealed record ParsedTemplate(IReadOnlyList<Node> Nodes, IReadOnlyList<string> Errors);
    private sealed record Token(bool Tag, string Value);
    private abstract record Node;
    private sealed record TextNode(string Value) : Node;
    private sealed record ValueNode(string Path) : Node;
    private sealed record HelperNode(string Name, IReadOnlyList<string> Arguments) : Node;
    private sealed record BlockNode(string Name, string Path, IReadOnlyList<Node> Body,
        IReadOnlyList<Node> ElseBody) : Node;
    private readonly record struct RenderScope(JsonElement Root, JsonElement Current);
}
