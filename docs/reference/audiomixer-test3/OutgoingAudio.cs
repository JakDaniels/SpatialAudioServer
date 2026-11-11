using Gst;
using log4net;

namespace AudioMixerTest3
{
    public class OutgoingAudio
    {
        private static readonly ILog _log = LogManager.GetLogger(typeof(OutgoingAudio));

        public Guid Uuid { get; set; }

        public Element? FinalMixer { get; set; }
        public Element? AudioQueue { get; set; }
        public Element? AudioSink { get; set; }

        public bool IsReady { get; set; } = false;

        public bool IsStarted { get; set; } = false;

        private Room? _room { get; set; }
        public Pipeline? _pipeline { get; set; }

        public OutgoingAudio(Guid uuid)
        {
            Uuid = uuid;
        }

        public bool Create(Room room, int SinkType = SinkType.AudioOut, string sinkLocation = "")
        {
            _room = room;
            _pipeline = room.Pipeline;
            _log.Debug($"{Uuid} outgoing audio creation called. Type: {SinkType} Location: {sinkLocation}");
            try
            {
                if(_pipeline == null) throw new Exception("Room pipeline is null.");

                switch (SinkType)
                {
                    case global::AudioMixerTest3.SinkType.Null:
                        AudioSink = ElementFactory.Make("fakesink", $"audio-sink-{Uuid}");
                        break;
                    case global::AudioMixerTest3.SinkType.File:
                        AudioSink = ElementFactory.Make("filesink", $"audio-sink-{Uuid}");
                        AudioSink.SetProperty("location", new GLib.Value(sinkLocation));
                        break;
                    case global::AudioMixerTest3.SinkType.AudioOut:
                        AudioSink = ElementFactory.Make("autoaudiosink", $"audio-sink-{Uuid}");
                        break;
                    case global::AudioMixerTest3.SinkType.Rtp:
                        throw new NotImplementedException();
                    default:
                        throw new NotImplementedException();
                }

                FinalMixer = ElementFactory.Make("audiomixer", $"audiomixer-{Uuid}");
                AudioQueue = ElementFactory.Make("queue", $"audio-queue-{Uuid}");

                if(new[] {FinalMixer, AudioQueue, AudioSink}.Any(e => e == null)) throw new Exception("Sink elements creation failed.");

                _pipeline.Add(FinalMixer, AudioQueue, AudioSink);

                if(!FinalMixer.Link(AudioQueue)) throw new Exception("FinalMixer.Link(AudioQueue) failed.");
                if(!AudioQueue.Link(AudioSink)) throw new Exception("AudioQueue.Link(AudioSink) failed.");
            }
            catch (Exception ex)
            {
                _log.Error($"{Uuid} outgoing audio creation failed.", ex);
                return false;
            }
            _log.Info($"{Uuid} outgoing audio chain created.");
            Visualiser.Graph(_room, "OutgoingAudio Create");
            IsReady = true;
            return true;
        }

        public void Destroy()
        {
            _log.Debug($"{Uuid} outgoing audio chain destroy called.");
            if(!IsReady) return;
            if(IsStarted) return;
            if(_pipeline == null) return;
            if(new[] {FinalMixer, AudioQueue, AudioSink}.Any(e => e == null)) return;
            FinalMixer!.SendEvent(Gst.Event.NewEos());
            FinalMixer!.SetState(State.Null);
            AudioQueue!.SendEvent(Gst.Event.NewEos());
            AudioQueue!.SetState(State.Null);
            AudioSink!.SendEvent(Gst.Event.NewEos());
            AudioSink!.SetState(State.Null);
            _pipeline.Remove(FinalMixer, AudioQueue, AudioSink);
            FinalMixer.Dispose();
            AudioQueue.Dispose();
            AudioSink.Dispose();
            _log.Info($"{Uuid} outgoing audio chain destroyed.");
            Visualiser.Graph(_room!, "OutgoingAudio Destroy");
        }

        public void Start()
        {
            _log.Debug($"{Uuid} outgoing audio chain start called.");
            if(!IsReady) return;
            if(IsStarted) return;
            if(_pipeline == null) return;
            if(new[] {FinalMixer,AudioQueue, AudioSink}.Any(e => e == null)) return;
            FinalMixer!.SetState(State.Playing);
            AudioQueue!.SetState(State.Playing);
            AudioSink!.SetState(State.Playing);
            IsStarted = true;
            _log.Info($"{Uuid} outgoing audio chain started.");
        }

