using Gst;
using log4net;

namespace AudioMixerTest3
{
    public class AudioMixer
    {
        private static readonly ILog _log = LogManager.GetLogger(typeof(AudioMixer));

        public Guid Uuid { get; set; }

        //Audio input points uses the Guid of the incoming audio
        public Dictionary<Guid, Element?> AudioTees { get; set; } = new Dictionary<Guid, Element?>();

        //Intermediate mixer elements.
        //Their keys are the Guid of the incoming audio and the outgoing audio
        //e.g. guid_guid

        public Dictionary<string, Pad> AudioTeeSrcPads { get; set; } = new Dictionary<string, Pad>();
        public Dictionary<string, Element> AudioQueues { get; set; } = new Dictionary<string, Element>();
        public Dictionary<string, Element> AudioGains { get; set; } = new Dictionary<string, Element>();
        public Dictionary<string, Element> AudioPans { get; set; } = new Dictionary<string, Element>();
        public Dictionary<string, Pad> FinalMixerSinkPads { get; set; } = new Dictionary<string, Pad>();

        public Dictionary<string, bool> IsReady { get; set; } = new Dictionary<string, bool>();
        public Dictionary<string, bool> IsStarted { get; set; } = new Dictionary<string, bool>();

        //Audio output points uses the Guid of the outgoing audio
        public Dictionary<Guid, Element?> FinalMixers { get; set; } = new Dictionary<Guid, Element?>();

        private Room _room { get; set; }
       
        public AudioMixer(Room room, Guid uuid)
        {
            _room = room;
            Uuid = uuid;
        }

        public bool Create(IncomingAudio incomingAudio, OutgoingAudio outgoingAudio)
        {
            var srcUuid = incomingAudio.Uuid;
            var sinkUuid = outgoingAudio.Uuid;
            var mixKey = $"{srcUuid.ToString()}_{sinkUuid.ToString()}";
            _log.Debug($"{mixKey} Create called.");
            try
            {
                if(_room.Pipeline == null) throw new Exception("Room pipeline is null.");

                if(AudioQueues!.ContainsKey(mixKey)) throw new Exception("AudioQueue already exists.");
                if(AudioGains!.ContainsKey(mixKey))  throw new Exception("AudioGain already exists.");
                if(AudioPans!.ContainsKey(mixKey))   throw new Exception("AudioPan already exists.");

                var audioQueue = ElementFactory.Make("queue", $"audio-queue-{mixKey}");
                var audioGain = ElementFactory.Make("audioamplify", $"audio-gain-{mixKey}");
                var audioPan = ElementFactory.Make("audiopanorama", $"audio-pan-{mixKey}");

                if(new[] {audioQueue, audioGain, audioPan}.Any(e => e == null))
                    throw new Exception("Mix elements creation failed!");

                AudioQueues.Add(mixKey, audioQueue);
                AudioGains.Add(mixKey, audioGain);
                AudioPans.Add(mixKey, audioPan);

                _room.Pipeline.Add(audioQueue, audioGain, audioPan);

                if(!audioQueue.Link(audioGain)) throw new Exception($"{srcUuid}: audioQueue.Link(audioGain) failed.");
                if(!audioGain.Link(audioPan))   throw new Exception($"{srcUuid}: audioGain.Link(audioPan) failed.");

                if(IsReady.ContainsKey(mixKey)) IsReady[mixKey] = true;
                else IsReady.Add(mixKey, true);

            }
            catch (Exception e)
            {
                _log.Error($"{mixKey} Create failed.", e);
                if(IsReady.ContainsKey(mixKey)) IsReady[mixKey] = false;
                else IsReady.Add(mixKey, false);
                return false;
            }

            _log.Info($"{mixKey} created.");
            Visualiser.Graph(_room, "AudioMixer Create");
            return true;
        }

