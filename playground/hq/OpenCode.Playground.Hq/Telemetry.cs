using OpenTelemetry.Logs;
using OpenTelemetry.Trace;

namespace OpenCode.Playground.Hq;

/// <summary>Exports host logs and HTTP traces to the Aspire dashboard when it supplies an OTLP endpoint.</summary>
internal static class Telemetry
{
    public static void AddTelemetry(this WebApplicationBuilder builder)
    {
        if (!string.IsNullOrEmpty(builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]))
        {
            builder.Services.AddOpenTelemetry().WithTracing(tracing => tracing
                .AddHttpClientInstrumentation().AddAspNetCoreInstrumentation().AddOtlpExporter());
            builder.Logging.AddOpenTelemetry(logging =>
            {
                logging.IncludeFormattedMessage = true;
                logging.AddOtlpExporter();
            });
        }
    }
}