        public void Stop()
        {
            _log.Debug($"{Uuid} outgoing audio chain stop called.");
            if(!IsReady) return;
            if(!IsStarted) return;
            if(_pipeline == null) return;
            if(new[] {FinalMixer, AudioQueue, AudioSink}.Any(e => e == null)) return;
            FinalMixer!.SendEvent(Gst.Event.NewEos());
            FinalMixer!.SetState(State.Null);
            AudioQueue!.SendEvent(Gst.Event.NewEos());
            AudioQueue!.SetState(State.Null);
            AudioSink!.SendEvent(Gst.Event.NewEos());
            AudioSink!.SetState(State.Null);
            IsStarted = false;
            _log.Info($"{Uuid} outgoing audio chain stopped.");
        }

        // public void MixerStart(IncomingAudio pia)
        // {
        //     try
        //     {
        //         if(!IsReady) throw new Exception("OutgoingAudio is not ready.");
        //         if(_pipeline == null) throw new Exception("Room pipeline is null.");
        //         if(new[] {AudioQueues, AudioGains, AudioPans}.Any(e => e == null))
        //             throw new Exception("Mix elements not found.");

        //         if(!AudioQueues!.ContainsKey(pia.Uuid))
        //             throw new Exception($"AudioQueue for {pia.Uuid} not found.");
        //         if(!AudioGains!.ContainsKey(pia.Uuid))
        //             throw new Exception($"AudioGain for {pia.Uuid} not found.");
        //         if(!AudioPans!.ContainsKey(pia.Uuid))
        //             throw new Exception($"AudioPan for {pia.Uuid} not found.");

        //         var audioQueue = AudioQueues[pia.Uuid];
        //         var audioGain = AudioGains[pia.Uuid];
        //         var audioPan = AudioPans[pia.Uuid];

        //         //and our mix elements can be started as well
        //         audioQueue.SetState(State.Playing);
        //         audioGain.SetState(State.Playing);
        //         audioPan.SetState(State.Playing);
        //         if(IsMixerStarted.ContainsKey(pia.Uuid)) IsMixerStarted[pia.Uuid] = true;
        //         else IsMixerStarted.Add(pia.Uuid, true);
        //     }
        //     catch (Exception e)
        //     {
        //         _log.Error($"{Uuid} mixer audio chain for {pia.Uuid} start failed.", e);
        //         return;
        //     }
        //     _log.Info($"{Uuid} mixer audio chain for {pia.Uuid} started.");
        // }

        // public void MixerStop(IncomingAudio pia)
        // {
        //     try
        //     {
        //         if(!IsReady) throw new Exception("OutgoingAudio is not ready.");
        //         if(_pipeline == null) throw new Exception("Room pipeline is null.");
        //         if(new[] {AudioQueues, AudioGains, AudioPans}.Any(e => e == null))
        //             throw new Exception("Mix elements not found.");

        //         if(!AudioQueues!.ContainsKey(pia.Uuid))
        //             throw new Exception($"AudioQueue for {pia.Uuid} not found.");
        //         if(!AudioGains!.ContainsKey(pia.Uuid))
        //             throw new Exception($"AudioGain for {pia.Uuid} not found.");
        //         if(!AudioPans!.ContainsKey(pia.Uuid))
        //             throw new Exception($"AudioPan for {pia.Uuid} not found.");

        //         var audioQueue = AudioQueues[pia.Uuid];
        //         var audioGain = AudioGains[pia.Uuid];
        //         var audioPan = AudioPans[pia.Uuid];

        //         audioQueue.SetState(State.Null);
        //         audioGain.SetState(State.Null);
        //         audioPan.SetState(State.Null);
        //         if(IsMixerStarted.ContainsKey(pia.Uuid)) IsMixerStarted[pia.Uuid] = false;
        //     }
        //     catch (Exception e)
        //     {
        //         _log.Error($"{Uuid} mixer audio chain for {pia.Uuid} stop failed.", e);
        //         return;
        //     }
        //     _log.Info($"{Uuid} mixer audio chain for {pia.Uuid} stopped.");
        // }

        // public void MixerDestroy(IncomingAudio pia)
        // {
        //     try
        //     {
        //         if(!IsReady) throw new Exception("OutgoingAudio is not ready.");
        //         if(_pipeline == null) throw new Exception("Room pipeline is null.");
        //         if(new[] {AudioQueues, AudioGains, AudioPans}.Any(e => e == null))
        //             throw new Exception("Mix elements not found.");

