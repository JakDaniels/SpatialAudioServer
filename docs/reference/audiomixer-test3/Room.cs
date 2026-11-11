using Gst;
using log4net;

namespace AudioMixerTest3
{
    public class Room : IDisposable
    {
        private static readonly ILog _log = LogManager.GetLogger(typeof(Room));

        public Guid RoomUuid { get; set; }
        public string RoomName { get; set; } = string.Empty;

        public Pipeline? Pipeline { get; set; }

        private Bus? _bus { get; set; }

        public bool IsStarted { get; set; } = false;

        public Dictionary<Guid, Participant> Participants { get; set; } = new Dictionary<Guid, Participant>();

        public AudioMixer Mixer { get; set; }

        public Room(Guid roomUuid, string roomName)
        {
            RoomUuid = roomUuid;
            RoomName = roomName;
            Pipeline = new Pipeline($"room-{RoomUuid}");
            _bus = Pipeline.Bus;
            // _bus.AddSignalHandler("",OnBusMessage);
            Mixer = new AudioMixer(this, Guid.NewGuid());
            _log.Info($"Room {RoomUuid} created.");
        }

        public void OnBusMessage(Bus bus, Message message)
        {
            _log.Debug($"{RoomUuid} bus message: {message.Type}");
        }

        public Participant? AddParticipant(AddParticipantRequest request)
        {
            Participant? participant = null;
            
            if(request.Uuid == Guid.Empty)
                request.Uuid = Guid.NewGuid();

            if(string.IsNullOrWhiteSpace(request.Name))
                request.Name = $"Participant-{Participants.Count + 1}";
                
            var uuid = request.Uuid;
            _log.Debug($"{RoomUuid} Add {uuid} called.");

            try
            {
                if(Participants.ContainsKey(uuid))
                    throw new Exception($"{uuid} already exists in room {RoomUuid}.");

                participant = new Participant(request);
                
                var outgoingAudio = new OutgoingAudio(uuid);
                if(!outgoingAudio.Create(this, request.SinkType, request.SinkLocation))
                    throw new Exception("Outgoing audio creation failed!");

                var incomingAudio = new IncomingAudio(uuid);
                if(!incomingAudio.Create(this, request.SourceType, request.SourceLocation))
                    throw new Exception("Incoming audio creation failed!");

                //adding ourselves to the mix ensures that there is always a valid pipeline with at least one input
                //although our own input is always muted at gain = 0.00
                if(!Mixer.Create(incomingAudio, outgoingAudio)) throw new Exception("Mixer creation failed!");
                if(!Mixer.Add(incomingAudio, outgoingAudio)) throw new Exception("Mixer add failed!");
                if(!Mixer.Start(incomingAudio, outgoingAudio)) throw new Exception("Mixer start failed!");

                outgoingAudio.Start();
                incomingAudio.Start();

                //add the participant to the room list
                Participants.Add(participant.Uuid, participant);

                if(Participants.Count > 1)
                {
                    //now add this participant to all other participants
                    foreach(var p in Participants.Values)
                    {
                        if(p.Uuid == participant.Uuid) continue;
                        var poa = p.OutgoingAudio;
                        if(!Mixer.Create(incomingAudio, poa)) _log.Warn($"Creating mix to {p.Uuid} failed!");
                        if(!Mixer.Add(incomingAudio,poa)) _log.Warn($"Adding to {p.Uuid} mix failed!");
                        if(!Mixer.Start(incomingAudio, poa)) _log.Warn($"Starting mix for {p.Uuid} failed!");
                    }

                    //then add mixes of all other participants to this new participant
                    foreach(var p in Participants.Values)
                    {
                        if(p.Uuid == participant.Uuid) continue;
                        var pia = p.IncomingAudio;
                        if(!Mixer.Create(pia, outgoingAudio)) _log.Warn($"Creating mix from {p.Uuid} failed!");
                        if(!Mixer.Start(pia, outgoingAudio)) _log.Warn($"Starting mix for {p.Uuid} failed!");
                        if(!Mixer.Add(pia, outgoingAudio)) _log.Warn($"Adding {p.Uuid} to mix failed!");
                        
                    }
                }

                participant.IncomingAudio = incomingAudio;
                participant.OutgoingAudio = outgoingAudio;
            }
            catch(Exception ex)
            {
                _log.Error($"{RoomUuid} Add {uuid} failed.", ex);
                return participant;
            }

            _log.Info($"{RoomUuid} Add {participant.Uuid} succeeded.");
            if(!IsStarted && Participants.Count > 0) Start();
            return participant;
        }

        public bool RemoveParticipant(Participant participant)
        {
            return RemoveParticipant(participant.Uuid);
        }

