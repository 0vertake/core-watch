using CoreWatch.Shared;
using Microsoft.AspNetCore.SignalR;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSignalR();

var app = builder.Build();

app.MapGet("/", () => "CoreWatch NotificationService is running. SignalR hub: /alarms");
app.MapHub<AlarmHub>("/alarms");

app.MapPost("/api/notifications/alarm", async (
    AlarmNotificationDto alarm,
    IHubContext<AlarmHub> hub) =>
{
    WriteAlarm(alarm);
    await hub.Clients.All.SendAsync("AlarmRaised", alarm);
    return Results.Ok("Alarm sent to SignalR clients.");
});

app.Run();

static void WriteAlarm(AlarmNotificationDto alarm)
{
    var oldColor = Console.ForegroundColor;
    Console.ForegroundColor = alarm.Priority switch
    {
        1 => ConsoleColor.Yellow,
        2 => ConsoleColor.DarkYellow,
        3 => ConsoleColor.Red,
        _ => oldColor
    };
    Console.WriteLine($"[NOTIFICATION P{alarm.Priority}] {alarm.SensorId}: {alarm.Value:F2} C");
    Console.ForegroundColor = oldColor;
}

public class AlarmHub : Hub
{
}
