using System.Text;

namespace WebRtcTest1
{
    public class TestWsClientService
    {
        public async Task HandleClient(HttpContext context)
        {
            var port = context.Request.Host.Port;
            var output = """
<!DOCTYPE html>
<html>
<head>
    <title>WebSocket Chat</title>
</head>
<body>
    <h1>WebSocket Chat</h1>
    <div id="messages"></div>
    <input id="chatbox" >
    <script>
""";
            output += $"    const socket = new WebSocket('ws://localhost:{port}/ws');\n";
            output += """
        socket.onmessage = function (event) {
            const messages = document.getElementById("messages");
            messages.innerHTML += `<p>${event.data}</p>`;
        };

        document.getElementById("chatbox").addEventListener("keyup", function(event) {
            if (event.key === "Enter") {
                socket.send(event.target.value);
                event.target.value = "";
            }
        });
    </script>
</body>
</html>
""";
            

            await context.Response.WriteAsync(output, Encoding.UTF8);

        }
    }
}