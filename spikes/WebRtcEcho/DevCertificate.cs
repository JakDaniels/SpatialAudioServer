// Copyright 2026 Simon Annetts/Jak Daniels
// SPDX-License-Identifier: Apache-2.0

using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace WebRtcEcho;

/// <summary>
/// A self-signed certificate for LAN testing: browsers only allow getUserMedia on HTTPS (or localhost).
/// Covers localhost, the host name and every local IPv4 address; saved so the browser exception survives restarts.
/// Delete the file to regenerate it (e.g. after the machine's address changes).
/// </summary>
public static class DevCertificate
{
    public static X509Certificate2 LoadOrCreate(string path, ILogger log)
    {
        if (File.Exists(path))
        {
            var existing = X509CertificateLoader.LoadPkcs12FromFile(path, password: null);
            if (existing.NotAfter > DateTime.Now.AddDays(7))
            {
                return existing;
            }
            existing.Dispose();
        }

        var names = new SubjectAlternativeNameBuilder();
        names.AddDnsName("localhost");
        names.AddDnsName(Dns.GetHostName());
        names.AddIpAddress(IPAddress.Loopback);
        var addresses = NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up)
            .SelectMany(n => n.GetIPProperties().UnicastAddresses)
            .Select(a => a.Address)
            .Where(a => a.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a))
            .Distinct()
            .ToList();
        addresses.ForEach(names.AddIpAddress);

        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest($"CN={Dns.GetHostName()} (SpatialAudioServer dev)", key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(names.Build());
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], false));

        using var created = request.CreateSelfSigned(DateTimeOffset.Now.AddDays(-1), DateTimeOffset.Now.AddYears(1));
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllBytes(path, created.Export(X509ContentType.Pkcs12));
        log.LogInformation("Created dev certificate {Path} for localhost, {Host}, {Addresses}",
            path, Dns.GetHostName(), string.Join(", ", addresses));
        return X509CertificateLoader.LoadPkcs12FromFile(path, password: null);
    }
}
