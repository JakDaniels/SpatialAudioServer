using System.Diagnostics;
using System.Runtime.InteropServices;
using Gst;

namespace AudioMixerTest3
{
    public static class Visualiser
    {
        public static void Graph(Room room, string subTitle = "")
        {
            var name = $"room-{room.RoomUuid}-({room.RoomName})";
            if(!string.IsNullOrWhiteSpace(subTitle)) subTitle += "\n";
            var data = Gst.Debug.BinToDotData(room.Pipeline, DebugGraphDetails.All);
            data = data.Replace("label=\"<GstPipeline>", $"label=\"{subTitle}<GstPipeline>");
            var date = System.DateTime.Now.ToString("yyyy-MM-dd-HH-mm-ss-fff");
            var filename= $"{Environment.CurrentDirectory}/graphs/{name}-{date}";
            
            File.WriteAllText($"{filename}.dot", data);
            
            var dot = "dot";
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
            File.Delete($"{filename}.dot");
        }

        public static void RemoveAllGraphs()
        {
            var files = Directory.GetFiles($"{Environment.CurrentDirectory}/graphs", "*.pdf");
            foreach(var file in files)
            {
                File.Delete(file);
            }
        }
    }
}