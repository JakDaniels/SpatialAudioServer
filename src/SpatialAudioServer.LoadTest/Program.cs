using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using SpatialAudioServer.Api;
using SpatialAudioServer.Media;
using SpatialAudioServer.Rooms;

namespace SpatialAudioServer.LoadTest;

/// <summary>
/// Load generator: fills rooms on a running server with fake clients (each sending its own tone), checks every
/// client hears everyone else and not itself, and reports server CPU/memory from /api/status.
/// Run it on a different machine from the server for meaningful CPU numbers: each fake client costs about as much
/// as the server spends per participant.
/// </summary>
/// <remarks>Explicit entry point: a top-level-statements Program would clash with the server's Program in the tests.</remarks>
public static class LoadTestProgram
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public static async Task<int> Main(string[] args)
    {
        Options options;
        try
        {
            options = Options.Parse(args);
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine(ex.Message);
            Console.Error.WriteLine(Options.Usage);
            return 2;
        }

        using var logs = LoggerFactory.Create(b => b.AddSimpleConsole(o => o.SingleLine = true).SetMinimumLevel(LogLevel.Warning));
        new GstRuntime(logs.CreateLogger<GstRuntime>()).Initialize();

        using var handler = new HttpClientHandler();
        if (options.Insecure)
        {
            handler.ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator;
        }
        using var http = new HttpClient(handler) { BaseAddress = options.Server, Timeout = TimeSpan.FromSeconds(60) };
        http.DefaultRequestHeaders.Add("X-Api-Key", options.ApiKey);

        var engines = options.Engines.Count > 0 ? options.Engines : [(await Status(http)).DefaultEngine];
        var results = new List<RunResult>();
        foreach (var engine in engines)
        {
            if (results.Count > 0)
            {
                await Task.Delay(TimeSpan.FromSeconds(3)); // let the previous run's teardown finish
            }
            results.Add(await RunAsync(http, options, engine, logs));
        }

        if (results.Count > 1)
        {
            Console.WriteLine();
            Console.WriteLine($"{"engine",-20} {"result",-6} {"CPU %core",9} {"% of all",8} {"MB",6} {"threads",7} {"heard min",9}");
            foreach (var r in results)
            {
                Console.WriteLine($"{r.Engine,-20} {(r.Pass ? "PASS" : "FAIL"),-6} {r.CpuCores * 100,9:F0} {r.CpuCores * 100 / r.Cores,8:F0} " +
                    $"{r.MemoryBytes / 1048576.0,6:F0} {r.Threads,7} {r.HeardMin,9:P1}");
            }
        }
        return results.All(r => r.Pass) ? 0 : 1;
    }

    private sealed record RunResult(string Engine, bool Pass, double CpuCores, int Cores, long MemoryBytes, int Threads, double HeardMin);

    private static async Task<RunResult> RunAsync(HttpClient http, Options options, string engine, ILoggerFactory logs)
    {
        var n = options.Participants;
        var volume = Math.Min(0.1, 0.6 / Math.Max(1, n - 1)); // keep the sum of n-1 tones under full scale
        var tones = Enumerable.Range(0, n).Select(i => 300.0 + 50 * i).ToArray(); // exact Goertzel bins at 20 ms
        Console.WriteLine($"Server {options.Server}, engine {engine}: {options.Rooms} room(s) x {n} participants, tone volume {volume:F3}");

        var rooms = new List<Guid>();
        var clients = new List<(FakeClient Client, int Room)>();
        try
        {
            for (var r = 0; r < options.Rooms; r++)
            {
                var room = await Post<RoomSnapshot>(http, "api/rooms", new { name = $"loadtest-{engine}-{r}", maxParticipants = n, engine });
                rooms.Add(room.Id);
                for (var i = 0; i < n; i++)
                {
                    var join = await Post<JoinResponse>(http, $"api/rooms/{room.Id}/participants", new { displayName = $"load-{r}-{i}" });
                    var client = new FakeClient($"r{r}p{i}", tones[i], tones, options.JitterBufferMs, logs.CreateLogger("fake"), volume);
                    clients.Add((client, r));
                    await client.ConnectAsync(http, join.ParticipantId, join.JoinToken);
                }
                Console.WriteLine($"  room {r}: {n} connected");
            }

            var settle = Stopwatch.StartNew();
            while (settle.Elapsed < TimeSpan.FromSeconds(45))
            {
                clients.ForEach(c => c.Client.Meter.Reset());
                await Task.Delay(1000);
                if (clients.All(c => Score(c, clients).HeardFraction >= 0.99))
                {
                    break;
                }
            }
            Console.WriteLine($"Settled in {settle.Elapsed.TotalSeconds:F1} s; measuring for {options.Duration.TotalSeconds:F0} s");

            var before = await Status(http);
            var wall = Stopwatch.StartNew();
            var self = Process.GetCurrentProcess().TotalProcessorTime;
            clients.ForEach(c => c.Client.Meter.Reset());
            await Task.Delay(options.Duration);
            var after = await Status(http);
            var elapsed = wall.Elapsed.TotalSeconds;
            var selfCpu = (Process.GetCurrentProcess().TotalProcessorTime - self).TotalSeconds;

            var scores = clients.Select(c => Score(c, clients)).ToList();
            var serverCpu = (after.Process.CpuSeconds - before.Process.CpuSeconds) / elapsed;
            var pairs = options.Rooms * n * (n - 1);
            Console.WriteLine();
            Console.WriteLine($"engine            {engine}");
            Console.WriteLine($"participants      {clients.Count} ({pairs} source→listener pairs)");
            Console.WriteLine($"server CPU        {serverCpu * 100:F0}% of one core = {serverCpu * 100 / after.Process.ProcessorCount:F0}% of {after.Process.ProcessorCount} cores");
            Console.WriteLine($"server memory     {after.Process.WorkingSetBytes / 1048576.0:F0} MB, {after.Process.Threads} threads");
            Console.WriteLine($"load tool CPU     {selfCpu / elapsed * 100:F0}% of one core (shares the machine if run on the server)");
            Console.WriteLine($"heard (others)    min {scores.Min(s => s.HeardFraction):P1}, mean {scores.Average(s => s.MeanPresence):P1} of frames");
            Console.WriteLine($"heard (self)      max {scores.Max(s => s.SelfPresence):P1} of frames (must be ~0: mix-minus)");
            Console.WriteLine($"leaked (other rooms) max {scores.Max(s => s.ForeignPresence):P1}");

            var ok = scores.All(s => s.HeardFraction >= 0.99 && s.SelfPresence < 0.02 && s.ForeignPresence < 0.02);
            Console.WriteLine(ok ? "RESULT: PASS" : "RESULT: FAIL");
            return new RunResult(engine, ok, serverCpu, after.Process.ProcessorCount, after.Process.WorkingSetBytes, after.Process.Threads,
                scores.Min(s => s.HeardFraction));
        }
        finally
        {
            foreach (var (client, _) in clients)
            {
                client.Dispose();
            }
            foreach (var room in rooms)
            {
                await http.DeleteAsync($"api/rooms/{room}");
            }
        }
    }

    /// <summary>
    /// HeardFraction: share of same-room others whose tone was present in more than half the frames.
    /// MeanPresence: average presence of those tones. Self/Foreign: worst presence of own tone / other rooms' tones
    /// (rooms reuse the same tones, so foreign leakage is only detectable as "too many" — reported for one room only).
    /// </summary>
    private static (double HeardFraction, double MeanPresence, double SelfPresence, double ForeignPresence) Score(
        (FakeClient Client, int Room) me, List<(FakeClient Client, int Room)> all)
    {
        var presence = me.Client.Meter.Presence();
        var others = all.Where(o => o.Room == me.Room && o.Client != me.Client).Select(o => o.Client.ToneHz).ToList();
        if (others.Count == 0)
        {
            return (1, 1, presence[me.Client.ToneHz], 0);
        }
        var heard = others.Count(t => presence[t] > 0.5) / (double)others.Count;
        var mean = others.Average(t => presence[t]);
        var foreign = presence.Where(p => p.Key != me.Client.ToneHz && !others.Contains(p.Key)).Select(p => p.Value).DefaultIfEmpty(0).Max();
        return (heard, mean, presence[me.Client.ToneHz], foreign);
    }

    private static async Task<T> Post<T>(HttpClient http, string path, object body)
    {
        using var response = await http.PostAsJsonAsync(path, body);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"POST {path}: {(int)response.StatusCode} {await response.Content.ReadAsStringAsync()}");
        }
        return (await response.Content.ReadFromJsonAsync<T>(Json))!;
    }

    private static async Task<StatusResponse> Status(HttpClient http) =>
        (await http.GetFromJsonAsync<StatusResponse>("api/status", Json))!;

    private sealed record Options(
        System.Uri Server, string ApiKey, int Participants, int Rooms, TimeSpan Duration, uint JitterBufferMs, bool Insecure,
        IReadOnlyList<string> Engines)
    {
        public const string Usage =
            "Usage: SpatialAudioServer.LoadTest --api-key KEY [--server http://host:5000] [--participants 4] [--rooms 1]\n" +
            "                                   [--duration 20] [--jitter-ms 100] [--insecure] [--engine a,b,...]\n" +
            "  The API key can also come from SAS_API_KEY. --insecure accepts the server's self-signed certificate.\n" +
            "  --engine runs the same load against each named mix engine in turn and prints a comparison\n" +
            "  (default: the server's default engine; GET /api/status lists them).";

        public static Options Parse(string[] args)
        {
            var server = new System.Uri("http://localhost:5000/");
            var key = Environment.GetEnvironmentVariable("SAS_API_KEY");
            int participants = 4, rooms = 1;
            var duration = TimeSpan.FromSeconds(20);
            uint jitter = 100;
            var insecure = false;
            var engines = new List<string>();
            for (var i = 0; i < args.Length; i++)
            {
                string Value() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"{args[i]} needs a value.");
                switch (args[i])
                {
                    case "--server": server = new System.Uri(Value().TrimEnd('/') + "/"); break;
                    case "--api-key": key = Value(); break;
                    case "--participants": participants = int.Parse(Value(), CultureInfo.InvariantCulture); break;
                    case "--rooms": rooms = int.Parse(Value(), CultureInfo.InvariantCulture); break;
                    case "--duration": duration = TimeSpan.FromSeconds(double.Parse(Value(), CultureInfo.InvariantCulture)); break;
                    case "--jitter-ms": jitter = uint.Parse(Value(), CultureInfo.InvariantCulture); break;
                    case "--insecure": insecure = true; break;
                    case "--engine": engines.AddRange(Value().Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)); break;
                    default: throw new ArgumentException($"Unknown argument {args[i]}.");
                }
            }
            if (string.IsNullOrEmpty(key))
            {
                throw new ArgumentException("An API key is required (--api-key or SAS_API_KEY).");
            }
            if (participants is < 1 or > 32 || rooms < 1)
            {
                throw new ArgumentException("--participants must be 1–32 and --rooms at least 1.");
            }
            return new Options(server, key, participants, rooms, duration, jitter, insecure, engines);
        }
    }
}
