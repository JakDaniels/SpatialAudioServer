// Copyright 2026 Simon Annetts/Jak Daniels
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;

namespace SpatialAudioServer.Media.Spatial;

public enum AcousticKind
{
    /// <summary>One number.</summary>
    Number,

    /// <summary>A list of numbers (JSON array; a notecard's "1, 2, 3" or a single number are accepted too).</summary>
    List,

    /// <summary>One of <see cref="AcousticParameter.Choices"/>.</summary>
    Choice,
}

/// <summary>
/// One tunable value of an <c>acoustic</c> room: a property of the acousticmultimixer element, overriding the
/// room's preset (or, for the distance curve, its profile) while set.
/// </summary>
/// <param name="Key">The JSON key in a room's <c>acousticParameters</c>.</param>
/// <param name="Property">The element property it sets; also accepted as the key (in-world notecards use it).</param>
/// <param name="Group">For the admin UI: <c>distance</c>, <c>reflections</c>, <c>reverb</c> or <c>dimensions</c>.</param>
/// <param name="SliderMax">For the admin UI: the top of a slider (the API takes up to <paramref name="Max"/>).</param>
/// <param name="Step">For the admin UI: a slider's step.</param>
/// <param name="MinCount">Lists: the fewest values.</param>
/// <param name="MaxCount">Lists: the most values.</param>
public sealed record AcousticParameter(
    string Key, string Property, string Group, AcousticKind Kind, string Unit, string Description,
    double Min = 0, double Max = 0, double SliderMax = 0, double Step = 0, int MinCount = 1, int MaxCount = 0,
    IReadOnlyList<string>? Choices = null);

/// <summary>
/// The values a room's <c>acousticParameters</c> may set (issue #31; acousticmultimixer #7 and #8): validation and
/// merging for the API, the element settings for the <c>acoustic</c> engine, and reading back the values in effect.
/// Other engines ignore them.
/// </summary>
public static class AcousticParameters
{
    public const string Distance = "distance";
    public const string Reflections = "reflections";
    public const string Reverb = "reverb";
    public const string Dimensions = "dimensions";

    public static IReadOnlyList<string> Materials { get; } =
    [
        "open", "concrete", "stone", "brick", "tile", "plaster", "plasterboard", "wood", "glass", "metal", "carpet", "curtain",
        "acoustic-tile", "water", "earth", "grass",
    ];

    public static IReadOnlyList<string> Contents { get; } = ["empty", "sparse", "furnished", "crowded"];

