namespace AudioMixerTest3
{
    public class Participant : ParticipantBaseModel
    {
        public IncomingAudio IncomingAudio { get; set; } = default!;
        public OutgoingAudio OutgoingAudio { get; set; } = default!;

        public Participant(AddParticipantRequest request)
        {
     
        
            Uuid = request.Uuid;
            Name = request.Name;
            IncomingAudio = new IncomingAudio(Uuid);
            OutgoingAudio = new OutgoingAudio(Uuid);
            if(request.PositionIsRandom)
            {
                CreateRandomPosition();
                return;
            }
            Lp = new ListenerPosition { X = request.X, Y = request.Y, Z = request.Z };
            Lh = new ListenerHeading { X = request.X, Y = request.Y, Z = request.Z, W = request.W };
            Sp = new SenderPosition { X = request.X, Y = request.Y, Z = request.Z };
            Sh = new SenderHeading { X = request.X, Y = request.Y, Z = request.Z, W = request.W };
        }

        public void CreateRandomPosition()
        {
            var x = new Random().Next(25600);
            var y = new Random().Next(25600);
            var z = new Random().Next(4000);
            var w = 100; //No rotation?
            Lp = new ListenerPosition { X = x, Y = y, Z = z };
            Lh = new ListenerHeading { X = x, Y = y, Z = z, W = w };
            Sp = new SenderPosition { X = x, Y = y, Z = z };
            Sh = new SenderHeading { X = x, Y = y, Z = z, W = w };
        }
    }
}