namespace AudioMixerTest3
{
    public class AddParticipantRequest
    {
        public Guid Uuid { get; set; }
        public string Name { get; set; } = string.Empty;
        public bool PositionIsRandom { get; set; } = false;
        public int X { get; set; } = 12800;
        public int Y { get; set; } = 12800;
        public int Z { get; set; } = 2000;
        public int W { get; set; } = 100;

        public int SourceType { get; set; } = global::AudioMixerTest3.SourceType.TestTone;
        public string SourceLocation { get; set; } = string.Empty;
        public int SinkType { get; set; } = global::AudioMixerTest3.SinkType.AudioOut;
        public string SinkLocation { get; set; } = string.Empty;
    }
}