        //         if(!AudioQueues!.ContainsKey(pia.Uuid))
        //             throw new Exception($"AudioQueue for {pia.Uuid} not found.");
        //         if(!AudioGains!.ContainsKey(pia.Uuid))
        //             throw new Exception($"AudioGain for {pia.Uuid} not found.");
        //         if(!AudioPans!.ContainsKey(pia.Uuid))
        //             throw new Exception($"AudioPan for {pia.Uuid} not found.");

        //         var audioQueue = AudioQueues[pia.Uuid];
        //         var audioGain = AudioGains[pia.Uuid];
        //         var audioPan = AudioPans[pia.Uuid];

        //         audioQueue.SetState(State.Null);
        //         audioGain.SetState(State.Null);
        //         audioPan.SetState(State.Null);

        //         audioQueue.Dispose();
        //         audioGain.Dispose();
        //         audioPan.Dispose();

        //         AudioQueues.Remove(pia.Uuid);
        //         AudioGains.Remove(pia.Uuid);
        //         AudioPans.Remove(pia.Uuid);
        //     }
        //     catch (Exception e)
        //     {
        //         _log.Error($"{Uuid} mixer audio chain for {pia.Uuid} destroy failed.", e);
        //         return;
        //     }
        //     _log.Info($"{Uuid} mixer audio chain for {pia.Uuid} destroyed.");
        // }

        // public bool AddParticipantToMix(IncomingAudio pia)
        // {
        //     try
        //     {
        //         if(_pipeline == null)
        //             throw new Exception("Room pipeline is null.");
        //         if(!IsReady)
        //             throw new Exception("ParticipantOutgoingAudio is not ready.");
        //         if(new[] {FinalMixer, AudioSink}.Any(e => e == null))
        //             throw new Exception("Sink elements not found.");
        //         if(new[] {AudioQueues, AudioGains, AudioPans}.Any(e => e == null))
        //             throw new Exception("Mix elements not found.");
        //         if(FinalMixerSinkPads == null)
        //             throw new Exception("FinalMixerSinkPads not found.");
        //         if(AudioQueues!.ContainsKey(pia.Uuid))
        //             throw new Exception("AudioQueue already exists.");
        //         if(AudioGains!.ContainsKey(pia.Uuid))
        //             throw new Exception("AudioGain already exists.");
        //         if(AudioPans!.ContainsKey(pia.Uuid))
        //             throw new Exception("AudioPan already exists.");

        //         var audioQueue = ElementFactory.Make("queue", $"audio-queue-{pia.Uuid}-{Uuid}");
        //         var audioGain = ElementFactory.Make("audioamplify", $"audio-gain-{pia.Uuid}-{Uuid}");
        //         var audioPan = ElementFactory.Make("audiopanorama", $"audio-pan-{pia.Uuid}-{Uuid}");

        //         if(new[] {audioQueue, audioGain, audioPan}.Any(e => e == null))
        //             throw new Exception("Mix elements creation failed!");

        //         AudioQueues.Add(pia.Uuid, audioQueue);
        //         AudioGains.Add(pia.Uuid, audioGain);
        //         AudioPans.Add(pia.Uuid, audioPan);

        //         _pipeline.Add(audioQueue, audioGain, audioPan);

        //         var inputMixerPad = FinalMixer!.GetRequestPad("sink_%u");
        //         var outputPanPad = audioPan.GetStaticPad("src");
        //         if(outputPanPad.Link(inputMixerPad) != PadLinkReturn.Ok) throw new Exception($"{pia.Uuid}: mixerPad.Link(panPad) failed.");
        //         // keep a record of this new mixer dynamic sink pad so we can release it later
        //         FinalMixerSinkPads.TryAdd(pia.Uuid, inputMixerPad);

        //         if(!audioGain.Link(audioPan)) throw new Exception($"{pia.Uuid}: audioGain.Link(audioPan) failed.");
        //         if(!audioQueue.Link(audioGain)) throw new Exception($"{pia.Uuid}: audioQueue.Link(audioGain) failed.");
                
