using Gst;
using log4net;

namespace WebRtcTest1
{
    public partial class GStreamer
    {
        private static readonly ILog _log = LogManager.GetLogger(typeof(GStreamer));
        public bool CheckPlugins()
        {
            var plugins = new[]
            {
                "opus",
                // "vpx",
                "webrtc",
                "nice",
                "dtls",
                "srtp",
                // "rtp",
                "rtpmanager",
                "videotestsrc",
                "audiotestsrc",
                // "audioconvert",
                // "audioresample",
                // "autodetect"
            };
            var registry = Registry.Get();
            bool ret = true;
            foreach (var plugin in plugins)
            {
                var pluginFound = registry.FindPlugin(plugin);
                if (pluginFound == null)
                {
                    _log.Error($"Plugin {plugin} not found.");
                    ret = false;
                    continue;
                }
                pluginFound.Unref();
            }
            return ret;
        }
    }
}