using System.Collections.Concurrent;

namespace AudioMixerTest3
{
    public class AudioServer
    {
        public ConcurrentDictionary<Guid, Room> Rooms { get; set; }

        public List<Guid> TestUuids { get; set; }

        public bool VisualiseGraph { get; set; } = false;

        public AudioServer(bool visualiseGraph = false, bool clearPreviousVisualisations = false)
        {
            Rooms = new ConcurrentDictionary<Guid, Room>();

            TestUuids = new List<Guid>();
            TestUuids.Add(Guid.Parse("21d9c1e2-c9f5-11ef-b905-436c8a541b35"));
            TestUuids.Add(Guid.Parse("21d9dee8-c9f5-11ef-b67f-838a794e3d35"));
            TestUuids.Add(Guid.Parse("21d9fa54-c9f5-11ef-90bc-6b5dcb37a7d6"));
            TestUuids.Add(Guid.Parse("21da1584-c9f5-11ef-aea5-3f1c21d768fa"));
            TestUuids.Add(Guid.Parse("21da2fb0-c9f5-11ef-a6a7-1706fc12805b"));
            TestUuids.Add(Guid.Parse("21da48ec-c9f5-11ef-b438-17e4b762ea81"));
            TestUuids.Add(Guid.Parse("21da6296-c9f5-11ef-b12d-7b7730f10cfe"));
            TestUuids.Add(Guid.Parse("21da7bc8-c9f5-11ef-9531-831c50e7e1bc"));
            TestUuids.Add(Guid.Parse("21da94c8-c9f5-11ef-ba4d-7bb72477a062"));
            TestUuids.Add(Guid.Parse("21daae7c-c9f5-11ef-aaf9-638ba7d041aa"));

            VisualiseGraph = visualiseGraph;

            if(clearPreviousVisualisations) Visualiser.RemoveAllGraphs();
        }
    }
}