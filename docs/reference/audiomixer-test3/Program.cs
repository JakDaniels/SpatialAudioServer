using System.Diagnostics;
using System.Runtime.InteropServices;
using Gst;
using log4net;
using log4net.Config;
namespace AudioMixerTest3
{
    partial class AudioMixerTest3
    {
        private static readonly ILog _log = LogManager.GetLogger(typeof(AudioMixerTest3));
        static void Main(string[] args)
        {
            // Console.WriteLine($"Launched from {Environment.CurrentDirectory}");
            // Console.WriteLine($"Physical location {AppDomain.CurrentDomain.BaseDirectory}");
            // Console.WriteLine($"AppContext.BaseDir {AppContext.BaseDirectory}");
            // Console.WriteLine($"Runtime Call {Path.GetDirectoryName(Process.GetCurrentProcess().MainModule!.FileName)}");
            // Environment.SetEnvironmentVariable("GST_DEBUG_DUMP_DOT_DIR", $"{Environment.CurrentDirectory}/graphs", EnvironmentVariableTarget.User);

            XmlConfigurator.Configure(new FileInfo($"{Environment.CurrentDirectory}/log4net.config"));
            _log.Info("AudioMixerTest3 started.");

            Application.Init(ref args);

            Gst.Debug.SetActive(true);
            Gst.Debug.SetDefaultThreshold(Gst.DebugLevel.Warning);

            var audioServer = new AudioServer(true, true);

            //Create a room
            var room1 = new Room(Guid.NewGuid(), "Room1");
            audioServer.Rooms.TryAdd(room1.RoomUuid, room1);

            //Put myself into the room
            var p1 = room1.AddParticipant(
                new AddParticipantRequest
                {
                    Uuid = audioServer.TestUuids[0],
                    Name = "Participant1",
                    PositionIsRandom = true,
                    SinkType = SinkType.AudioOut,
                    SourceType = SourceType.TestTone
                }
            );

            Thread.Sleep(2000);

            var p2 = room1.AddParticipant(
                new AddParticipantRequest
                {
                    Uuid = audioServer.TestUuids[1],
                    Name = "Participant2",
                    PositionIsRandom = true,
                    SinkType = SinkType.Null,
                    SourceType = SourceType.TestTone
                }
            );

            Thread.Sleep(5000);

            room1.RemoveParticipant(p1);

            Thread.Sleep(2000);

            room1.RemoveParticipant(p2);

            Thread.Sleep(2000);

            // room1.RemoveParticipant(p2);

            //add a new participant to the room and mix them in to my output
            // var p2 = new Participant(audioServer.TestUuids[1], "Participant2");
            // p2.CreateRandomPosition();
            // room1.AddParticipant(p2);
            // var pia2 = new ParticipantIncomingAudio(p2.Uuid);
            // pia2.Create(room1, AudioServerConstants.SourceTypes.TestTone);
            // poa1.AddParticipantToMix(pia2);
            // Console.WriteLine($"Participant2 created with Uuid {p2.Uuid}");


            // Thread.Sleep(2000);


            // //add a new participant to the room and mix them in to my output
            // var p3 = new Participant(audioServer.TestUuids[2], "Participant3");
            // p3.CreateRandomPosition();
            // room1.AddParticipant(p3);
            // var pia3 = new ParticipantIncomingAudio(p3.Uuid);
            // pia3.Create(room1, AudioServerConstants.SourceTypes.TestTone);
            // poa1.AddParticipantToMix(pia3);
            // Console.WriteLine($"Participant3 created with Uuid {p3.Uuid}");


            // Thread.Sleep(2000);


            // //remove participant2 from the mix and the room
            // poa1.RemoveParticipantFromMix(pia2);
            // pia2.Destroy();
            // room1.RemoveParticipant(p2);
            // Console.WriteLine($"Participant2 removed with Uuid {p2.Uuid}");


            // Thread.Sleep(2000);


            // room1.Stop();
            // Console.WriteLine("Room1 stopped.");

            room1.Dispose();
            audioServer.Rooms.TryRemove(room1.RoomUuid, out _);
            Console.WriteLine("Room1 removed.");

           
        }

        public static void VisualiseGraph(string name, string dotData)
        {
            var filename= $"{Environment.CurrentDirectory}/graphs/{name}";
            //save dotData to file:
            File.WriteAllText($"{filename}.dot", dotData);
            //run dot -Tpdf -o pipeline.pdf pipeline.dot
            var dot = "dot";
            //if windows use dot.exe
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                dot = "dot.exe";
            }
            var dotArgs = $"-Tpdf {filename}.dot -o{filename}.pdf";
            var dotProcess = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = dot,
                    Arguments = dotArgs,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };
            dotProcess.Start();
            dotProcess.WaitForExit();
        }
    }
}
