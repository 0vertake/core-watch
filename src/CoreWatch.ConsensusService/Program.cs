using CoreWatch.ConsensusService;
using CoreWatch.Shared;
using Microsoft.EntityFrameworkCore;

var builder = Host.CreateApplicationBuilder(args);
string connectionString = builder.Configuration.GetConnectionString("CoreWatch")
    ?? "Host=localhost;Port=5432;Database=corewatch;Username=postgres;Password=postgres";

builder.Services.AddDbContext<CoreWatchDbContext>(options =>
    options.UseNpgsql(connectionString));
builder.Services.AddHostedService<Worker>();

var host = builder.Build();
host.Run();