        public bool Destroy(IncomingAudio incomingAudio, OutgoingAudio outgoingAudio)
        {
            var srcUuid = incomingAudio.Uuid;
            var sinkUuid = outgoingAudio.Uuid;
            var mixKey = $"{srcUuid.ToString()}_{sinkUuid.ToString()}";
            _log.Debug($"{mixKey} Destroy called.");
            try
            {
                if(!IsReady.ContainsKey(mixKey) || !IsReady[mixKey]) throw new Exception("Mixer is not ready.");
                if(_room.Pipeline == null) throw new Exception("Room pipeline is null.");

                if(!AudioQueues!.ContainsKey(mixKey)) throw new Exception($"AudioQueue not found.");
                if(!AudioGains!.ContainsKey(mixKey))  throw new Exception($"AudioGain not found.");
                if(!AudioPans!.ContainsKey(mixKey))   throw new Exception($"AudioPan not found.");

                var audioQueue = AudioQueues[mixKey];
                var audioGain = AudioGains[mixKey];
                var audioPan = AudioPans[mixKey];

                if(audioQueue.SetState(State.Null) != StateChangeReturn.Success) _log.Warn($"{mixKey}: audioQueue.SetState(State.Null) failed.");
                if(audioGain.SetState(State.Null) != StateChangeReturn.Success) _log.Warn($"{mixKey}: audioGain.SetState(State.Null) failed.");
                if(audioPan.SetState(State.Null) != StateChangeReturn.Success) _log.Warn($"{mixKey}: audioPan.SetState(State.Null) failed.");

                audioQueue.Unlink(audioGain);
                audioGain.Unlink(audioPan);

                _room.Pipeline.Remove(audioQueue, audioGain, audioPan);

                audioQueue.Dispose();
                audioGain.Dispose();
                audioPan.Dispose();

                AudioQueues.Remove(mixKey);
                AudioGains.Remove(mixKey);
                AudioPans.Remove(mixKey);

                if(IsReady.ContainsKey(mixKey)) IsReady[mixKey] = false;
                else IsReady.Add(mixKey, false);
            }
            catch (Exception e)
            {
                _log.Error($"{mixKey} Destroy failed.", e);
                return false;
            }

            _log.Info($"{mixKey} destroyed.");
            Visualiser.Graph(_room, "AudioMixer Destroy");
            return true;
        }

        public bool Start(IncomingAudio incomingAudio, OutgoingAudio outgoingAudio)
        {
            var srcUuid = incomingAudio.Uuid;
            var sinkUuid = outgoingAudio.Uuid;
            var mixKey = $"{srcUuid.ToString()}_{sinkUuid.ToString()}";
            _log.Debug($"{mixKey} Start called.");
            try
            {
                if(!IsReady.ContainsKey(mixKey) || !IsReady[mixKey]) throw new Exception("Mixer is not ready.");
                if(_room.Pipeline == null) throw new Exception("Room pipeline is null.");

                if(!AudioQueues!.ContainsKey(mixKey)) throw new Exception($"AudioQueue not found.");
                if(!AudioGains!.ContainsKey(mixKey))  throw new Exception($"AudioGain not found.");
                if(!AudioPans!.ContainsKey(mixKey))   throw new Exception($"AudioPan not found.");

                var audioQueue = AudioQueues[mixKey];
                var audioGain = AudioGains[mixKey];
                var audioPan = AudioPans[mixKey];

                //and our mix elements can be started as well
                audioQueue.SetState(State.Playing);
                audioGain.SetState(State.Playing);
                audioPan.SetState(State.Playing);

                if(IsStarted.ContainsKey(mixKey)) IsStarted[mixKey] = true;
                else IsStarted.Add(mixKey, true);
            }
            catch (Exception e)
            {
                _log.Error($"{mixKey} Start failed.", e);
                if(IsStarted.ContainsKey(mixKey)) IsStarted[mixKey] = false;
                else IsStarted.Add(mixKey, false);
                return false;
            }

            _log.Info($"{mixKey} started.");
            return true;
        }

        public bool Stop(IncomingAudio incomingAudio, OutgoingAudio outgoingAudio)
        {
            var srcUuid = incomingAudio.Uuid;
            var sinkUuid = outgoingAudio.Uuid;
            var mixKey = $"{srcUuid.ToString()}_{sinkUuid.ToString()}";
            _log.Debug($"{mixKey} Stop called.");
            try
            {
                if(!IsReady.ContainsKey(mixKey) || !IsReady[mixKey]) throw new Exception("Mixer is not ready.");
                if(_room.Pipeline == null) throw new Exception("Room pipeline is null.");

                if(!AudioQueues!.ContainsKey(mixKey)) throw new Exception($"AudioQueue not found.");
                if(!AudioGains!.ContainsKey(mixKey))  throw new Exception($"AudioGain not found.");
                if(!AudioPans!.ContainsKey(mixKey))   throw new Exception($"AudioPan not found.");

                var audioQueue = AudioQueues[mixKey];
                var audioGain = AudioGains[mixKey];
                var audioPan = AudioPans[mixKey];

                audioQueue.SetState(State.Null);
                audioGain.SetState(State.Null);
                audioPan.SetState(State.Null);
                
                if(IsStarted.ContainsKey(mixKey)) IsStarted[mixKey] = false;
                else IsStarted.Add(mixKey, false);
            }
            catch (Exception e)
            {
                _log.Error($"{mixKey} Stop failed.", e);
                return false;
            }
            _log.Info($"{mixKey} stopped.");
            return true;
        }