    public static IReadOnlyList<AcousticParameter> All { get; } =
    [
        // The distance curve (overriding the room's profile) and direction.
        new("distanceModel", "distance-model", Distance, AcousticKind.Choice, "", "How volume falls with distance beyond the reference distance",
            Choices: ["inverse", "linear", "exponential"]),
        new("referenceDistance", "reference-distance", Distance, AcousticKind.Number, "m", "Voices this close or closer are heard at full volume",
            Max: 1000, SliderMax: 20, Step: 0.1),
        new("rolloff", "rolloff", Distance, AcousticKind.Number, "", "How fast volume falls beyond the reference distance",
            Max: 1000, SliderMax: 5, Step: 0.05),
        new("maxRange", "max-range", Distance, AcousticKind.Number, "m", "Voices further away are silent", Max: 10000, SliderMax: 200, Step: 1),
        new("taper", "taper", Distance, AcousticKind.Number, "", "Where voices start fading out, as a share of the maximum range (1 = no fade)",
            Max: 1, SliderMax: 1, Step: 0.01),
        new("nearDistance", "near-distance", Distance, AcousticKind.Number, "m",
            "Closer than this a voice's room sound is turned down, so it stands out (0 = never)", Max: 1000, SliderMax: 10, Step: 0.1),
        new("directionModel", "direction-model", Distance, AcousticKind.Choice, "",
            "pan: a stereo pan; head: the far ear hears later, quieter and duller", Choices: ["pan", "head"]),
        new("airAbsorption", "air-absorption", Distance, AcousticKind.Number, "dB/m", "Treble lost with distance, at 8 kHz (air: about 0.1)",
            Max: 10, SliderMax: 1, Step: 0.01),
        new("rearShadow", "rear-shadow", Distance, AcousticKind.Number, "Hz", "Head model: treble cut for a voice straight behind (0 = none)",
            Max: 20000, SliderMax: 20000, Step: 100),

        // Early reflections (overriding the preset's, or what the dimensions give).
        new("reflectionDelays", "reflection-delays", Reflections, AcousticKind.List, "ms",
            "The reflections' delays after the direct sound, paired in order with their levels", Max: 500, SliderMax: 500, Step: 0.5, MaxCount: 16),
        new("reflectionLevels", "reflection-levels", Reflections, AcousticKind.List, "dB",
            "The reflections' levels against a voice at the reference distance", Min: -80, Max: 12, SliderMax: 0, Step: 0.5, MaxCount: 16),
        new("wallCutoff", "wall-cutoff", Reflections, AcousticKind.Number, "Hz", "Low-pass on the reflections: the walls' treble absorption (0 = none)",
            Max: 24000, SliderMax: 20000, Step: 100),
        new("roomFalloff", "room-falloff", Reflections, AcousticKind.Number, "",
            "How fast the room's sound falls with distance, against the voice's (less: far voices sound roomier)", Max: 2, SliderMax: 2, Step: 0.05),
        new("reflectionSpread", "reflection-spread", Reflections, AcousticKind.Number, "",
            "How far apart the reflections from all around are in the two ears, as a share of their delay", Max: 0.5, SliderMax: 0.5, Step: 0.01),
        new("reflectionDiffusion", "reflection-diffusion", Reflections, AcousticKind.Number, "",
            "The first reflection's share heard from all around, rising to all of it for the last", Max: 1, SliderMax: 1, Step: 0.01),
        new("nearFloor", "near-floor", Reflections, AcousticKind.Number, "m", "Where the near distance's turn-down of a close voice's room stops",
            Max: 100, SliderMax: 2, Step: 0.05),

        // Late reverb.
        new("reverbLevel", "reverb-level", Reverb, AcousticKind.Number, "dB", "The reverb against the room's own level (−60 = none)",
            Min: -60, Max: 12, SliderMax: 12, Step: 0.5),
        new("reverbDecay", "reverb-decay", Reverb, AcousticKind.Number, "s", "Time for the reverb to fall by 60 dB in the bass",
            Min: 0.05, Max: 20, SliderMax: 10, Step: 0.05),
        new("reverbTrebleDecay", "reverb-treble-decay", Reverb, AcousticKind.Number, "s",
            "Time for the reverb to fall by 60 dB in the treble (at most the bass decay)", Min: 0.05, Max: 20, SliderMax: 10, Step: 0.05),
        new("reverbSize", "reverb-size", Reverb, AcousticKind.Number, "ms", "About the time between walls: when the tail starts",
            Min: 1, Max: 250, SliderMax: 250, Step: 1),

        // The room's dimensions: with all three sizes, they give the reflections, cutoff and reverb.
        new("roomWidth", "room-width", Dimensions, AcousticKind.Number, "m", "The room's size east to west (0 = not given)",
            Max: 1000, SliderMax: 100, Step: 0.5),
        new("roomDepth", "room-depth", Dimensions, AcousticKind.Number, "m", "The room's size north to south (0 = not given)",
            Max: 1000, SliderMax: 100, Step: 0.5),
        new("roomHeight", "room-height", Dimensions, AcousticKind.Number, "m", "The room's height (0 = not given)", Max: 1000, SliderMax: 40, Step: 0.1),
        new("wallMaterial", "wall-material", Dimensions, AcousticKind.Choice, "", "What the walls are made of", Choices: Materials),
        new("floorMaterial", "floor-material", Dimensions, AcousticKind.Choice, "", "What the floor is made of", Choices: Materials),
        new("ceilingMaterial", "ceiling-material", Dimensions, AcousticKind.Choice, "", "What the ceiling is made of", Choices: Materials),
        new("wallAbsorption", "wall-absorption", Dimensions, AcousticKind.List, "",
            "How much sound the walls absorb (0–1), overriding their material: bass, treble (or one for both)", Max: 1, SliderMax: 1, Step: 0.01,
            MaxCount: 2),
        new("floorAbsorption", "floor-absorption", Dimensions, AcousticKind.List, "", "The floor's absorption, as for the walls",
            Max: 1, SliderMax: 1, Step: 0.01, MaxCount: 2),
        new("ceilingAbsorption", "ceiling-absorption", Dimensions, AcousticKind.List, "", "The ceiling's absorption, as for the walls",
            Max: 1, SliderMax: 1, Step: 0.01, MaxCount: 2),
        new("roomContents", "room-contents", Dimensions, AcousticKind.Choice, "", "What is in the room, absorbing sound (furniture and people)",
            Choices: Contents),
    ];

