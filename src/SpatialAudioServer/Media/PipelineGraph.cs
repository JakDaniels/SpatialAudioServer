using System.Text.RegularExpressions;
using Gst;

namespace SpatialAudioServer.Media;

/// <summary>
/// A snapshot of a room's pipeline for the admin pipeline view: elements (with their pads), the links between pads, and
/// for each top-level element the summary block it belongs to. Bins are listed with their children only when asked
/// (<c>expand</c>), so the default snapshot stays small.
/// </summary>
/// <param name="Participants">Participant ids by the short id used in element names.</param>
public sealed record PipelineGraph(
    string Engine, IReadOnlyList<GraphElement> Elements, IReadOnlyList<GraphLink> Links, IReadOnlyDictionary<string, Guid> Participants);

/// <param name="Path">Element names from the pipeline down, joined with '/': unique, and what links and expand use.</param>
/// <param name="Parent">The containing bin's path, or null for a top-level element.</param>
/// <param name="Children">Number of child elements of a bin (listed only if it was expanded).</param>
/// <param name="Block">Top-level elements only: the summary block (see <see cref="GraphBlock"/>).</param>
/// <param name="Live">A few properties worth watching (queue fill, selector input, connection state), as text.</param>
public sealed record GraphElement(
    string Path, string Name, string Factory, string? Parent, bool IsBin, int Children, string State, GraphBlock? Block,
    IReadOnlyList<GraphPad> Pads, IReadOnlyDictionary<string, string> Live);

/// <param name="Direction">"src" or "sink".</param>
/// <param name="Caps">Negotiated caps, or null before negotiation.</param>
/// <param name="Ghost">A bin's ghost pad (proxies a pad inside the bin).</param>
public sealed record GraphPad(string Name, string Direction, string? Caps, bool Ghost);

/// <summary>Data flows <paramref name="From"/> → <paramref name="To"/>; both are "element path:pad name".</summary>
public sealed record GraphLink(string From, string To);

/// <summary>
/// Which summary block a top-level element belongs to. Kinds: <c>webrtc</c> (a participant's webrtcbin), <c>decode</c>
/// (their incoming audio to raw), <c>room</c> (the room's shared mixing input), <c>cluster</c> (a shared mix in
/// matrix-shared), <c>personal</c> (a talker's own mix), <c>listener</c> (what is sent to one participant),
/// <c>other</c> (not recognised).
/// </summary>
/// <param name="Key">Identifies the block, e.g. "decode:{participant}" or "cluster:c3-1a2b3c4d".</param>
/// <param name="Participant">The participant it belongs to, if any.</param>
/// <param name="Name">The cluster's name, for clusters.</param>
public sealed record GraphBlock(string Key, string Kind, Guid? Participant, string? Name);

/// <summary>One element's full detail: every readable property as text.</summary>
public sealed record GraphElementDetails(string Path, string Factory, string State, IReadOnlyList<GraphProperty> Properties);

public sealed record GraphProperty(string Name, string Value);

/// <summary>Reads <see cref="PipelineGraph"/> snapshots. Call on the room's worker so the graph does not change meanwhile.</summary>
internal static partial class PipelineGraphReader
{
    /// <summary>Properties shown live, by element factory.</summary>
    private static readonly Dictionary<string, string[]> LiveProperties = new()
    {
        ["queue"] = ["current-level-buffers", "current-level-time", "max-size-time", "leaky"],
        ["webrtcbin"] = ["connection-state", "ice-connection-state", "latency"],
        ["tee"] = ["num-src-pads"],
        ["audiomixmatrix"] = ["in-channels", "out-channels"],
        ["opusenc"] = ["bitrate", "complexity", "frame-size"],
        ["rtpopuspay"] = ["pt"],
        ["capsfilter"] = ["caps"],
        ["audioamplify"] = ["amplification"],
        ["audiopanorama"] = ["panorama"],
    };

    public static PipelineGraph Read(Pipeline pipeline, string engine, IReadOnlyCollection<Guid> participants, Guid roomId,
        IReadOnlySet<string> expand)
    {
        var shortIds = participants.ToDictionary(Engines.MediaBlocks.ShortId);
        var roomShort = roomId.ToString("N")[..8];
        var elements = new List<GraphElement>();
        var links = new List<GraphLink>();
        foreach (var element in Children(pipeline))
        {
            Walk(element, null, shortIds, roomShort, expand, elements, links);
        }
        // Links whose other end is not listed (inside a bin that was not expanded) are left out.
        var listed = elements.Select(e => e.Path).ToHashSet();
        links.RemoveAll(l => !listed.Contains(ElementOf(l.From)) || !listed.Contains(ElementOf(l.To)));
        return new PipelineGraph(engine, elements, links, shortIds);
    }

    public static GraphElementDetails? Details(Pipeline pipeline, string path)
    {
        if (Find(pipeline, path) is not { } element)
        {
            return null;
        }
        var properties = NativeGObject.ReadProperties(element.Handle).Select(p => new GraphProperty(p.Name, p.Value)).ToList();
        return new GraphElementDetails(path, FactoryOf(element), StateOf(element), properties);
    }

