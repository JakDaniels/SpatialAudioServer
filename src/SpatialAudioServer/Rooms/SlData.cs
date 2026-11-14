using System.Text.Json;

namespace SpatialAudioServer.Rooms;

/// <summary>
/// One message from an SL viewer on its "SLData" data channel: a JSON object whose keys say what it carries (several
/// may come together). See PLAN.md Phase 7 and the viewer's <c>llvoicewebrtc.cpp</c>.
/// </summary>
/// <param name="Pose">
/// <c>sp/sh</c> (avatar position and rotation) and <c>lp/lh</c> (camera position and rotation): global coordinates in
/// centimetres, quaternions ×100, sent at most 10 times a second while they change. Converted to metres and unit
/// quaternions.
/// </param>
/// <param name="Joined"><c>j</c>: the viewer is ready; <see cref="Primary"/> when this is the region its avatar is in.</param>
/// <param name="Mutes"><c>m</c>: avatar UUID → muted, the viewer muting someone for itself.</param>
/// <param name="Gains"><c>ug</c>: avatar UUID → volume for itself (×100 per unit at the viewer's slider, ≤ 400).</param>
/// <param name="OtherKeys">Keys not understood (logged, for protocol work).</param>
public sealed record SlDataMessage(
    ClientPoseReport? Pose,
    bool Joined,
    bool Primary,
    IReadOnlyDictionary<string, bool> Mutes,
    IReadOnlyDictionary<string, int> Gains,
    IReadOnlyList<string> OtherKeys);

public static class SlData
{
    /// <summary>Longer messages are ignored (a pose update is about 200 bytes; a mute list grows with the room).</summary>
    public const int MaxMessageLength = 16 * 1024;

    private static readonly Quat Identity = new(0, 0, 0, 1);

    /// <summary>The message, or null if it is not a JSON object (or too long).</summary>
    public static SlDataMessage? Parse(string text)
    {
        if (text.Length > MaxMessageLength)
        {
            return null;
        }
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(text);
        }
        catch (JsonException)
        {
            return null;
        }
        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }
            Vec3? sp = null, lp = null;
            Quat? sh = null, lh = null;
            var joined = false;
            var primary = false;
            var mutes = new Dictionary<string, bool>();
            var gains = new Dictionary<string, int>();
            var other = new List<string>();
            foreach (var property in root.EnumerateObject())
            {
                switch (property.Name)
                {
                    case "sp": sp = Position(property.Value); break;
                    case "sh": sh = Rotation(property.Value); break;
                    case "lp": lp = Position(property.Value); break;
                    case "lh": lh = Rotation(property.Value); break;
                    case "j":
                        joined = true;
                        primary = property.Value.ValueKind == JsonValueKind.Object
                            && property.Value.TryGetProperty("p", out var p) && p.ValueKind == JsonValueKind.True;
                        break;
                    case "m":
                        foreach (var (id, value) in Members(property.Value))
                        {
                            if (value.ValueKind is JsonValueKind.True or JsonValueKind.False)
                            {
                                mutes[id] = value.GetBoolean();
                            }
                        }
                        break;
                    case "ug":
                        foreach (var (id, value) in Members(property.Value))
                        {
                            if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var gain))
                            {
                                gains[id] = gain;
                            }
                        }
                        break;
                    default: other.Add(property.Name); break;
                }
            }
            var pose = sp is { } speaker && lp is { } listener
                ? new ClientPoseReport(speaker, sh ?? Identity, listener, lh ?? Identity)
                : null;
            return new SlDataMessage(pose, joined, primary, mutes, gains, other);
        }
    }

    private static IEnumerable<(string, JsonElement)> Members(JsonElement element) =>
        element.ValueKind == JsonValueKind.Object
            ? element.EnumerateObject().Select(p => (p.Name, p.Value)).ToList()
            : [];

    private static double? Number(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var d)
            && double.IsFinite(d)
            ? d
            : null;

    /// <summary>Centimetres → metres.</summary>
    private static Vec3? Position(JsonElement element) =>
        element.ValueKind == JsonValueKind.Object
        && Number(element, "x") is { } x && Number(element, "y") is { } y && Number(element, "z") is { } z
            ? new Vec3(x / 100, y / 100, z / 100)
            : null;

    /// <summary>×100 integers → a unit quaternion (identity if it is degenerate).</summary>
    private static Quat? Rotation(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object
            || Number(element, "x") is not { } x || Number(element, "y") is not { } y
            || Number(element, "z") is not { } z || Number(element, "w") is not { } w)
        {
            return null;
        }
        var norm = Math.Sqrt(x * x + y * y + z * z + w * w);
        return norm < 1e-6 ? Identity : new Quat(x / norm, y / norm, z / norm, w / norm);
    }
}