    private static readonly Dictionary<string, AcousticParameter> ByName = All
        .SelectMany(p => new[] { (Name: p.Key, Parameter: p), (Name: p.Property, Parameter: p) })
        .DistinctBy(e => e.Name, StringComparer.OrdinalIgnoreCase) // rolloff and taper are both
        .ToDictionary(e => e.Name, e => e.Parameter, StringComparer.OrdinalIgnoreCase);

    /// <summary>A parameter by its JSON key or its element property name (case-insensitive).</summary>
    public static AcousticParameter? Find(string name) => ByName.GetValueOrDefault(name.Trim());

    /// <summary>
    /// <paramref name="current"/> with <paramref name="changes"/> merged in (keys by JSON key or property name; null
    /// removes one), values normalised; null when nothing is left. Errors go to <paramref name="errors"/> under
    /// <c>field.key</c>, so the caller refuses the whole change.
    /// </summary>
    public static JsonObject? Merge(JsonObject? current, JsonObject changes, Action<string, string> errors, string field = "acousticParameters")
    {
        var merged = current?.DeepClone().AsObject() ?? [];
        foreach (var (name, value) in changes)
        {
            if (Find(name) is not { } parameter)
            {
                errors($"{field}.{name}", "is not an acoustic parameter");
                continue;
            }
            if (value is null)
            {
                merged.Remove(parameter.Key);
                continue;
            }
            if (Normalize(parameter, value, out var error) is { } normalized)
            {
                merged[parameter.Key] = normalized;
            }
            else
            {
                errors($"{field}.{name}", error!);
            }
        }
        return merged.Count == 0 ? null : merged;
    }

    /// <summary>A value checked against its parameter and put in the stored form, or null with the reason.</summary>
    public static JsonNode? Normalize(AcousticParameter parameter, JsonNode value, out string? error)
    {
        error = null;
        switch (parameter.Kind)
        {
            case AcousticKind.Number when Number(value) is { } n:
                if (!double.IsFinite(n) || n < parameter.Min || n > parameter.Max)
                {
                    error = $"must be between {Text(parameter.Min)} and {Text(parameter.Max)}";
                    return null;
                }
                return JsonValue.Create(n);
            case AcousticKind.Number:
                error = "must be a number";
                return null;
            case AcousticKind.List:
                var items = value switch
                {
                    JsonArray array => array.Select(i => i is null ? null : Number(i)).ToList(),
                    JsonValue v when v.TryGetValue<string>(out var s) => s.Split([',', ' ', ';'], StringSplitOptions.RemoveEmptyEntries)
                        .Select(t => double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : (double?)null).ToList(),
                    _ when Number(value) is { } single => [single],
                    _ => null,
                };
                if (items is null || items.Any(i => i is null))
                {
                    error = "must be a list of numbers";
                    return null;
                }
                if (items.Count < parameter.MinCount || items.Count > parameter.MaxCount)
                {
                    error = $"must have {parameter.MinCount} to {parameter.MaxCount} values";
                    return null;
                }
                if (items.Any(i => !double.IsFinite(i!.Value) || i < parameter.Min || i > parameter.Max))
                {
                    error = $"values must be between {Text(parameter.Min)} and {Text(parameter.Max)}";
                    return null;
                }
                return new JsonArray(items.Select(i => (JsonNode?)JsonValue.Create(i!.Value)).ToArray());
            default:
                var choice = value is JsonValue c && c.TryGetValue<string>(out var t) ? t.Trim().ToLowerInvariant() : null;
                if (choice is null || !parameter.Choices!.Contains(choice))
                {
                    error = $"must be one of: {string.Join(", ", parameter.Choices!)}";
                    return null;
                }
                return JsonValue.Create(choice);
        }
    }

