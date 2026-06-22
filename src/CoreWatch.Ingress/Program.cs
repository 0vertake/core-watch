var builder = WebApplication.CreateBuilder(args);

string ingestionUrl = builder.Configuration["Services:Ingestion"]
    ?? "http://localhost:5001";
string notificationUrl = builder.Configuration["Services:Notification"]
    ?? "http://localhost:5002";

builder.Services.AddReverseProxy()
    .LoadFromMemory(
    [
        new Yarp.ReverseProxy.Configuration.RouteConfig
        {
            RouteId = "ingest",
            ClusterId = "ingestion",
            Match = new Yarp.ReverseProxy.Configuration.RouteMatch
            {
                Path = "/api/ingest"
            }
        },
        new Yarp.ReverseProxy.Configuration.RouteConfig
        {
            RouteId = "sensors-root",
            ClusterId = "ingestion",
            Match = new Yarp.ReverseProxy.Configuration.RouteMatch
            {
                Path = "/api/sensors"
            }
        },
        new Yarp.ReverseProxy.Configuration.RouteConfig
        {
            RouteId = "sensors",
            ClusterId = "ingestion",
            Match = new Yarp.ReverseProxy.Configuration.RouteMatch
            {
                Path = "/api/sensors/{**catch-all}"
            }
        },
        new Yarp.ReverseProxy.Configuration.RouteConfig
        {
            RouteId = "reports-root",
            ClusterId = "ingestion",
            Match = new Yarp.ReverseProxy.Configuration.RouteMatch
            {
                Path = "/api/reports"
            }
        },
        new Yarp.ReverseProxy.Configuration.RouteConfig
        {
            RouteId = "notifications",
            ClusterId = "notification",
            Match = new Yarp.ReverseProxy.Configuration.RouteMatch
            {
                Path = "/alarms/{**catch-all}"
            }
        }
    ],
    [
        new Yarp.ReverseProxy.Configuration.ClusterConfig
        {
            ClusterId = "ingestion",
            Destinations = new Dictionary<string, Yarp.ReverseProxy.Configuration.DestinationConfig>
            {
                ["default"] = new() { Address = ingestionUrl }
            }
        },
        new Yarp.ReverseProxy.Configuration.ClusterConfig
        {
            ClusterId = "notification",
            Destinations = new Dictionary<string, Yarp.ReverseProxy.Configuration.DestinationConfig>
            {
                ["default"] = new() { Address = notificationUrl }
            }
        }
    ]);

var app = builder.Build();

app.MapGet("/", () => "CoreWatch Ingress radi. Rute: /api/ingest, /api/sensors, /alarms");
app.MapReverseProxy();

app.Run();
