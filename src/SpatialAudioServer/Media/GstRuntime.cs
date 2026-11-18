// Copyright 2026 Simon Annetts/Jak Daniels
// SPDX-License-Identifier: Apache-2.0

using Gst;
using Gst.Interop;
using Gst.Sdp;
using Gst.WebRTC;

namespace SpatialAudioServer.Media;

public sealed record GstStatus(bool Enabled, bool Initialised, string? Version, IReadOnlyList<string> MissingElements, string? Error)
{
    public bool Ok => !Enabled || (Initialised && MissingElements.Count == 0);
}

/// <summary>Process-wide GStreamer start-up: library load, module type registration and the plugin check.</summary>
public sealed class GstRuntime(ILogger<GstRuntime> log)
{
    /// <summary>Every element the media layer builds. Missing any of these means the server cannot work.</summary>
    public static readonly IReadOnlyList<string> RequiredElements =
    [
        "webrtcbin", "nicesrc", "nicesink", "dtlssrtpenc", "dtlssrtpdec", "srtpenc", "srtpdec",
        "rtpopuspay", "rtpopusdepay", "opusenc", "opusdec",
        "audiomixer", "audiotestsrc", "audioconvert", "audioresample", "audioamplify", "audiopanorama",
        "queue", "tee", "capsfilter", "level", "audiointerleave", "audiomixmatrix", "input-selector",
        "sctpenc", "sctpdec", // data channels: Second Life viewers always offer one ("SLData")
    ];

    private readonly Lock _lock = new();

    public GstStatus Status { get; private set; } = new(false, false, null, [], null);

    /// <summary>Loads GStreamer and checks plugins. Never throws; the outcome is in <see cref="Status"/>.</summary>
    public GstStatus Initialize()
    {
        lock (_lock)
        {
            if (Status.Initialised)
            {
                return Status;
            }
            try
            {
                // Windows installations carry their own GStreamer (PACKAGING-PLAN.md, code change 4).
                if (EmbeddedGStreamer.UseIfPresent(System.Reflection.Assembly.GetEntryAssembly()?.GetName().Name ?? "sas") is { } embedded)
                {
                    log.LogInformation("Using the GStreamer embedded in {Folder}", embedded);
                }
                // Each module initialiser registers its boxed types; they all end in the same gst_init.
                GstSdp.Initialize();
                GstWebRTC.Initialize();
                ExceptionTrap.UnhandledException += ex => log.LogError(ex, "Unhandled exception in a GStreamer callback");

                var missing = RequiredElements.Where(e => ElementFactory.Find(e) is null).ToList();
                Engines.MixEngines.CheckElements(e => ElementFactory.Find(e) is not null);
                Status = new GstStatus(true, true, GstSharp.NativeVersion.Description, missing, null);
                log.LogInformation("{Version} loaded", Status.Version);
                if (missing.Count > 0)
                {
                    log.LogError("Missing GStreamer elements: {Missing}", string.Join(", ", missing));
                }
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or InvalidOperationException)
            {
                Status = new GstStatus(true, false, null, [], ex.Message);
                log.LogError(ex, "GStreamer could not be loaded");
            }
            return Status;
        }
    }
}
