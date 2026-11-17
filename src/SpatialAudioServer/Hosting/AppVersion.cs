// Copyright 2026 Simon Annetts/Jak Daniels
// SPDX-License-Identifier: Apache-2.0

using System.Reflection;

namespace SpatialAudioServer.Hosting;

/// <summary>The version this build was made with (from git tags by MinVer; PACKAGING-PLAN.md "Versioning").</summary>
public static class AppVersion
{
    /// <summary>The full version, e.g. <c>0.2.1-alpha.0.3+1a2b3c4…</c> (SemVer, then the commit).</summary>
    public static string Informational { get; } = Of(typeof(AppVersion).Assembly);

    /// <summary>The informational version of an assembly, or "unknown".</summary>
    public static string Of(Assembly assembly) =>
        assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown";
}
