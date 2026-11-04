using OpenCode.Playground.Hq;
using OpenCode.Playground.Hq.Components;
using OpenCode.Playground.Hq.Features.Connection;
using OpenCode.Playground.Hq.Features.Conversation;
using OpenCode.Playground.Hq.Features.Panel;
using OpenCode.Playground.Hq.Features.Sessions;

var builder = WebApplication.CreateBuilder(args);
builder.AddTelemetry();
builder.Services.AddRazorComponents().AddInteractiveServerComponents();
builder.Services.AddHealthChecks().AddCheck<OpenCodeHealthCheck>("opencode");
builder.Services.AddSingleton(static services => OpenCodeConnection.Connect(services.GetRequiredService<IConfiguration>()));
builder.Services.AddSingleton<SessionBoard>();
builder.Services.AddSingleton<SourceSnippets>();
builder.Services.AddTransient<SessionConversation>();
builder.Services.AddHostedService(static services => services.GetRequiredService<SessionBoard>());

var app = builder.Build();
app.UseAntiforgery();
app.MapStaticAssets();
app.MapHealthChecks("/health");
app.MapRazorComponents<App>().AddInteractiveServerRenderMode();
await app.RunAsync();
