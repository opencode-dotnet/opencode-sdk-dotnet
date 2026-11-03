var builder = DistributedApplication.CreateBuilder(args);

// A plain serve does not resume sessions other processes run; a --service boot would.
var password = ParameterResourceBuilderExtensions.CreateDefaultPasswordParameter(builder, "opencode-password", special: false);
var opencode = builder.AddExecutable("opencode", "opencode", builder.AppHostDirectory, "serve", "--hostname", "127.0.0.1")
    .WithHttpEndpoint(env: "OPENCODE_PORT", name: "http")
    .WithArgs(static context =>
    {
        var endpoint = ((IResourceWithEndpoints)context.Resource).GetEndpoint("http");
        context.Args.Add("--port");
        context.Args.Add(endpoint.Property(EndpointProperty.TargetPort));
    })
    .WithEnvironment("OPENCODE_PASSWORD", password);

builder.AddProject<Projects.OpenCode_Playground_Hq>("hq")
    .WithEnvironment("OpenCode__Endpoint", opencode.GetEndpoint("http"))
    .WithEnvironment("OpenCode__Password", password)
    .WaitFor(opencode)
    .WithHttpHealthCheck("/health");

await builder.Build().RunAsync();
