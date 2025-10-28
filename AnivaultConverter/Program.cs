using AnivaultConverter;
using Coravel;
using Serilog;
using Serilog.Events;

HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);

builder.Services
    .AddScoped<VideoConverterTask>()
    .AddScheduler()
    .AddSerilog(serilogConfig =>
    {
        serilogConfig = serilogConfig.MinimumLevel.Information().WriteTo.Console();

        serilogConfig.WriteTo.File("/log/anivaultConverter.log",
            LogEventLevel.Information,
            rollingInterval: RollingInterval.Day,
            retainedFileCountLimit: 7,
            outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {Message:lj}{NewLine}{Exception}");
    });

IHost host = builder.Build();

using (IServiceScope scope = host.Services.CreateScope())
{
    scope.ServiceProvider
        .UseScheduler(scheduler =>
        {
            scheduler.Schedule<VideoConverterTask>()
                .EveryThirtySeconds()
                .RunOnceAtStart()
                .PreventOverlapping(nameof(VideoConverterTask));
        })
        .LogScheduledTaskProgress();
}

host.Run();