        public bool RemoveParticipant(Guid uuid)
        {
            _log.Debug($"{RoomUuid} Remove {uuid} called.");
            if(!Participants.ContainsKey(uuid)) throw new Exception($"{uuid} not found in room.");
            
            try
            {
                var incomingAudio = Participants[uuid].IncomingAudio;
                var outgoingAudio = Participants[uuid].OutgoingAudio;

                if(Participants.Count > 1)
                {
                    //remove all other participants mixes from me
                    foreach(var p in Participants.Values)
                    {
                        if(p.Uuid == uuid) continue;
                        var pia = p.IncomingAudio;
                        if(!Mixer.Remove(pia, outgoingAudio)) _log.Warn($"Remove {p.Uuid} from mix failed!");
                        if(!Mixer.Stop(pia, outgoingAudio)) _log.Warn($"Stop mix for {p.Uuid} failed!");
                        if(!Mixer.Destroy(pia, outgoingAudio)) _log.Warn($"Destroy mix from {p.Uuid} failed!");
                    }

                    // remove my mix from all other paticipants
                    foreach(var p in Participants.Values)
                    {
                        if(p.Uuid == uuid) continue;
                        var poa = p.OutgoingAudio;
                        if(!Mixer.Stop(incomingAudio, poa)) _log.Warn($"Stop mix for {p.Uuid} failed!");
                        if(!Mixer.Remove(incomingAudio, poa)) _log.Warn($"Remove {p.Uuid} from mix failed!");
                        if(!Mixer.Destroy(incomingAudio, poa)) _log.Warn($"Destroy mix to {p.Uuid} failed!");
                    }
                }

                outgoingAudio.Stop();
                incomingAudio.Stop();

                //remove myself from the mix
                Mixer.Stop(incomingAudio, outgoingAudio);
                Mixer.Remove(incomingAudio, outgoingAudio);
                Mixer.Destroy(incomingAudio, outgoingAudio);

                incomingAudio.Destroy();
                outgoingAudio.Destroy();

                //remove the participant from the room list
                Participants.Remove(uuid);
            }
            catch(Exception ex)
            {
                _log.Error($"{RoomUuid} Remove {uuid} failed.", ex);
                return false;
            }
        

            _log.Info($"{RoomUuid} Remove {uuid} succeeded.");
            if(IsStarted && Participants.Count == 0) Stop();
            return true;
        }


        public bool Start()
        {
            _log.Debug($"{RoomUuid} start called.");
            if(IsStarted) return true;
            try
            {
                if(Pipeline == null) throw new Exception("Pipeline is null.");
                var ret = Pipeline.SetState(State.Playing);
                // if(ret != StateChangeReturn.Success)
                //     throw new Exception("Pipeline.SetState(State.Playing) failed.");
            }
            catch(Exception ex)
            {
                _log.Error($"{RoomUuid} start failed.", ex);
                return false;
            }

            IsStarted = true;
            _log.Info($"{RoomUuid} started.");
            return true;
        }

        public bool Stop()
        {
            _log.Debug($"{RoomUuid} stop called.");
            if(IsStarted == false) return true;
            try
            {
                if(Pipeline == null) throw new Exception("Pipeline is null.");
                if(Pipeline.SetState(State.Null) != StateChangeReturn.Success)
                    throw new Exception("Pipeline.SetState(State.Null) failed.");
            }
            catch(Exception ex)
            {
                _log.Error($"{RoomUuid} stop failed.", ex);
                return false;
            }

            IsStarted = false;
            Visualiser.Graph(this, "Room Stop");
            _log.Info($"{RoomUuid} stopped.");
            return true;
        }

        // public ConcurrentDictionary<Guid, ParticipantIncomingAudioMixer> Participants { get; set; } // <uuid, participant>

        // public Dictionary<Guid, Element> AudioSrcs { get; set; } = new Dictionary<Guid, Element>();
        // public Dictionary<Guid, Element> AudioTees { get; set; } = new Dictionary<Guid, Element>();
        // public Dictionary<Guid, Element> AudioQueues { get; set; } = new Dictionary<Guid, Element>();
        // public Dictionary<Guid, Element> AudioAmps { get; set; } = new Dictionary<Guid, Element>();
        // public Dictionary<Guid, Element> AudioPans { get; set; } = new Dictionary<Guid, Element>();


        // public Room()
        // {
        //     Participants = new ConcurrentDictionary<Guid, ParticipantAudioMixer>();
        // }

        // public Room AddParticipant(ParticipantAudioMixer pam)
        // {
            
        //     Participants.TryAdd(pam.Uuid, pam);
        //     return this;
        // }
        // public ParticipantAudioMixer CreateRandomParticipant()
        // {
        //     var pam = new ParticipantAudioMixer(Guid.NewGuid()) {
        //         IsConnected = true,
        //         IsMuted = false,
        //         WorldX = 128 + new Random().Next(20),
        //         WorldY = 128 + new Random().Next(20),
        //         WorldZ = 20 + new Random().Next(20),
        //         WorldW = new Random().Next(360)
        //     };
        //     return pam;
        // }

        public void Dispose()
        {
            _log.Debug($"{RoomUuid} dispose called.");
            if(Participants.Count > 0)
            {
                foreach(var p in Participants.Values)
                {
                    RemoveParticipant(p);
                }
            }

            Pipeline?.Dispose();
        }
    }
}