        //         var inputTeePad = pia.AudioTee!.GetRequestPad("src_%u");
        //         var inputQueuePad = audioQueue.GetStaticPad("sink");
        //         if(inputTeePad.Link(inputQueuePad) != PadLinkReturn.Ok) throw new Exception($"{pia.Uuid}: teePad.Link(queuePad) failed.");

        //         if(pia.Uuid == Uuid)
        //         {
        //             //our own input audio is always muted (gain = 0.00) in our output mix
        //             audioGain.SetProperty("amplification", new GLib.Value(0.01));
        //             audioPan.SetProperty("panorama", new GLib.Value(-1.0));
        //         }
        //         else
        //         {   //When a participant is first created their elements can only be started if connected to a mix.
        //             //Now that we've added them to the mix we can make sure their elements are started.
        //             pia.AddReceiver(Uuid);
        //             //and our mix elements can be started as well
        //             audioQueue.SetState(State.Playing);
        //             audioGain.SetState(State.Playing);
        //             audioPan.SetState(State.Playing);
        //             _log.Info($"{Uuid} mixer audio chain started.");
        //             audioGain.SetProperty("amplification", new GLib.Value(0.06));
        //             audioPan.SetProperty("panorama", new GLib.Value(1.0));
        //         }
        //     }
        //     catch (Exception e)
        //     {
        //         _log.Error($"{Uuid} adding {pia.Uuid} to mix failed.", e);
        //         return false;
        //     }
        //     _log.Info($"{Uuid} added {pia.Uuid} to their mix.");
        //     Visualiser.Graph(_room!);
        //     return true;
        // }

        // public bool RemoveParticipantFromMix(IncomingAudio pia)
        // {
        //     try
        //     {
        //         if(_pipeline == null)
        //             throw new Exception("Room pipeline is null.");
        //         if(!IsReady)
        //             throw new Exception("ParticipantOutgoingAudio is not ready.");
        //         if(new[] {FinalMixer, AudioSink}.Any(e => e == null))
        //             throw new Exception("Sink elements not found.");
        //         if(new[] {AudioQueues, AudioGains, AudioPans}.Any(e => e == null))
        //             throw new Exception("Mix elements not found.");
        //         if(FinalMixerSinkPads == null)
        //             throw new Exception("FinalMixerSinkPads not found.");

        //         if(!AudioQueues!.ContainsKey(pia.Uuid))
        //             throw new Exception($"AudioQueue for {pia.Uuid} not found.");
        //         if(!AudioGains!.ContainsKey(pia.Uuid))
        //             throw new Exception($"AudioGain for {pia.Uuid} not found.");
        //         if(!AudioPans!.ContainsKey(pia.Uuid))
        //             throw new Exception($"AudioPan for {pia.Uuid} not found.");

        //         var audioQueue = AudioQueues[pia.Uuid];
        //         var audioGain = AudioGains[pia.Uuid];
        //         var audioPan = AudioPans[pia.Uuid];

        //         if(new[] {audioQueue, audioGain, audioPan}.Any(e => e == null))
        //             throw new Exception($"Mix elements for {pia.Uuid} not found.");

        //         audioQueue.SetState(State.Null);
        //         audioGain.SetState(State.Null);
        //         audioPan.SetState(State.Null);
        //         _log.Info($"{Uuid} mixer audio chain stopped.");

        //         //We created a new sink pad on the final mixer for this participant, so we need to release it
        //         if(FinalMixerSinkPads != null && FinalMixerSinkPads.ContainsKey(pia.Uuid))
        //             FinalMixer!.ReleaseRequestPad(FinalMixerSinkPads[pia.Uuid]);
                
        //         //Now we can unlink the upstream chain
        //         FinalMixer!.Unlink(audioPan);
        //         audioPan.Unlink(audioGain);
        //         audioGain.Unlink(audioQueue);
                
        //         _pipeline.Remove(audioQueue, audioGain, audioPan);

        //         AudioQueues.Remove(pia.Uuid);
        //         AudioGains.Remove(pia.Uuid);
        //         AudioPans.Remove(pia.Uuid);

        //         audioQueue.Dispose();
        //         audioGain.Dispose();
        //         audioPan.Dispose();
        //     }
        //     catch (Exception e)
        //     {
        //         _log.Error($"{Uuid} removing {pia.Uuid} from mix failed.", e);
        //         return false;
        //     }
        //     _log.Info($"{Uuid} removed {pia.Uuid} from their mix.");
        //     Visualiser.Graph(_room!);
        //     return true;
        // }
    }
}