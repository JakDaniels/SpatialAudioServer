using Gst;
using log4net;

namespace WebRtcTest1
{
    public class Room
    {
        private static readonly ILog _log = LogManager.GetLogger(typeof(Room));

        public Guid RoomUuid { get; set; }
        public string RoomName { get; set; } = string.Empty;

        public Pipeline? Pipeline { get; set; }

        private Bus? _bus { get; set; }

        public bool IsStarted { get; set; } = false;


        public enum AppState {
            APP_STATE_UNKNOWN = 0,
            APP_STATE_ERROR = 1, /* generic error */
            SERVER_CONNECTING = 1000,
            SERVER_CONNECTION_ERROR,
            SERVER_CONNECTED, /* Ready to register */
            SERVER_REGISTERING = 2000,
            SERVER_REGISTRATION_ERROR,
            SERVER_REGISTERED, /* Ready to call a peer */
            SERVER_CLOSED, /* server connection closed by us or the server */
            ROOM_JOINING = 3000,
            ROOM_JOIN_ERROR,
            ROOM_JOINED,
            ROOM_CALL_NEGOTIATING = 4000, /* negotiating with some or all peers */
            ROOM_CALL_OFFERING, /* when we're the one sending the offer */
            ROOM_CALL_ANSWERING, /* when we're the one answering an offer */
            ROOM_CALL_STARTED, /* in a call with some or all peers */
            ROOM_CALL_STOPPING,
            ROOM_CALL_STOPPED,
            ROOM_CALL_ERROR,
        };



        public Room(Guid roomUuid, string roomName)
        {
            RoomUuid = roomUuid;
            RoomName = roomName;
            Pipeline = new Pipeline($"room-{RoomUuid}");
            _log.Info($"Room {RoomUuid} created.");
        }

        public void OnBusMessage(Bus bus, Message message)
        {
            _log.Debug($"{RoomUuid} bus message: {message.Type}");
        }
 
    }
}