        public bool SetGain(Guid srcUuid, Guid sinkUuid, float gain)
        {
            var mixKey = $"{srcUuid.ToString()}_{sinkUuid.ToString()}";
            _log.Debug($"{mixKey} Gain: {gain} called.");
            try
            {
                if(!AudioGains!.ContainsKey(mixKey)) throw new Exception($"AudioGain not found.");
                var audioGain = AudioGains[mixKey];
                audioGain.SetProperty("amplification", new GLib.Value(gain));
            }
            catch (Exception e)
            {
                _log.Error($"{mixKey} Gain: {gain} failed!", e);
                return false;
            }
            _log.Info($"{mixKey} Gain: {gain} set.");
            return true;
        }

        public bool SetPan(Guid srcUuid, Guid sinkUuid, float pan)
        {
            var mixKey = $"{srcUuid.ToString()}_{sinkUuid.ToString()}";
            _log.Debug($"{mixKey} Pan: {pan} called.");
            try
            {
                if(!AudioPans!.ContainsKey(mixKey)) throw new Exception($"AudioPan not found.");
                var audioPan = AudioPans[mixKey];
                audioPan.SetProperty("panorama", new GLib.Value(pan));
            }
            catch (Exception e)
            {
                _log.Error($"{mixKey} Pan: {pan} failed!", e);
                return false;
            }
            _log.Info($"{mixKey} Pan: {pan} set.");
            return true;
        }

        public bool Add(IncomingAudio incomingAudio, OutgoingAudio outgoingAudio)
        {
            
            var srcUuid = incomingAudio.Uuid;
            var sinkUuid = outgoingAudio.Uuid;
            var mixKey = $"{srcUuid.ToString()}_{sinkUuid.ToString()}";
            _log.Debug($"{mixKey} Add to mix called.");
            try
            {
                if(!IsReady.ContainsKey(mixKey) || !IsReady[mixKey]) throw new Exception("Mixer is not ready.");
                if(_room.Pipeline == null) throw new Exception("Room pipeline is null.");

                if(incomingAudio.AudioTee == null) throw new Exception("Incoming audio tee not found.");
                if(AudioTees!.ContainsKey(srcUuid)) AudioTees[srcUuid] = incomingAudio.AudioTee;
                else AudioTees.Add(srcUuid, incomingAudio.AudioTee);

                if(outgoingAudio.FinalMixer == null) throw new Exception("Outgoing audio mixer not found.");
                if(FinalMixers!.ContainsKey(sinkUuid)) FinalMixers[sinkUuid] = outgoingAudio.FinalMixer;
                else FinalMixers.Add(sinkUuid, outgoingAudio.FinalMixer);

                // Get a pad on the output mixer of the sink participant
                if(!FinalMixers!.ContainsKey(sinkUuid))    throw new Exception("FinalMixer not found.");
                var finalMixer = FinalMixers![sinkUuid] ?? throw new Exception("FinalMixer not found.");
                var finalMixerPad = finalMixer!.GetRequestPad("sink_%u") ?? throw new Exception("FinalMixer sink pad not found.");
                 
                 //Get a pad from the output of the mixer for the source participant
                if(!AudioPans!.ContainsKey(mixKey)) throw new Exception("AudioPan not found.");
                var audioPan = AudioPans[mixKey] ??   throw new Exception("AudioPan not found.");
                var audioPanPad = audioPan.GetStaticPad("src") ?? throw new Exception("AudioPan src pad not found.");

                //Get an input pin on the queue input of the mixer for the source participant
                if(!AudioQueues!.ContainsKey(mixKey)) throw new Exception("AudioQueue not found.");
                var audioQueue = AudioQueues[mixKey] ?? throw new Exception("AudioQueue not found.");
                var audioQueuePad = audioQueue.GetStaticPad("sink") ?? throw new Exception("AudioQueue sink pad not found.");

                //Get an output pin on the source participant's audio input tee
                if(!AudioTees!.ContainsKey(srcUuid)) throw new Exception("AudioTee not found.");
                var audioTee = AudioTees[srcUuid] ?? throw new Exception("AudioTee not found.");
                var audioTeePad = AudioTees[srcUuid]!.GetRequestPad("src_%u") ?? throw new Exception("AudioTee src pad not found.");

                //Link the output of the source participant's mixer to the input of the sink participant's mixer
                if(audioPanPad.Link(finalMixerPad) != PadLinkReturn.Ok) throw new Exception($"audioPanPad.Link(finalMixerPad) failed.");
                // keep a record of this new mixer dynamic sink pad so we can release it later

                FinalMixerSinkPads!.TryAdd(mixKey, finalMixerPad);

                //Link the output of the source participant's audio tee to the input of their mixer
                if(audioTeePad.Link(audioQueuePad) != PadLinkReturn.Ok) throw new Exception($"audioTeePad.Link(audioQueuePad) failed.");
                // keep a record of this input tee src pad so we can release it later
                AudioTeeSrcPads!.TryAdd(mixKey, audioTeePad);

                if(srcUuid == sinkUuid)
                {   //our own input audio is always muted (gain = 0.00) in our output mix
                    SetGain(srcUuid, sinkUuid, 0.1f);
                    SetPan(srcUuid, sinkUuid, -1.0f);
                }
            }
            catch (Exception e)
            {
                _log.Error($"{mixKey} Add failed!", e);
                return false;
            }
            _log.Info($"{mixKey} Add succeeded.");
            Visualiser.Graph(_room, "AudioMixer Add");
            return true;
        }

