using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OpenCode.Playground.Worker;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddHostedService<EventWorker>();

using var host = builder.Build();
await host.RunAsync();

// EventWorker sets this on failure so a service manager can restart the worker.
return Environment.ExitCode;