    /// <summary>
    /// Every parameter's element setting, as text for <c>gst_util_set_object_arg</c>: the room's value, else the
    /// value that leaves the preset in charge (−1, an empty list, no dimensions) or the room's distance curve
    /// (<paramref name="curve"/>, the server's spatial settings with the room's profile; null: the element's
    /// defaults).
    /// </summary>
    public static IReadOnlyList<(string Property, string Value)> ElementSettings(JsonObject? parameters, SpatialOptions? curve)
    {
        var settings = new List<(string, string)>(All.Count);
        foreach (var p in All)
        {
            var value = parameters?[p.Key] is { } set ? Serialize(p, set) : Unset(p, curve);
            settings.Add((p.Property, value));
        }
        return settings;
    }

    private static string Unset(AcousticParameter p, SpatialOptions? curve) => p.Key switch
    {
        "distanceModel" => (curve?.DistanceModel ?? DistanceModel.Exponential).ToString().ToLowerInvariant(),
        "referenceDistance" => Text(curve?.ReferenceDistance ?? 2),
        "rolloff" => Text(curve?.Rolloff ?? 1),
        "maxRange" => Text(curve?.MaxRange ?? 60),
        "taper" => Text(curve?.Taper ?? 0.75),
        "nearDistance" => Text(curve?.NearDistance ?? 2),
        "directionModel" => "head",
        "airAbsorption" => "0.1",
        "rearShadow" => "4000",
        "reverbLevel" => "0",
        "roomWidth" or "roomDepth" or "roomHeight" => "0",
        "wallMaterial" or "ceilingMaterial" => "plaster",
        "floorMaterial" => "wood",
        "roomContents" => "furnished",
        _ => p.Kind == AcousticKind.List ? "< >" : "-1",
    };

    private static string Serialize(AcousticParameter p, JsonNode value) => p.Kind switch
    {
        AcousticKind.List => "< " + string.Join(", ", value.AsArray().Select(v => Text(Number(v!) ?? 0))) + " >",
        AcousticKind.Number => Text(Number(value) ?? 0),
        _ => value.GetValue<string>(),
    };