        public bool Remove(IncomingAudio incomingAudio, OutgoingAudio outgoingAudio)
        {
            var srcUuid = incomingAudio.Uuid;
            var sinkUuid = outgoingAudio.Uuid;
            var mixKey = $"{srcUuid.ToString()}_{sinkUuid.ToString()}";
            _log.Debug($"{mixKey} Remove from mix called.");
            try
            {
                if(!IsReady.ContainsKey(mixKey) || !IsReady[mixKey]) throw new Exception("Mixer is not ready.");
                if(_room.Pipeline == null) throw new Exception("Room pipeline is null.");

                if(!AudioTees!.ContainsKey(srcUuid))            throw new Exception("AudioTee not found.");
                var audioTee = AudioTees[srcUuid] ??            throw new Exception("AudioTee not found.");
                if(!AudioTeeSrcPads!.ContainsKey(mixKey))       throw new Exception("AudioTee src pad not found.");
                var audioTeePad = AudioTeeSrcPads[mixKey] ??    throw new Exception("AudioTee src pad not found.");
                if(!AudioQueues!.ContainsKey(mixKey))           throw new Exception("AudioQueue not found.");
                var audioQueue = AudioQueues[mixKey] ??         throw new Exception("AudioQueue not found.");
                if(!AudioGains!.ContainsKey(mixKey))            throw new Exception("AudioGain not found.");
                var mixGain = AudioGains[mixKey] ??             throw new Exception("AudioGain not found.");
                if(!AudioPans!.ContainsKey(mixKey))             throw new Exception("AudioPan not found.");
                var audioPan = AudioPans[mixKey] ??             throw new Exception("AudioPan not found.");
                if(!FinalMixerSinkPads!.ContainsKey(mixKey))    throw new Exception("FinalMixer sink pad not found.");
                var finalMixerSinkPad = FinalMixerSinkPads[mixKey] ?? throw new Exception("FinalMixer sink pad not found.");
                if(!FinalMixers!.ContainsKey(sinkUuid))         throw new Exception("FinalMixer not found.");
                var finalMixer = FinalMixers[sinkUuid] ??       throw new Exception("FinalMixer not found.");

                //disconnect the source participant's audio tee from their mixer
                var audioQueueSinkPad = audioQueue.GetStaticPad("sink") ?? throw new Exception("AudioQueue sink pad not found.");
                var ret = audioQueue.SendEvent(Gst.Event.NewEos());
                if(!audioTeePad.Unlink(audioQueueSinkPad)) _log.Warn($"audioTeePad.Unlink(audioQueueSinkPad) failed.");
                //we created a new src pad on the audio tee for this participant, so we need to release it
                audioTee.ReleaseRequestPad(audioTeePad);
                audioTeePad.Dispose();

                //disconnect the source participant's mixer from the sink participant's mixer
                var audioPanPad = audioPan.GetStaticPad("src") ?? throw new Exception("AudioPan src pad not found.");
                if(!audioPanPad.Unlink(finalMixerSinkPad)) _log.Warn($"audioPanPad.Unlink(finalMixerSinkPad) failed.");

                //We created a new sink pad on the final mixer for this participant, so we need to release it
                finalMixer.ReleaseRequestPad(finalMixerSinkPad);
                finalMixerSinkPad.Dispose();
            }
            catch (Exception e)
            {
                _log.Error($"{mixKey} Remove from mix failed.", e);
                return false;
            }
            _log.Info($"{mixKey} removed from mix.");
            Visualiser.Graph(_room, "AudioMixer Remove");
            return true;
        }

    }
}