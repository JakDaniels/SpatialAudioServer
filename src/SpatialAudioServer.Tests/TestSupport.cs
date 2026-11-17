// Copyright 2026 Simon Annetts/Jak Daniels
// SPDX-License-Identifier: Apache-2.0

using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;

namespace SpatialAudioServer.Tests;

/// <summary>
/// The test host's requests have no client address: a request with <see cref="Header"/> gets that address (as if
/// it came from there), before any of the server's own middleware runs.
/// </summary>
public sealed class TestRemoteAddress : IStartupFilter
{
    public const string Header = "X-Test-Remote-Address";

    public Action<Microsoft.AspNetCore.Builder.IApplicationBuilder> Configure(Action<Microsoft.AspNetCore.Builder.IApplicationBuilder> next) =>
        app =>
        {
            Microsoft.AspNetCore.Builder.UseExtensions.Use(app, (http, nextStep) =>
            {
                if (http.Request.Headers.TryGetValue(Header, out var value) && System.Net.IPAddress.TryParse(value, out var address))
                {
                    http.Connection.RemoteIpAddress = address;
                    http.Request.Headers.Remove(Header);
                }
                return nextStep(http);
            });
            next(app);
        };
}

/// <summary>A temporary directory, removed (with any pooled SQLite connections) on dispose.</summary>
public sealed class TempDir : IDisposable
{
    public string Path { get; } = Directory.CreateTempSubdirectory("sas-tests-").FullName;

    public string File(string name) => System.IO.Path.Combine(Path, name);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(Path, recursive: true);
    }
}

/// <summary>The real server in memory, on its own SQLite file, with GStreamer off unless asked for.</summary>
public sealed class ServerFactory(string databasePath, IDictionary<string, string?>? settings = null) : WebApplicationFactory<Program>
{
    public const string ApiKey = "test-bootstrap-key";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("Storage:DatabasePath", databasePath);
        builder.UseSetting("Auth:ApiKeys:0", ApiKey);
        builder.UseSetting("Media:Enabled", "false");
        builder.ConfigureServices(services =>
            Microsoft.Extensions.DependencyInjection.ServiceCollectionServiceExtensions.AddSingleton<IStartupFilter, TestRemoteAddress>(services));
        foreach (var (key, value) in settings ?? new Dictionary<string, string?>())
        {
            builder.UseSetting(key, value);
        }
    }

    public HttpClient CreateAuthedClient(string key = ApiKey)
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", key);
        return client;
    }
}

public static class Json
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public static async Task<T> Read<T>(this HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<T>(Options))!;
}

/// <summary>A clock tests can move.</summary>
public sealed class ManualTime(DateTimeOffset start) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = start;

    public override DateTimeOffset GetUtcNow() => Now;
}