    /// <summary>The summary block of a top-level element, from the names the engines give their elements.</summary>
    public static GraphBlock Classify(string name, IReadOnlyDictionary<string, Guid> shortIds, string roomShort)
    {
        var dash = name.IndexOf('-');
        var prefix = dash < 0 ? name : name[..dash];
        var rest = dash < 0 ? "" : name[(dash + 1)..];
        GraphBlock Of(string kind, string? id) =>
            id is not null && shortIds.TryGetValue(id, out var pid) ? new GraphBlock($"{kind}:{pid}", kind, pid, null) : Other();
        GraphBlock Cluster(string clusterName) => new($"cluster:{clusterName}", "cluster", null, clusterName);
        GraphBlock Room() => new("room", "room", null, null);
        GraphBlock Other() => new("other", "other", null, null);

        switch (prefix)
        {
            case "webrtc":
                return Of("webrtc", rest);
            case "ingress" or "tee" or "noslot":
                return Of("decode", rest);
            case "interleave" or "roomtee" or "silencecaps":
                return Room();
            case "silence":
                return rest == roomShort ? Room() : Of("listener", rest);
            case "mix" or "egress" or "sel" or "pay":
                return Of("listener", rest);
            case "cq":
                return Of("listener", rest.Split('-')[0]);
            case "penc":
                return Of("personal", rest);
            case "cenc" or "ctee":
                return Cluster(rest);
            case "chain": // chain-{source}-{listener}: part of the listener's mix
                return rest.Split('-') is [_, var listener] ? Of("listener", listener) : Other();
            case "q" or "matrix":
                if (rest.StartsWith("p-", StringComparison.Ordinal))
                {
                    return Of("personal", rest[2..]);
                }
                return ClusterName().IsMatch(rest) ? Cluster(rest) : Of("listener", rest);
            default:
                return Other();
        }
    }

    private static void Walk(Element element, string? parent, IReadOnlyDictionary<string, Guid> shortIds, string roomShort,
        IReadOnlySet<string> expand, List<GraphElement> elements, List<GraphLink> links)
    {
        var name = element.Name ?? "?";
        var path = parent is null ? name : $"{parent}/{name}";
        var children = element is Bin bin ? Children(bin) : [];
        var pads = new List<GraphPad>();
        foreach (var pad in Pads(element))
        {
            var padName = pad.Name ?? "?";
            var direction = pad.Direction == PadDirection.Src ? "src" : "sink";
            string? caps;
            using (var current = pad.GetCurrentCaps())
            {
                caps = current?.ToString();
            }
            var ghost = pad as GhostPad;
            pads.Add(new GraphPad(padName, direction, caps, ghost is not null));

            if (direction == "src" && pad.GetPeer() is { } peer && peer.GetParentElement() is { } peerElement)
            {
                // The peer is in the same bin as this element (or, for a bin's ghost src pad, in the bin's parent).
                var peerParent = parent;
                links.Add(new GraphLink($"{path}:{padName}", $"{Join(peerParent, peerElement.Name)}:{peer.Name}"));
            }
            // A ghost pad's link into the bin: ghost sink → inner pad, inner pad → ghost src.
            if (ghost?.GetTarget() is { } target && target.GetParentElement() is { } inner)
            {
                var innerPad = $"{path}/{inner.Name}:{target.Name}";
                links.Add(direction == "sink"
                    ? new GraphLink($"{path}:{padName}", innerPad)
                    : new GraphLink(innerPad, $"{path}:{padName}"));
            }
        }

        var factory = FactoryOf(element);
        var live = new Dictionary<string, string>();
        if (LiveProperties.TryGetValue(factory, out var names))
        {
            foreach (var property in names)
            {
                if (NativeGObject.ReadProperty(element.Handle, property) is { } value)
                {
                    live[property] = value;
                }
            }
        }
        if (factory == "input-selector" && NativeGObject.ReadObjectName(element.Handle, "active-pad") is { } active)
        {
            live["active-pad"] = active;
        }

        var block = parent is null ? Classify(name, shortIds, roomShort) : null;
        elements.Add(new GraphElement(path, name, factory, parent, element is Bin, children.Count, StateOf(element), block, pads, live));
        if (element is Bin && expand.Contains(path))
        {
            foreach (var child in children)
            {
                Walk(child, path, shortIds, roomShort, expand, elements, links);
            }
        }
    }

    private static string Join(string? parent, string? name) => parent is null ? name ?? "?" : $"{parent}/{name}";

    private static string ElementOf(string padPath) => padPath[..padPath.LastIndexOf(':')];

    private static Element? Find(Bin root, string path)
    {
        Element? current = root;
        foreach (var name in path.Split('/'))
        {
            current = current is Bin bin ? Children(bin).FirstOrDefault(e => e.Name == name) : null;
            if (current is null)
            {
                return null;
            }
        }
        return current;
    }

    private static IReadOnlyList<Element> Children(Bin bin)
    {
        using var iterator = bin.IterateElements();
        // gst_bin_iterate_elements lists the newest first; reverse for the order they were added.
        return iterator.Items<Element>().Reverse().ToList();
    }

    private static IReadOnlyList<Pad> Pads(Element element)
    {
        using var iterator = element.IteratePads();
        return iterator.Items<Pad>();
    }

    private static string FactoryOf(Element element) =>
        element.GetFactory()?.Name ?? (element is Bin ? "bin" : "element");

    private static string StateOf(Element element)
    {
        element.GetState(out var state, out _, ClockTime.FromMilliseconds(0));
        return state.ToString().ToLowerInvariant();
    }

    [GeneratedRegex(@"^c\d+-")]
    private static partial Regex ClusterName();
}
