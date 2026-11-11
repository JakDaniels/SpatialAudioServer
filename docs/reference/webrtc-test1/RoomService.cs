using log4net;

namespace WebRtcTest1
{
    public class RoomService
    {
        private static readonly ILog _log = LogManager.GetLogger(typeof(RoomService));
        private List<Room> _rooms = new List<Room>();
        public RoomService()
        {
            _log.Info("AudioService created.");
        }

        public void AddRoom(Room room)
        {
            _rooms.Add(room);
            _log.Info($"Room {room.RoomUuid} added.");
        }
    }
}