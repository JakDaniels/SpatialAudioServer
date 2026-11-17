// Copyright 2026 Simon Annetts/Jak Daniels
// SPDX-License-Identifier: Apache-2.0

namespace SpatialAudioServer.Media;

/// <summary>
/// Viewer keys (avatar UUIDs, participant ids) that recently left a room, kept for <see cref="Window"/> so a viewer
/// that joins or reconnects can be told they have gone. A viewer keeps its voice list across a lost connection (it
/// retries without clearing it), so after a server restart, or a leave it missed, it would otherwise list them until
/// it logs out. Held per room, outside its pipeline, so it outlives media stops; thread-safe.
/// </summary>
internal sealed class ViewerDepartures(TimeProvider? time = null)
{
    /// <summary>How long a departure is replayed.</summary>
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(10);

    /// <summary>At most this many are kept (the oldest go first).</summary>
    public const int MaxKept = 256;

    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly Dictionary<string, DateTimeOffset> _gone = new(StringComparer.OrdinalIgnoreCase);

    public void Add(string key)
    {
        lock (_gone)
        {
            _gone[key] = _time.GetUtcNow();
            if (_gone.Count > MaxKept)
            {
                _gone.Remove(_gone.MinBy(g => g.Value).Key);
            }
        }
    }

    /// <summary>Departures within the window, oldest dropped.</summary>
    public IReadOnlyList<string> Recent()
    {
        lock (_gone)
        {
            var since = _time.GetUtcNow() - Window;
            foreach (var old in _gone.Where(g => g.Value < since).Select(g => g.Key).ToList())
            {
                _gone.Remove(old);
            }
            return [.. _gone.Keys];
        }
    }
}
