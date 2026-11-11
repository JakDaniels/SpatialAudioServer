using System.Diagnostics;
using System.Runtime.InteropServices;
using Gst;
using Microsoft.AspNetCore.Builder;
using log4net;
using log4net.Config;
namespace WebRtcTest1
{
    partial class WebRtcTest1
    {
        private static readonly ILog _log = LogManager.GetLogger(typeof(WebRtcTest1));
        static void Main(string[] args)
        {
            XmlConfigurator.Configure(new FileInfo($"{Environment.CurrentDirectory}/log4net.config"));
            _log.Info("WebRtcTest1 started.");

            var builder = WebApplication.CreateBuilder(args);
            builder.Services.AddSingleton<TestWsServerService>();
            builder.Services.AddSingleton<TestWsClientService>();
            builder.Services.AddSingleton<RoomService>();
            
            var app = builder.Build();
            app.UseWebSockets();
            app.MapGet("/ws", async (HttpContext context, TestWsServerService webRtcService) =>
            {
                if (context.WebSockets.IsWebSocketRequest)
                {
                    var webSocket = await context.WebSockets.AcceptWebSocketAsync();
                    await webRtcService.HandleWebSocketConnection(webSocket);
                }
                else
                {
                    context.Response.StatusCode = 400;
                    await context.Response.WriteAsync("Expected a WebSocket request");
                }
            });
            app.MapGet("/wstestclient", async (HttpContext context, TestWsClientService testClientService) =>
            {
                await testClientService.HandleClient(context);
            });

            Application.Init(ref args);

            Gst.Debug.SetActive(true);
            Gst.Debug.SetDefaultThreshold(Gst.DebugLevel.Warning);


            // var cts = new CancellationTokenSource();
            // var webRtcThread =  new Thread(() => );
            // webRtcThread.Start();

            app.Run();

            // cts.Cancel();
            // webRtcThread.Join();
        }
    }
}
