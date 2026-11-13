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

        if (options.Room is { } roomId)
        {
            return await JoinRoomAsync(http, options, roomId, logs);
        }

        var status = await Status(http);
        var engines = options.Engines.Count > 0 ? options.Engines
            : [options.Spatial ? status.DefaultSpatialEngine ?? status.DefaultEngine : status.DefaultEngine];
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

    /// <summary>
    /// Room mode: adds fake speakers to an existing room (for listening tests in a browser) and keeps them there until
    /// Ctrl+C or --duration. Only its own participants are removed on exit; the room stays.
    /// </summary>
    private static async Task<int> JoinRoomAsync(HttpClient http, Options options, Guid roomId, ILoggerFactory logs)
    {
        using var room = await http.GetAsync($"api/rooms/{roomId}");
        if (!room.IsSuccessStatusCode)
        {
            Console.Error.WriteLine($"Room {roomId}: {(int)room.StatusCode} {await room.Content.ReadAsStringAsync()}");
            return 2;
        }
        var info = await room.Content.ReadFromJsonAsync<JsonElement>(Json);

        var clips = new List<AudioClip>();
        var n = options.Participants;
        foreach (var file in options.AudioFiles)
        {
            if (file.ToLowerInvariant() is "flite" or "flite-together")
            {
                var together = file.Equals("flite-together", StringComparison.OrdinalIgnoreCase);
                // One phrase per bot: its number, and with --spatial where it stands (see the circle below).
                var phrases = Enumerable.Range(0, n)
                    .Select(i => options.Spatial ? $"Bot {i}. {Spoken(2 * Math.PI * i / n)}." : $"Bot {i}.")
                    .ToList();
                var voices = AudioClip.Speech(phrases, together);
                clips.AddRange(voices);
                Console.WriteLine($"Audio: {voices.Count} flite voices {(together ? "speaking together" : "taking turns")}, " +
                    $"a {voices[0].Length.TotalSeconds:F1} s cycle, e.g. {voices[^1].Path}");
                continue;
            }
            var clip = AudioClip.Load(file);
            clips.Add(clip);
            Console.WriteLine($"Audio {clip.Path}: {clip.Length.TotalSeconds:F1} s");
        }

        var talkers = Math.Min(options.Talkers ?? n, n);
        var volume = options.Volume ?? (clips.Count > 0 ? 1.0 : 0.1);
        var tones = Enumerable.Range(0, n).Select(i => 300.0 + 50 * i).ToArray();
        Console.WriteLine($"Joining room \"{info.GetProperty("name").GetString()}\" ({roomId}) on {options.Server} with {n} " +
            $"participant(s), {talkers} talking ({(clips.Count > 0 ? "audio" : "tones")}) at volume {volume:F2}");

        var roomSpatial = info.TryGetProperty("spatial", out var flag) && flag.GetBoolean();
        if (options.Spatial && !roomSpatial)
        {
            Console.WriteLine("Note: the room is not spatial, so positions are ignored (create it with Spatial ticked).");
        }

        using var stop = new CancellationTokenSource();
        ConsoleCancelEventHandler onCancel = (_, e) =>
        {
            e.Cancel = true;
            stop.Cancel();
        };
        Console.CancelKeyPress += onCancel;
        var clients = new List<FakeClient>();
        try
        {
            for (var i = 0; i < n && !stop.IsCancellationRequested; i++)
            {
                var name = $"{options.Name}-{i}"; // from 0, matching what the flite voices say
                // --spatial: a circle round the centre (where the browser client stands), bot-0 straight ahead (east)
                // and the rest spaced anticlockwise, i.e. towards the listener's left first.
                var angle = 2 * Math.PI * i / n;
                var position = options.Spatial
                    ? new Vec3(Math.Round(options.Radius * Math.Cos(angle), 3), Math.Round(options.Radius * Math.Sin(angle), 3), 0)
                    : null;
                var join = await Post<JoinResponse>(http, $"api/rooms/{roomId}/participants", new { displayName = name, position });
                // Each client of a shared clip starts at a different point so they don't speak in unison.
                var clip = clips.Count > 0 ? clips[i % clips.Count] : null;
                var sharing = clips.Count > 0 ? (n + clips.Count - 1) / clips.Count : 1;
                var offset = clip is null ? 0 : (int)((long)clip.Pcm.Length * (i / Math.Max(1, clips.Count)) / sharing);
                var client = new FakeClient(name, tones[i], tones, options.JitterBufferMs, logs.CreateLogger("fake"), volume,
                    talking: i < talkers, clip: clip, clipOffsetBytes: offset);
                clients.Add(client);
                await client.ConnectAsync(http, join.ParticipantId, join.JoinToken);
                Console.WriteLine($"  {name} joined ({join.ParticipantId}){(i < talkers ? "" : ", silent")}" +
                    (position is null ? "" : $", {options.Radius:0.#} m {Direction(angle)}"));
            }

            Console.WriteLine(options.DurationSet
                ? $"Running for {options.Duration.TotalSeconds:F0} s (Ctrl+C to leave early)"
                : "Running until Ctrl+C");
            var until = options.DurationSet ? DateTime.UtcNow + options.Duration : DateTime.MaxValue;
            var reported = new HashSet<FakeClient>();
            for (var tick = 1; !stop.IsCancellationRequested && DateTime.UtcNow < until; tick++)
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(1), stop.Token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                foreach (var gone in clients.Where(c => c.Removed && reported.Add(c)))
                {
                    Console.WriteLine($"  {gone.Name} was removed by the server");
                }
                if (clients.All(c => c.Removed))
                {
                    using var check = await http.GetAsync($"api/rooms/{roomId}");
                    Console.WriteLine(check.StatusCode == System.Net.HttpStatusCode.NotFound
                        ? "The room was deleted; exiting."
                        : "Every bot was removed from the room; exiting.");
                    return 0;
                }
                if (tick % 10 == 0)
                {
                    var active = clients.Where(c => !c.Removed).ToList();
                    var failed = active.Where(c => c.IceError is not null).Select(c => c.Name).ToList();
                    Console.WriteLine($"  {active.Count(c => c.AudioFlowing)}/{active.Count} receiving audio" +
                        (failed.Count > 0 ? $"; ICE exchange failed: {string.Join(", ", failed)}" : ""));
                }
            }
            return 0;
        }
        finally
        {
            Console.CancelKeyPress -= onCancel;
            Console.WriteLine("Leaving…");
            foreach (var client in clients)
            {
                if (client.Removed)
                {
                    client.Dispose(); // already gone on the server
                    continue;
                }
                try
                {
                    await client.LeaveAsync(http);
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"  {client.Name}: leave failed ({ex.Message}); removing it via the control API");
                    await http.DeleteAsync($"api/rooms/{roomId}/participants/{client.ParticipantId}");
                }
                client.Dispose();
            }
        }
    }

    /// <summary>What a bot at this angle (anticlockwise from east) says about where it is: compass, then relative to a
    /// listener facing east (the browser client's starting direction).</summary>
    private static string Spoken(double angle)
    {
        string[] compass = ["East", "North-east", "North", "North-west", "West", "South-west", "South", "South-east"];
        string[] relative = ["ahead of you", "ahead, to your left", "on your left", "behind, to your left", "behind you",
            "behind, to your right", "on your right", "ahead, to your right"];
        var octant = (int)Math.Round(angle / (Math.PI / 4)) % 8;
        return $"{compass[octant]}, {relative[octant]}";
    }

    /// <summary>Where a bot at this angle (anticlockwise from east) is for a listener facing east.</summary>
    private static string Direction(double angle)
    {
        var degrees = (int)Math.Round(angle * 180 / Math.PI) % 360;
        string[] names = ["ahead (east)", "ahead-left (north-east)", "left (north)", "behind-left (north-west)", "behind (west)",
            "behind-right (south-west)", "right (south)", "ahead-right (south-east)"];
        return degrees % 45 == 0 ? names[degrees / 45] : $"at {degrees}° anticlockwise from ahead";
    }

    private sealed record RunResult(string Engine, bool Pass, double CpuCores, int Cores, long MemoryBytes, int Threads, double HeardMin);

    private static async Task<RunResult> RunAsync(HttpClient http, Options options, string engine, ILoggerFactory logs)
    {
        var n = options.Participants;
        var talkers = Math.Min(options.Talkers ?? n, n);
        var volume = Math.Min(0.1, 0.6 / Math.Max(1, talkers)); // keep the sum of the tones heard under full scale
        var tones = Enumerable.Range(0, n).Select(i => 300.0 + 50 * i).ToArray(); // exact Goertzel bins at 20 ms
        Console.WriteLine($"Server {options.Server}, engine {engine}{(options.Spatial ? ", spatial" : "")}: {options.Rooms} room(s) x {n} " +
            $"participants ({talkers} talking), tone volume {volume:F3}");
        // Spatial: everyone somewhere in a 30 m square facing anywhere (few listeners share a rendering, so close to
        // the worst case for clustering), all within range; distant talkers are quieter, so the meter listens lower.
        var random = new Random(1234);

        var rooms = new List<Guid>();
        var clients = new List<(FakeClient Client, int Room)>();
        try
        {
            for (var r = 0; r < options.Rooms; r++)
            {
                var room = await Post<RoomSnapshot>(http, "api/rooms",
                    new { name = $"loadtest-{engine}-{r}", maxParticipants = n, engine, spatial = options.Spatial });
                rooms.Add(room.Id);
                for (var i = 0; i < n; i++)
                {
                    var yaw = random.NextDouble() * Math.PI; // half-angle for the quaternion about z
                    var join = await Post<JoinResponse>(http, $"api/rooms/{room.Id}/participants", options.Spatial
                        ? new
                        {
                            displayName = $"load-{r}-{i}",
                            position = (object)new Vec3(random.NextDouble() * 30, random.NextDouble() * 30, 22),
                            orientation = (object)new Quat(0, 0, Math.Sin(yaw), Math.Cos(yaw)),
                        }
                        : new { displayName = $"load-{r}-{i}", position = (object)null!, orientation = (object)null! });
                    var client = new FakeClient($"r{r}p{i}", tones[i], tones, options.JitterBufferMs, logs.CreateLogger("fake"), volume,
                        talking: i < talkers, threshold: options.Spatial ? volume / 10 : null); // codec artefacts reach ~volume/20
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
            // Warm-up: engines finish building after everyone connects (matrix-shared finds its talkers, builds their
            // personal mixes and splits listeners into clusters) and .NET is still compiling hot paths. Measuring
            // straight away counted that one-off work as steady-state CPU.
            Console.WriteLine($"Settled in {settle.Elapsed.TotalSeconds:F1} s; warming up for {options.Warmup.TotalSeconds:F0} s, " +
                $"then measuring for {options.Duration.TotalSeconds:F0} s");
            await Task.Delay(options.Warmup);

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
            if (await MediaStats(http, rooms[0]) is { } stats)
            {
                Console.WriteLine($"room 0 media      {string.Join(", ", stats.Counters.Select(c => $"{c.Key} {c.Value}"))}");
            }
            Console.WriteLine($"heard (others)    min {scores.Min(s => s.HeardFraction):P1}, mean {scores.Average(s => s.MeanPresence):P1} of frames");
            Console.WriteLine($"heard (self)      max {scores.Max(s => s.SelfPresence):P1} of frames (must be ~0: mix-minus)");
            Console.WriteLine($"leaked (other rooms) max {scores.Max(s => s.ForeignPresence):P1}");

            var ok = scores.All(s => s.HeardFraction >= 0.99 && s.SelfPresence < 0.02 && s.ForeignPresence < 0.02);
            if (!ok)
            {
                // Who failed and what they heard: tone → presence (and mean L/R amplitude).
                foreach (var (client, room) in clients.Where((c, i) =>
                    scores[i] is var s && !(s.HeardFraction >= 0.99 && s.SelfPresence < 0.02 && s.ForeignPresence < 0.02)))
                {
                    var levels = client.Meter.Levels();
                    Console.WriteLine($"  {client.Name} ({client.ToneHz} Hz, {(client.Talking ? "talking" : "silent")}) hears " +
                        string.Join(", ", client.Meter.Presence().Where(p => p.Value > 0.02)
                            .Select(p => $"{p.Key}:{p.Value:P0} (L {levels[p.Key].Left:F3} R {levels[p.Key].Right:F3})")));
                }
            }
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
        // Expected: every other talker in the same room.
        var others = all.Where(o => o.Room == me.Room && o.Client != me.Client && o.Client.Talking).Select(o => o.Client.ToneHz).ToList();
        if (others.Count == 0)
        {
            return (1, 1, presence[me.Client.ToneHz], 0);
        }
        var heard = others.Count(t => presence[t] > 0.5) / (double)others.Count;
        var mean = others.Average(t => presence[t]);
        // A tone that is an exact multiple of a talker's (600 Hz = 2 × 300 Hz) also catches that talker's codec
        // harmonics, faint but above the low spatial threshold: it cannot show a leak, so it is not judged.
        var talkers = all.Where(o => o.Room == me.Room && o.Client.Talking).Select(o => o.Client.ToneHz).ToList();
        bool Harmonic(double hz) => talkers.Any(t => hz > t && Math.Abs(hz / t - Math.Round(hz / t)) < 1e-9);
        var foreign = presence.Where(p => p.Key != me.Client.ToneHz && !others.Contains(p.Key) && !Harmonic(p.Key))
            .Select(p => p.Value).DefaultIfEmpty(0).Max();
        var self = Harmonic(me.Client.ToneHz) ? 0 : presence[me.Client.ToneHz];
        return (heard, mean, self, foreign);
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

    /// <summary>The room's engine counters (encoders, clusters…), or null from a server without the endpoint.</summary>
    private static async Task<RoomMediaStats?> MediaStats(HttpClient http, Guid roomId)
    {
        using var response = await http.GetAsync($"api/rooms/{roomId}/media");
        return response.IsSuccessStatusCode ? await response.Content.ReadFromJsonAsync<RoomMediaStats>(Json) : null;
    }

    private static async Task<StatusResponse> Status(HttpClient http) =>
        (await http.GetFromJsonAsync<StatusResponse>("api/status", Json))!;

    private sealed record Options(
        System.Uri Server, string ApiKey, int Participants, int Rooms, TimeSpan Duration, uint JitterBufferMs, bool Insecure,
        IReadOnlyList<string> Engines, int? Talkers, Guid? Room, IReadOnlyList<string> AudioFiles, double? Volume, string Name,
        bool DurationSet, bool Spatial, double Radius, TimeSpan Warmup)
    {
        public const string Usage =
            "Usage: SpatialAudioServer.LoadTest --api-key KEY [--server http://host:5000] [--participants 4] [--rooms 1]\n" +
            "                                   [--duration 20] [--warmup 10] [--jitter-ms 100] [--insecure] [--engine a,b,...]\n" +
            "                                   [--talkers K] [--spatial]\n" +
            "  The API key can also come from SAS_API_KEY. --insecure accepts the server's self-signed certificate.\n" +
            "  --engine runs the same load against each named mix engine in turn and prints a comparison\n" +
            "  (default: the server's default engine; GET /api/status lists them).\n" +
            "  --talkers K: only the first K clients in each room send their tone (default: all); the rest send silence.\n" +
            "  --spatial: spatial rooms, everyone placed at random in a 30 m square facing a random way.\n" +
            "\n" +
            "Room mode (listening tests): SpatialAudioServer.LoadTest --api-key KEY --room ROOM_ID [--participants 4]\n" +
            "                                   [--audio speech.wav[,other.ogg...]] [--talkers K] [--volume 1.0] [--name bot]\n" +
            "                                   [--duration SECONDS]\n" +
            "  Adds participants to an existing room (e.g. one you are in with the browser client) and keeps them there\n" +
            "  until Ctrl+C or --duration, then removes them; the room is left alone. They send a tone each, or loop the\n" +
            "  --audio files (any format GStreamer decodes; shared round-robin, each client starting at a different point).\n" +
            "  --audio flite: synthesised speech instead of a file (flitetestsrc): bot-0 says \"zero, …\", bot-1 \"one, …\"\n" +
            "  and so on (up to 8 voices), taking turns. --audio flite-together: the same, all speaking at once.\n" +
            "  --volume scales what they send (default 1.0 for audio files, 0.1 for tones).\n" +
            "  --spatial (spatial rooms): bots stand in a circle of --radius metres (default 5) round the centre, where the\n" +
            "  browser client puts you: bot-0 ahead (east), the rest spaced anticlockwise.";

        public static Options Parse(string[] args)
        {
            var server = new System.Uri("http://localhost:5000/");
            var key = Environment.GetEnvironmentVariable("SAS_API_KEY");
            int participants = 4, rooms = 1;
            var duration = TimeSpan.FromSeconds(20);
            uint jitter = 100;
            var insecure = false;
            var engines = new List<string>();
            int? talkers = null;
            Guid? room = null;
            var audio = new List<string>();
            double? volume = null;
            var name = "bot";
            var durationSet = false;
            var spatial = false;
            var radius = 5.0;
            var warmup = TimeSpan.FromSeconds(10);
            for (var i = 0; i < args.Length; i++)
            {
                string Value() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"{args[i]} needs a value.");
                switch (args[i])
                {
                    case "--server": server = new System.Uri(Value().TrimEnd('/') + "/"); break;
                    case "--api-key": key = Value(); break;
                    case "--participants": participants = int.Parse(Value(), CultureInfo.InvariantCulture); break;
                    case "--rooms": rooms = int.Parse(Value(), CultureInfo.InvariantCulture); break;
                    case "--duration": duration = TimeSpan.FromSeconds(double.Parse(Value(), CultureInfo.InvariantCulture)); durationSet = true; break;
                    case "--room": room = Guid.TryParse(Value(), out var id) ? id : throw new ArgumentException("--room needs a room ID (a UUID)."); break;
                    case "--audio": audio.AddRange(Value().Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)); break;
                    case "--volume": volume = double.Parse(Value(), CultureInfo.InvariantCulture); break;
                    case "--name": name = Value(); break;
                    case "--spatial": spatial = true; break;
                    case "--radius": radius = double.Parse(Value(), CultureInfo.InvariantCulture); break;
                    case "--warmup": warmup = TimeSpan.FromSeconds(double.Parse(Value(), CultureInfo.InvariantCulture)); break;
                    case "--jitter-ms": jitter = uint.Parse(Value(), CultureInfo.InvariantCulture); break;
                    case "--insecure": insecure = true; break;
                    case "--talkers": talkers = int.Parse(Value(), CultureInfo.InvariantCulture); break;
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
            if (talkers is < 0)
            {
                throw new ArgumentException("--talkers must be 0 or more.");
            }
            if (audio.Count > 0 && room is null)
            {
                throw new ArgumentException("--audio needs --room: the load test measures tones, so it can't use audio files.");
            }
            if (room is not null && engines.Count > 0)
            {
                throw new ArgumentException("--engine can't be used with --room: the room already has its engine.");
            }
            if (volume is < 0 or > 10)
            {
                throw new ArgumentException("--volume must be between 0 and 10.");
            }
            return new Options(server, key, participants, rooms, duration, jitter, insecure, engines, talkers, room, audio, volume, name,
                durationSet, spatial, radius, warmup);
        }
    }
}
