using Gst;
using log4net;

namespace AudioMixerTest3
{
    public class IncomingAudio
    {
        private static readonly ILog _log = LogManager.GetLogger(typeof(IncomingAudio));

        public Guid Uuid { get; set; }
        public Element? AudioSrc { get; set; }
        public Element? AudioSrcCapsFilter { get; set; }
        public Element? AudioConvert { get; set; }
        public Element? AudioTee { get; set; }

        /// <summary>
        /// List of Uuids of participants who are receiving and mixing this audio.
        /// </summary>
        public List<Guid> Receivers { get; set; } = new List<Guid>();

        public bool IsReady { get; set; } = false;

        public bool IsStarted { get; set; } = false;

        public bool IsMuted { get; set; } = false;

        private Room? _room { get; set; }
        private Pipeline? _pipeline { get; set; }

        public IncomingAudio(Guid uuid)
        {
           Uuid = uuid;
        }

        public bool Create(Room room, int SourceType = SourceType.TestTone, string sourceLocation = "")
        {
            _room = room;
            _pipeline = room.Pipeline;
            _log.Debug($"{Uuid} incoming audio creation called. Type: {SourceType} Location: {sourceLocation}");
            try
            {
                if(_pipeline == null) throw new Exception("Room pipeline is null.");
                switch (SourceType)
                {
                    case global::AudioMixerTest3.SourceType.File:
                        AudioSrc = ElementFactory.Make("filesrc", $"audio-source-{Uuid}");
                        AudioSrc.SetProperty("location", new GLib.Value(sourceLocation));
                        break;
                    case global::AudioMixerTest3.SourceType.TestTone:
                        AudioSrc = ElementFactory.Make("audiotestsrc", $"audio-source-{Uuid}");
                        AudioSrc.SetProperty("freq", new GLib.Value(200 + new Random().Next(500)));
                        AudioSrc.SetProperty("volume", new GLib.Value(1.0));
                        break;
                    case global::AudioMixerTest3.SourceType.Rtp:
                        throw new NotImplementedException();
                    default:
                        throw new NotImplementedException();
                }

                AudioSrcCapsFilter = ElementFactory.Make("capsfilter", $"audio-src-caps-filter-{Uuid}");
                var capsMono = Caps.FromString("audio/x-raw,channels=1");
                AudioSrcCapsFilter.SetProperty("caps", new GLib.Value(capsMono));
                capsMono.Dispose();

                AudioConvert = ElementFactory.Make("audioconvert", $"audio-convert-{Uuid}");

                AudioTee = ElementFactory.Make("tee", $"audio-tee-{Uuid}");

                if(new[] {AudioSrc, AudioSrcCapsFilter, AudioConvert, AudioTee}.Any(e => e == null)) throw new Exception("Source elements creation failed.");

                _pipeline.Add(AudioSrc, AudioSrcCapsFilter, AudioConvert, AudioTee);

                if(!AudioSrc.Link(AudioSrcCapsFilter)) throw new Exception("AudioSrc.Link(AudioSrcCapsFilter) failed.");
                if(!AudioSrcCapsFilter.Link(AudioConvert)) throw new Exception("AudioSrcCapsFilter.Link(AudioConvert) failed.");
                if(!AudioConvert.Link(AudioTee)) throw new Exception("AudioConvert.Link(AudioTee) failed.");

            }
            catch (Exception ex)
            {
                _log.Error($"{Uuid} incoming audio creation failed.", ex);
                return false;
            }
            _log.Info($"{Uuid} incoming audio chain created.");
            Visualiser.Graph(_room, "IncomingAudio Create");
            IsReady = true;
            return true;
        }

        public void Destroy()
        {
            _log.Debug($"{Uuid} incoming audio destroy called.");
            if(!IsReady) return;
            if(IsStarted) return;
            if(_pipeline == null) return;

            AudioConvert!.Unlink(AudioTee);
            AudioSrcCapsFilter!.Unlink(AudioConvert);
            AudioSrc!.Unlink(AudioSrcCapsFilter);

            _pipeline.Remove(AudioSrc, AudioSrcCapsFilter, AudioConvert, AudioTee);

            AudioTee!.Dispose();
            AudioConvert!.Dispose();
            AudioSrcCapsFilter!.Dispose();
            AudioSrc!.Dispose();

            _log.Info($"{Uuid} incoming audio chain destroyed.");
            Visualiser.Graph(_room!, "IncomingAudio Destroy");
            IsReady = false;
            return;
        }

        public void Start()
        {
            _log.Debug($"{Uuid} incoming audio start called.");
            if(!IsReady) return;
            if(IsStarted) return;
            if(_pipeline == null) return;
            AudioSrc!.SetState(State.Playing);
            AudioSrcCapsFilter!.SetState(State.Playing);
            AudioConvert!.SetState(State.Playing);
            AudioTee!.SetState(State.Playing);
            IsStarted = true;
            _log.Info($"{Uuid} incoming audio chain started.");
        }

        public void Stop()
        {
            _log.Debug($"{Uuid} incoming audio stop called.");
            if(!IsReady) return;
            if(!IsStarted) return;
            if(_pipeline == null) return;

            AudioSrc!.SetState(State.Null);
            AudioSrcCapsFilter!.SetState(State.Null);
            AudioConvert!.SetState(State.Null);
            AudioTee!.SetState(State.Null);
            AudioTee.SendEvent(Gst.Event.NewEos());

            IsStarted = false;
            _log.Info($"{Uuid} incoming audio chain stopped.");
        }

        /// <summary>
        /// Adds a reference to a participant who is receiving and mixing this audio.
        /// Make sure the input pipleline elements are started.
        /// </summary>
        /// <param name="receiverUuid"></param>
        public void AddReceiver(Guid receiverUuid)
        {
            if(!Receivers.Contains(receiverUuid))
            {
                Receivers.Add(receiverUuid);
                _log.Info($"{Uuid} added receiver {receiverUuid}.");
            }
            if(Receivers.Count > 0)
            {
                //Start();
            }
        }

        /// <summary>
        /// Removes a reference to a participant who is receiving and mixing this audio.
        /// If there are no more receivers, the input pipeline elements are stopped.
        /// </summary>
        public void RemoveReceiver(Guid receiverUuid)
        {
            if(Receivers.Contains(receiverUuid))
            {
                Receivers.Remove(receiverUuid);
                _log.Info($"{Uuid} removed receiver {receiverUuid}.");
            }
            if(Receivers.Count == 0)
            {
                //Stop();
            }
        }
    }
}