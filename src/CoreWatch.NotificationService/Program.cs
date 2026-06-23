using CoreWatch.Shared;
using Microsoft.AspNetCore.SignalR;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSignalR();

var app = builder.Build();

app.UseDefaultFiles();
app.UseStaticFiles();

app.MapGet("/", () => "CoreWatch NotificationService is running. SignalR hub: /alarms, demo: /demo.html");
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
    AlarmConsole.WriteLine(alarm.Priority,
        $"[NOTIFICATION P{alarm.Priority}] {alarm.SensorId}: {alarm.Value:F2} C");
}

public class AlarmHub : Hub
{
}
