using System.Net.WebSockets;
using System.Text;
using log4net;

namespace WebRtcTest1
{
    public class TestWsServerService
    {
        private static readonly ILog _log = LogManager.GetLogger(typeof(TestWsServerService));
        private readonly List<WebSocket> _sockets = new();

        private readonly RoomService _roomService;

        public TestWsServerService(RoomService roomService)
        {
            _roomService = roomService;
            _log.Info("TestWsServerService created.");
        }
        
        public async Task HandleWebSocketConnection(WebSocket socket)
        {
            _sockets.Add(socket);
            var buffer = new byte[1024 * 2];
            var mustExit = false;
            while (socket.State == WebSocketState.Open && !mustExit)
            {
                var result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), default);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    await socket.CloseAsync(result.CloseStatus!.Value, result.CloseStatusDescription, default);
                    break;
                }

                var message = Encoding.UTF8.GetString(buffer[..result.Count]);
                if(message == "exit") mustExit = true;
                
                foreach (var s in _sockets)
                {
                    await s.SendAsync(buffer[..result.Count], WebSocketMessageType.Text, true, default);
                }
            }
            _sockets.Remove(socket);
        }
    }
}