    /// <summary>
    /// The values in effect, by JSON key, from the element: <paramref name="roomParameters"/> is its
    /// <c>room-parameters</c> as text (the preset's values with the overrides and what dimensions give), and
    /// <paramref name="read"/> serialises one of its properties (for the rest). Reverb values are null without a
    /// reverb. Also <c>room</c>, <c>reverbRoomLevel</c>, and <c>overridden</c> and <c>derived</c> (JSON keys).
    /// </summary>
    public static JsonObject Values(string? roomParameters, Func<string, string?> read)
    {
        var fields = roomParameters is null ? [] : ParseStructure(roomParameters);
        var values = new JsonObject();
        foreach (var p in All)
        {
            JsonNode? value;
            if (fields.TryGetValue(p.Property, out var field))
            {
                value = field.DeepClone();
            }
            else if (p.Group is Reflections or Reverb && p.Key != "reverbLevel")
            {
                value = null; // not in room-parameters: the room has none (outdoors, or no reverb)
            }
            else
            {
                value = read(p.Property) is { } text ? ParseValue(text) : null;
            }
            if (p.Kind == AcousticKind.List && value is not JsonArray && value is not null)
            {
                value = new JsonArray(value);
            }
            values[p.Key] = value;
        }
        if (fields.TryGetValue("room", out var room))
        {
            values["room"] = room.DeepClone();
        }
        values["reverbRoomLevel"] = fields.TryGetValue("reverb-room-level", out var level) ? level.DeepClone() : null;
        foreach (var list in new[] { "overridden", "derived" })
        {
            var names = fields.TryGetValue(list, out var node) ? node as JsonArray ?? new JsonArray(node.DeepClone()) : [];
            values[list] = new JsonArray(names
                .Select(n => n?.GetValue<string>() is { } name && Find(name) is { } p ? (JsonNode?)JsonValue.Create(p.Key) : null)
                .Where(n => n is not null)
                .ToArray());
        }
        return values;
    }

    /// <summary>
    /// The fields of a serialised GstStructure ("name, a=(double)1.5, b=(double)&lt; 1, 2 &gt;, c=(string)x;") as JSON:
    /// numbers, strings, and lists of them.
    /// </summary>
    public static Dictionary<string, JsonNode> ParseStructure(string text)
    {
        var fields = new Dictionary<string, JsonNode>(StringComparer.Ordinal);
        var parts = SplitTopLevel(text.Trim().TrimEnd(';'));
        foreach (var part in parts.Skip(1)) // the structure's name first
        {
            var equals = part.IndexOf('=');
            if (equals <= 0)
            {
                continue;
            }
            if (ParseValue(part[(equals + 1)..]) is { } value)
            {
                fields[part[..equals].Trim()] = value;
            }
        }
        return fields;
    }

    /// <summary>One serialised value: "(type)value", a list "&lt; a, b &gt;", a number or a word.</summary>
    public static JsonNode? ParseValue(string text)
    {
        var t = StripType(text.Trim());
        if (t.StartsWith('<') && t.EndsWith('>'))
        {
            var inner = t[1..^1].Trim();
            return new JsonArray(inner.Length == 0 ? [] : SplitTopLevel(inner).Select(ParseValue).ToArray());
        }
        if (double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
        {
            return JsonValue.Create(Math.Round(number, 4));
        }
        return t.Length == 0 ? null : JsonValue.Create(t.Trim('"'));
    }

    private static string StripType(string text) =>
        text.StartsWith('(') && text.IndexOf(')') is > 0 and var close ? text[(close + 1)..].Trim() : text;

    private static List<string> SplitTopLevel(string text)
    {
        var parts = new List<string>();
        var depth = 0;
        var quoted = false;
        var current = new StringBuilder();
        foreach (var c in text)
        {
            if (c == '"')
            {
                quoted = !quoted;
            }
            else if (!quoted && c is '<' or '[' or '{')
            {
                depth++;
            }
            else if (!quoted && c is '>' or ']' or '}')
            {
                depth--;
            }
            if (c == ',' && depth == 0 && !quoted)
            {
                parts.Add(current.ToString().Trim());
                current.Clear();
            }
            else
            {
                current.Append(c);
            }
        }
        if (current.Length > 0)
        {
            parts.Add(current.ToString().Trim());
        }
        return parts;
    }

    private static double? Number(JsonNode value) => value switch
    {
        JsonValue v when v.TryGetValue<double>(out var d) => d,
        JsonValue v when v.TryGetValue<string>(out var s)
            && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) => parsed,
        _ => null,
    };

    private static string Text(double value) => value.ToString("R", CultureInfo.InvariantCulture);
}
