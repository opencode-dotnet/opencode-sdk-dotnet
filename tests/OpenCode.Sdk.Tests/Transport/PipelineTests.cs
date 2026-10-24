using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text;
using OpenCode.Sdk.Internal;
using OpenCode.Sdk.Models;
using OpenCode.Sdk.Tests.Support;
using OpenCode.Sdk.Tests.Transport.Support;

namespace OpenCode.Sdk.Tests.Transport;

public sealed class PipelineTests
{
    private static async Task ExecuteInfoAsync(Pipeline pipeline) =>
        _ = await pipeline.ExecuteAsync(OperationId, HttpMethod.Get, "/api/info", new RecordingResponseAdapter(), options: null, CancellationToken.None);

    private static async Task AssertLocationHeadersAbsentAsync(RecordedRequest request)
    {
        await Assert.That(request.Headers).DoesNotContainKey("x-opencode-directory");
        await Assert.That(request.Headers).DoesNotContainKey("x-opencode-workspace");
    }

    private static readonly Uri Endpoint = new("http://localhost:4096");
    private const string OperationId = "test.operation";

    /// <summary>
    /// The progress window the manual-clock tests run under: far beyond the hang guard, so only the
    /// test's clock can pass it, never the host's.
    /// </summary>
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(2);

    [Test]
    [Arguments("")]
    [Arguments(" ")]
    [Arguments("\t")]
    public async Task ExecuteAsync_Should_Refuse_A_Blank_Operation_Id(string operationId)
    {
        using var handler = new RecordingHttpHandler();
        using var httpClient = new HttpClient(handler);
        using var pipeline = CreatePipeline(httpClient);

        var exception = await Assert
            .That(async () => _ = await pipeline.ExecuteAsync(operationId,
                HttpMethod.Get, "/api/info", new RecordingResponseAdapter(), options: null, CancellationToken.None))
            .Throws<ArgumentException>();

        await Assert.That(exception!.ParamName).IsEqualTo(nameof(operationId));
        await Assert.That(handler.Requests).IsEmpty();
    }

    [Test]
    public async Task ExecuteAsync_Should_Join_The_Route_Onto_The_Endpoint()
    {
        using var handler = new RecordingHttpHandler();
        using var httpClient = new HttpClient(handler);
        using var pipeline = CreatePipeline(httpClient);

        _ = await pipeline.ExecuteAsync(OperationId, HttpMethod.Get, "/api/info", new RecordingResponseAdapter(), options: null, CancellationToken.None);

        await Assert.That(handler.Requests.Single().Method).IsEqualTo(HttpMethod.Get);
        await Assert.That(handler.Requests.Single().RequestUri).IsEqualTo(new Uri("http://localhost:4096/api/info"));
    }

    [Test]
    public async Task ExecuteAsync_Should_Preserve_The_Endpoint_Path_Prefix()
    {
        using var handler = new RecordingHttpHandler();
        using var httpClient = new HttpClient(handler);
        using var pipeline = CreatePipeline(httpClient, endpoint: new Uri("http://localhost:8080/opencode/"));

        _ = await pipeline.ExecuteAsync(OperationId, HttpMethod.Get, "/api/info", new RecordingResponseAdapter(), options: null, CancellationToken.None);

        await Assert.That(handler.Requests.Single().RequestUri).IsEqualTo(new Uri("http://localhost:8080/opencode/api/info"));
    }

    [Test]
    public async Task ExecuteAsync_Should_Decorate_Basic_Auth_From_The_Explicit_Password()
    {
        using var handler = new RecordingHttpHandler();
        using var httpClient = new HttpClient(handler);
        using var pipeline = CreatePipeline(httpClient, password: "secret");

        _ = await pipeline.ExecuteAsync(OperationId, HttpMethod.Get, "/api/info", new RecordingResponseAdapter(), options: null, CancellationToken.None);

        var expected = $"Basic {Convert.ToBase64String(Encoding.UTF8.GetBytes("opencode:secret"))}";
        await Assert.That(handler.Requests.Single().Authorization).IsEqualTo(expected);
    }

    [Test]
    public async Task ExecuteAsync_Should_Decorate_The_Configured_Username()
    {
        using var handler = new RecordingHttpHandler();
        using var httpClient = new HttpClient(handler);
        using var pipeline = CreatePipeline(httpClient, password: "secret", username: "admin");

        _ = await pipeline.ExecuteAsync(OperationId, HttpMethod.Get, "/api/info", new RecordingResponseAdapter(), options: null, CancellationToken.None);

        var expected = $"Basic {Convert.ToBase64String(Encoding.UTF8.GetBytes("admin:secret"))}";
        await Assert.That(handler.Requests.Single().Authorization).IsEqualTo(expected);
    }

    [Test]
    public async Task Pipeline_Should_Snapshot_The_Options_At_Construction()
    {
        using var handler = new RecordingHttpHandler();
        using var httpClient = new HttpClient(handler);
        var options = new OpenCodeClientOptions
        {
            Endpoint = Endpoint,
            Password = "secret",
        };
        using var pipeline = new Pipeline(httpClient, ownsHttpClient: false, options);
        options.Password = "changed";
        options.Endpoint = new Uri("http://other:1");

        _ = await pipeline.ExecuteAsync(OperationId, HttpMethod.Get, "/api/info", new RecordingResponseAdapter(), options: null, CancellationToken.None);

        var expected = $"Basic {Convert.ToBase64String(Encoding.UTF8.GetBytes("opencode:secret"))}";
        await Assert.That(handler.Requests.Single().Authorization).IsEqualTo(expected);
        await Assert.That(handler.Requests.Single().RequestUri).IsEqualTo(new Uri("http://localhost:4096/api/info"));
    }

    /// <summary>
    /// Only null means no credential, as in the pinned client, which sends any password it holds:
    /// a server configured with a whitespace password runs with exactly that one.
    /// </summary>
    [Test]
    [Arguments("")]
    [Arguments(" ")]
    [Arguments("\t")]
    public async Task ExecuteAsync_Should_Send_A_Blank_Explicit_Password_As_Written(string password)
    {
        using var handler = new RecordingHttpHandler();
        using var httpClient = new HttpClient(handler);
        using var pipeline = CreatePipeline(httpClient, password: password);

        _ = await pipeline.ExecuteAsync(OperationId, HttpMethod.Get, "/api/info", new RecordingResponseAdapter(), options: null, CancellationToken.None);

        var expected = $"Basic {Convert.ToBase64String(Encoding.UTF8.GetBytes("opencode:" + password))}";
        await Assert.That(handler.Requests.Single().Authorization).IsEqualTo(expected);
    }

    [Test]
    [Arguments("")]
    [Arguments(" ")]
    public async Task Pipeline_Should_Refuse_A_Blank_Username(string username)
    {
        using var handler = new RecordingHttpHandler();
        using var httpClient = new HttpClient(handler);

        var exception = Assert.Throws<ArgumentException>(() =>
            _ = CreatePipeline(httpClient, password: "secret", username: username));

        await Assert.That(exception.Message).Contains("username");
    }

    [Test]
    public async Task Pipeline_Should_Refuse_A_Username_Containing_A_Colon()
    {
        using var handler = new RecordingHttpHandler();
        using var httpClient = new HttpClient(handler);

        var exception = Assert.Throws<ArgumentException>(() =>
            _ = CreatePipeline(httpClient, password: "secret", username: "open:code"));

        await Assert.That(exception.Message).Contains("colon");
    }

    [Test]
    public async Task ExecuteAsync_Should_Send_Anonymously_When_No_Password_Is_Set()
    {
        using var handler = new RecordingHttpHandler();
        using var httpClient = new HttpClient(handler);
        using var pipeline = CreatePipeline(httpClient);

        _ = await pipeline.ExecuteAsync(OperationId, HttpMethod.Get, "/api/info", new RecordingResponseAdapter(), options: null, CancellationToken.None);

        await Assert.That(handler.Requests.Single().Authorization).IsNull();
    }

    [Test]
    public async Task ExecuteAsync_Should_Let_The_Explicit_Password_Win_Over_A_Default_Authorization()
    {
        using var handler = new RecordingHttpHandler();
        using var httpClient = new HttpClient(handler);
        httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "foreign-token");
        using var pipeline = CreatePipeline(httpClient, password: "secret");

        _ = await pipeline.ExecuteAsync(OperationId, HttpMethod.Get, "/api/info", new RecordingResponseAdapter(), options: null, CancellationToken.None);

        var expected = $"Basic {Convert.ToBase64String(Encoding.UTF8.GetBytes("opencode:secret"))}";
        await Assert.That(handler.Requests.Single().Authorization).IsEqualTo(expected);
    }

    [Test]
    public async Task ExecuteAsync_Should_Decorate_The_Ambient_Location_Headers()
    {
        using var handler = new RecordingHttpHandler();
        using var httpClient = new HttpClient(handler);
        using var pipeline = CreatePipeline(httpClient, location: new LocationSelector
        {
            Directory = "/repo"
        });

        await ExecuteInfoAsync(pipeline);

        var request = handler.Requests.Single();
        await Assert.That(request.Headers["x-opencode-directory"]).IsEqualTo("%2Frepo");
        await Assert.That(request.Headers).DoesNotContainKey("x-opencode-workspace");
    }

    [Test]
    public async Task ExecuteAsync_Should_Send_No_Location_Header_For_An_Empty_Selector()
    {
        using var handler = new RecordingHttpHandler();
        using var httpClient = new HttpClient(handler);
        using var pipeline = CreatePipeline(httpClient, location: new LocationSelector());

        await ExecuteInfoAsync(pipeline);

        await AssertLocationHeadersAbsentAsync(handler.Requests.Single());
    }

    [Test]
    public async Task ExecuteAsync_Should_Escape_A_Directory_Whose_Name_Contains_A_Percent_Sequence()
    {
        using var handler = new RecordingHttpHandler();
        using var httpClient = new HttpClient(handler);
        using var pipeline = CreatePipeline(httpClient, location: new LocationSelector
        {
            Directory = "/data/reports%20q3",
        });

        _ = await pipeline.ExecuteAsync(OperationId, HttpMethod.Get, "/api/info", new RecordingResponseAdapter(), options: null, CancellationToken.None);

        // The server decodes this header, so an unescaped '%20' would arrive as a space and
        // silently address a different directory.
        await Assert.That(handler.Requests.Single().Headers["x-opencode-directory"])
            .IsEqualTo("%2Fdata%2Freports%2520q3");
    }

    [Test]
    public async Task ExecuteAsync_Should_Send_A_Non_Ascii_Directory_As_Ascii()
    {
        using var handler = new RecordingHttpHandler();
        using var httpClient = new HttpClient(handler);
        using var pipeline = CreatePipeline(httpClient, location: new LocationSelector
        {
            Directory = "/workspace/café",
        });

        _ = await pipeline.ExecuteAsync(OperationId, HttpMethod.Get, "/api/info", new RecordingResponseAdapter(), options: null, CancellationToken.None);

        // Header values above Latin-1 are unsendable, so the escape is what keeps a
        // non-ASCII path on the wire at all.
        await Assert.That(handler.Requests.Single().Headers["x-opencode-directory"])
            .IsEqualTo("%2Fworkspace%2Fcaf%C3%A9");
    }

    [Test]
    public async Task ExecuteAsync_Should_Omit_The_Location_Headers_Without_An_Ambient_Location()
    {
        using var handler = new RecordingHttpHandler();
        using var httpClient = new HttpClient(handler);
        using var pipeline = CreatePipeline(httpClient);

        await ExecuteInfoAsync(pipeline);

        await AssertLocationHeadersAbsentAsync(handler.Requests.Single());
    }

    [Test]
    public async Task ExecuteAsync_Should_Snapshot_The_Ambient_Location_At_Construction()
    {
        var options = new OpenCodeClientOptions
        {
            Endpoint = Endpoint,
            Location = new LocationSelector { Directory = "/before" },
        };
        using var handler = new RecordingHttpHandler();
        using var httpClient = new HttpClient(handler);
        using var pipeline = Pipeline.Create(httpClient, options);
        options.Location = new LocationSelector { Directory = "/after" };

        _ = await pipeline.ExecuteAsync(OperationId, HttpMethod.Get, "/api/info", new RecordingResponseAdapter(), options: null, CancellationToken.None);

        await Assert.That(handler.Requests.Single().Headers["x-opencode-directory"]).IsEqualTo("%2Fbefore");
    }

    [Test]
    public async Task ExecuteAsync_Should_Decorate_The_User_Agent_Per_Request()
    {
        using var handler = new RecordingHttpHandler();
        using var httpClient = new HttpClient(handler);
        using var pipeline = CreatePipeline(httpClient);

        _ = await pipeline.ExecuteAsync(OperationId, HttpMethod.Get, "/api/info", new RecordingResponseAdapter(), options: null, CancellationToken.None);

        await Assert.That(handler.Requests.Single().UserAgent).IsEqualTo(UserAgentPolicy.Resolve().ToString());
    }

    [Test]
    public async Task ExecuteAsync_Should_Never_Touch_The_Client_Defaults()
    {
        using var handler = new RecordingHttpHandler();
        using var httpClient = new HttpClient(handler);
        using var pipeline = CreatePipeline(httpClient, password: "secret");

        _ = await pipeline.ExecuteAsync(OperationId, HttpMethod.Get, "/api/info", new RecordingResponseAdapter(), options: null, CancellationToken.None);

        await Assert.That(httpClient.BaseAddress).IsNull();
        await Assert.That(httpClient.DefaultRequestHeaders).IsEmpty();
    }

    [Test]
    public async Task ExecuteAsync_Should_Send_The_Json_Body_With_The_Json_Content_Type()
    {
        using var handler = new RecordingHttpHandler();
        using var httpClient = new HttpClient(handler);
        using var pipeline = CreatePipeline(httpClient);

        _ = await pipeline.ExecuteAsync(OperationId,
            HttpMethod.Post,
            "/api/session",
            new TestBody { Value = "created" },
            TestBodyJsonContext.Default.TestBody,
            new RecordingResponseAdapter(),
            options: null,
            CancellationToken.None);

        await Assert.That(handler.Requests.Single().Method).IsEqualTo(HttpMethod.Post);
        await Assert.That(handler.Requests.Single().ContentType).IsEqualTo("application/json");
        await Assert.That(handler.Requests.Single().Body).IsEqualTo("""{"value":"created"}""");
    }

    [Test]
    public async Task ExecuteAsync_Should_Keep_The_Bodyless_Path_Body_Free()
    {
        using var handler = new RecordingHttpHandler();
        using var httpClient = new HttpClient(handler);
        using var pipeline = CreatePipeline(httpClient);

        _ = await pipeline.ExecuteAsync(OperationId, HttpMethod.Get, "/api/info", new RecordingResponseAdapter(), options: null, CancellationToken.None);

        await Assert.That(handler.Requests.Single().Body).IsNull();
        await Assert.That(handler.Requests.Single().ContentType).IsNull();
    }

    [Test]
    public async Task ExecuteAsync_Should_Guard_The_Body_Arguments()
    {
        using var handler = new RecordingHttpHandler();
        using var httpClient = new HttpClient(handler);
        using var pipeline = CreatePipeline(httpClient);

        _ = await Assert
            .That(async () => _ = await pipeline.ExecuteAsync(OperationId,
                HttpMethod.Post, "/api/session", null!, TestBodyJsonContext.Default.TestBody,
                new RecordingResponseAdapter(), options: null, CancellationToken.None))
            .Throws<ArgumentNullException>();
        _ = await Assert
            .That(async () => _ = await pipeline.ExecuteAsync(OperationId,
                HttpMethod.Post, "/api/session", new TestBody { Value = "created" }, bodyTypeInfo: null!,
                new RecordingResponseAdapter(), options: null, CancellationToken.None))
            .Throws<ArgumentNullException>();
        await Assert.That(handler.Requests).IsEmpty();
    }

    [Test]
    public async Task ExecuteAsync_Should_Guard_The_Route_On_The_Body_Path()
    {
        using var handler = new RecordingHttpHandler();
        using var httpClient = new HttpClient(handler);
        using var pipeline = CreatePipeline(httpClient);

        var exception = await Assert
            .That(async () => _ = await pipeline.ExecuteAsync(OperationId,
                HttpMethod.Post, "api/session", new TestBody { Value = "created" }, TestBodyJsonContext.Default.TestBody,
                new RecordingResponseAdapter(), options: null, CancellationToken.None))
            .Throws<ArgumentException>();

        await Assert.That(exception!.Message).Contains("Routes must start with '/'");
        await Assert.That(handler.Requests).IsEmpty();
    }

    [Test]
    public async Task ExecuteAsync_Should_Buffer_The_Body_For_The_Adapter()
    {
        using var handler = new RecordingHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"healthy\":true}"),
        });
        using var httpClient = new HttpClient(handler);
        using var pipeline = CreatePipeline(httpClient);
        var adapter = new RecordingResponseAdapter();

        var response = await pipeline.ExecuteAsync(OperationId, HttpMethod.Get, "/api/info", adapter, options: null, CancellationToken.None);

        await Assert.That(adapter.AdaptedStatus).IsEqualTo(200);
        await Assert.That(adapter.AdaptedRawBody).IsEqualTo("{\"healthy\":true}");
        await Assert.That(response.Status).IsEqualTo(200);
    }

    [Test]
    public async Task ExecuteAsync_Should_Throw_The_Api_Exception_By_Default()
    {
        using var handler = new RecordingHttpHandler(static _ => new HttpResponseMessage(HttpStatusCode.Unauthorized)
        {
            Content = new StringContent("{\"_tag\":\"UnauthorizedError\"}"),
        });
        using var httpClient = new HttpClient(handler);
        using var pipeline = CreatePipeline(httpClient);
        var error = new UnauthorizedError
        {
            Message = "password required",
        };
        var adapter = new RecordingResponseAdapter((status, rawBody) => new TestResponse
        {
            Status = status,
            IsError = true,
            Error = error,
            RawBody = rawBody,
        });

        var exception = await Assert
            .That(async () => _ = await pipeline.ExecuteAsync(OperationId, HttpMethod.Get, "/api/info", adapter, options: null, CancellationToken.None))
            .Throws<OpenCodeApiException>();

        await Assert.That(exception!.Status).IsEqualTo(401);
        await Assert.That(exception.Error).IsSameReferenceAs(error);
        await Assert.That(exception.RawBody).IsEqualTo("{\"_tag\":\"UnauthorizedError\"}");
        await Assert.That(exception.Message).Contains("401");
        await Assert.That(exception.Message).Contains("UnauthorizedError");
    }

    [Test]
    public async Task ExecuteAsync_Should_Return_The_Error_Envelope_When_NoThrow()
    {
        using var handler = new RecordingHttpHandler(static _ => new HttpResponseMessage(HttpStatusCode.Unauthorized)
        {
            Content = new StringContent("{\"_tag\":\"UnauthorizedError\"}"),
        });
        using var httpClient = new HttpClient(handler);
        using var pipeline = CreatePipeline(httpClient);
        var adapter = new RecordingResponseAdapter(static (status, rawBody) => new TestResponse
        {
            Status = status,
            IsError = true,
            RawBody = rawBody,
        });

        var response = await pipeline.ExecuteAsync(OperationId,
            HttpMethod.Get,
            "/api/info",
            adapter,
            OpenCodeRequestOptions.NoThrow,
            CancellationToken.None);

        await Assert.That(response.IsError).IsTrue();
        await Assert.That(response.Status).IsEqualTo(401);
        await Assert.That(response.RawBody).IsEqualTo("{\"_tag\":\"UnauthorizedError\"}");
    }

    [Test]
    public async Task ExecuteAsync_Should_Wrap_Network_Failures_As_Transport_Failures()
    {
        using var handler = new RecordingHttpHandler(static _ => throw new HttpRequestException("connection refused"));
        using var httpClient = new HttpClient(handler);
        using var pipeline = CreatePipeline(httpClient);

        var exception = await Assert
            .That(async () => _ = await pipeline.ExecuteAsync(OperationId,
                HttpMethod.Get, "/api/info", new RecordingResponseAdapter(), options: null, CancellationToken.None))
            .Throws<OpenCodeTransportException>();

        await Assert.That(exception!.InnerException).IsTypeOf<HttpRequestException>();
    }

    [Test]
    public async Task ExecuteAsync_Should_Wrap_An_Invalid_Charset_As_A_Transport_Failure()
    {
        using var handler = new RecordingHttpHandler(static _ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent("{\"healthy\":true}"u8.ToArray()),
            };
            response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "not-an-encoding" };
            return response;
        });
        using var httpClient = new HttpClient(handler);
        using var pipeline = CreatePipeline(httpClient);

        var exception = await Assert
            .That(async () => _ = await pipeline.ExecuteAsync(OperationId,
                HttpMethod.Get, "/api/info", new RecordingResponseAdapter(), options: null, CancellationToken.None))
            .Throws<OpenCodeTransportException>();

        await Assert.That(exception!.InnerException).IsTypeOf<InvalidOperationException>();
    }

#if NET
    [Test]
    public async Task ExecuteAsync_Should_Wrap_An_Unsupported_Success_Charset_As_A_Transport_Failure()
    {
        using var handler = new RecordingHttpHandler(static _ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(Encoding.UTF8.GetBytes(WireBodyData.InfoOk)),
            };
            response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-7" };
            return response;
        });
        using var httpClient = new HttpClient(handler);
        using var pipeline = CreatePipeline(httpClient);

        var exception = await Assert
            .That(async () => _ = await pipeline.ExecuteAsync(OperationId,
                HttpMethod.Get, "/api/info", new RecordingResponseAdapter(), options: null, CancellationToken.None))
            .Throws<OpenCodeTransportException>();

        await Assert.That(exception!.InnerException).IsTypeOf<InvalidOperationException>();
        await Assert.That(exception.InnerException!.InnerException).IsTypeOf<NotSupportedException>();
    }

    [Test]
    public async Task ExecuteAsync_Should_Wrap_An_Unsupported_Error_Charset_As_A_Transport_Failure()
    {
        using var handler = new RecordingHttpHandler(static _ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.Unauthorized)
            {
                Content = new ByteArrayContent(Encoding.UTF8.GetBytes(WireBodyData.UnauthorizedError)),
            };
            response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-7" };
            return response;
        });
        using var httpClient = new HttpClient(handler);
        using var pipeline = CreatePipeline(httpClient);

        var exception = await Assert
            .That(async () => _ = await pipeline.ExecuteAsync(OperationId,
                HttpMethod.Get, "/api/info", new RecordingResponseAdapter(), options: null, CancellationToken.None))
            .Throws<OpenCodeTransportException>();

        await Assert.That(exception!.InnerException).IsTypeOf<InvalidOperationException>();
        await Assert.That(exception.InnerException!.InnerException).IsTypeOf<NotSupportedException>();
    }
#endif

#if NET472
    [Test]
    public async Task ExecuteAsync_Should_Decode_A_Utf7_Success_Body_On_Net472()
    {
        var encoding = Encoding.GetEncoding("utf-7");
        using var handler = new RecordingHttpHandler(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(encoding.GetBytes(WireBodyData.InfoOk)),
            };
            response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-7" };
            return response;
        });
        using var httpClient = new HttpClient(handler);
        using var pipeline = CreatePipeline(httpClient);
        var adapter = new RecordingResponseAdapter();

        _ = await pipeline.ExecuteAsync(OperationId,
            HttpMethod.Get, "/api/info", adapter, options: null, CancellationToken.None);

        await Assert.That(adapter.AdaptedRawBody).IsEqualTo(WireBodyData.InfoOk);
    }

    [Test]
    public async Task ExecuteAsync_Should_Decode_A_Utf7_Error_Body_On_Net472()
    {
        var encoding = Encoding.GetEncoding("utf-7");
        using var handler = new RecordingHttpHandler(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.Unauthorized)
            {
                Content = new ByteArrayContent(encoding.GetBytes(WireBodyData.UnauthorizedError)),
            };
            response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-7" };
            return response;
        });
        using var httpClient = new HttpClient(handler);
        using var pipeline = CreatePipeline(httpClient);
        var adapter = new RecordingResponseAdapter(static (status, rawBody) => new TestResponse
        {
            Status = status,
            IsError = true,
            RawBody = rawBody,
        });

        var response = await pipeline.ExecuteAsync(OperationId,
            HttpMethod.Get, "/api/info", adapter, OpenCodeRequestOptions.NoThrow, CancellationToken.None);

        await Assert.That(response.RawBody).IsEqualTo(WireBodyData.UnauthorizedError);
    }
#endif

    [Test]
    public async Task ExecuteAsync_Should_Decode_A_Bom_Selected_Utf16_Success_Body()
    {
        var preamble = Encoding.Unicode.GetPreamble();
        var payload = Encoding.Unicode.GetBytes(WireBodyData.InfoOk);
        using var handler = new RecordingHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent([.. preamble, .. payload]),
        });
        using var httpClient = new HttpClient(handler);
        using var pipeline = CreatePipeline(httpClient);
        var adapter = new RecordingResponseAdapter();

        _ = await pipeline.ExecuteAsync(OperationId,
            HttpMethod.Get, "/api/info", adapter, options: null, CancellationToken.None);

        await Assert.That(adapter.AdaptedRawBody).IsEqualTo(WireBodyData.InfoOk);
    }

    [Test]
    public async Task ExecuteAsync_Should_Strip_A_Utf8_Bom_From_A_Success_Body()
    {
        var body = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(WireBodyData.InfoOk)).ToArray();
        using var handler = new RecordingHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(body),
        });
        using var httpClient = new HttpClient(handler);
        using var pipeline = CreatePipeline(httpClient);
        var adapter = new RecordingResponseAdapter();

        _ = await pipeline.ExecuteAsync(OperationId,
            HttpMethod.Get, "/api/info", adapter, options: null, CancellationToken.None);

        await Assert.That(adapter.AdaptedRawBody).IsEqualTo(WireBodyData.InfoOk);
    }

    [Test]
    [Arguments("utf-8")]
    [Arguments("\"utf-8\"")]
    public async Task ExecuteAsync_Should_Decode_A_Declared_Utf8_Success_Body(string charset)
    {
        using var handler = new RecordingHttpHandler(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(Encoding.UTF8.GetBytes(WireBodyData.InfoOk)),
            };
            response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = charset };
            return response;
        });
        using var httpClient = new HttpClient(handler);
        using var pipeline = CreatePipeline(httpClient);
        var adapter = new RecordingResponseAdapter();

        _ = await pipeline.ExecuteAsync(OperationId,
            HttpMethod.Get, "/api/info", adapter, options: null, CancellationToken.None);

        await Assert.That(adapter.AdaptedRawBody).IsEqualTo(WireBodyData.InfoOk);
    }

    [Test]
    public async Task ExecuteAsync_Should_Return_An_Empty_Success_Body_Before_Validating_The_Charset()
    {
        using var handler = new RecordingHttpHandler(static _ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent([]),
            };
            response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "not-an-encoding" };
            return response;
        });
        using var httpClient = new HttpClient(handler);
        using var pipeline = CreatePipeline(httpClient);
        var adapter = new RecordingResponseAdapter();

        _ = await pipeline.ExecuteAsync(OperationId,
            HttpMethod.Get, "/api/info", adapter, options: null, CancellationToken.None);

        await Assert.That(adapter.AdaptedRawBody).IsEmpty();
    }

    [Test]
    public async Task ExecuteAsync_Should_Decode_A_Quoted_Utf8_Error_Body()
    {
        using var handler = new RecordingHttpHandler(static _ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.Unauthorized)
            {
                Content = new ByteArrayContent(Encoding.UTF8.GetBytes(WireBodyData.UnauthorizedError)),
            };
            response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "\"utf-8\"" };
            return response;
        });
        using var httpClient = new HttpClient(handler);
        using var pipeline = CreatePipeline(httpClient);
        var adapter = new RecordingResponseAdapter(static (status, rawBody) => new TestResponse
        {
            Status = status,
            IsError = true,
            RawBody = rawBody,
        });

        var response = await pipeline.ExecuteAsync(OperationId,
            HttpMethod.Get, "/api/info", adapter, OpenCodeRequestOptions.NoThrow, CancellationToken.None);

        await Assert.That(response.RawBody).IsEqualTo(WireBodyData.UnauthorizedError);
    }

    [Test]
    public async Task ExecuteAsync_Should_Preserve_Replacement_Decoding_For_Malformed_Utf8()
    {
        using var handler = new RecordingHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(WireBodyData.InfoWithMalformedUtf8UnknownField()),
        });
        using var httpClient = new HttpClient(handler);
        using var pipeline = CreatePipeline(httpClient);

        var response = await pipeline.ExecuteAsync(OperationId,
            HttpMethod.Get, "/api/info", ServerInfoResponseAdapter.Instance, options: null, CancellationToken.None);

        await Assert.That(response.Status).IsEqualTo(200);
    }

    [Test]
    public async Task ExecuteAsync_Should_Wrap_A_Disposal_Race_On_Send_As_A_Transport_Failure()
    {
        using var handler = new RecordingHttpHandler(static _ => throw new ObjectDisposedException(nameof(HttpClient)));
        using var httpClient = new HttpClient(handler);
        using var pipeline = CreatePipeline(httpClient);

        var exception = await Assert
            .That(async () => _ = await pipeline.ExecuteAsync(OperationId,
                HttpMethod.Get, "/api/info", new RecordingResponseAdapter(), options: null, CancellationToken.None))
            .Throws<OpenCodeTransportException>();

        await Assert.That(exception!.InnerException).IsTypeOf<ObjectDisposedException>();
    }

    [Test]
    public async Task ExecuteAsync_Should_Refuse_An_Undefined_Error_Behavior_Before_Sending()
    {
        using var handler = new RecordingHttpHandler();
        using var httpClient = new HttpClient(handler);
        using var pipeline = CreatePipeline(httpClient);
        var options = new OpenCodeRequestOptions { ErrorBehavior = (ErrorBehavior)2 };

        _ = await Assert
            .That(async () => _ = await pipeline.ExecuteAsync(OperationId,
                HttpMethod.Get, "/api/info", new RecordingResponseAdapter(), options, CancellationToken.None))
            .Throws<ArgumentOutOfRangeException>();

        await Assert.That(handler.Requests).IsEmpty();
    }

    [Test]
    public async Task ExecuteAsync_Should_Pass_Cancellation_Through()
    {
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelOnWorkerAsync();
        using var handler = new RecordingHttpHandler(_ => throw new OperationCanceledException(cancellation.Token));
        using var httpClient = new HttpClient(handler);
        using var pipeline = CreatePipeline(httpClient);

        _ = await Assert
            .That(async () => _ = await pipeline.ExecuteAsync(OperationId,
                HttpMethod.Get, "/api/info", new RecordingResponseAdapter(), options: null, cancellation.Token))
            .Throws<OperationCanceledException>();

        await Assert.That(handler.CancellationTokens.Single().IsCancellationRequested).IsTrue();
    }

    [Test]
    public async Task ExecuteAsync_Should_Preserve_The_Caller_Token_During_Send_Cancellation()
    {
        using var caller = new CancellationTokenSource();
        using var source = new GatedStream([0x41], chunkCount: 1);
        using var handler = new RecordingHttpHandler();
        using var httpClient = new HttpClient(handler);
        using var pipeline = CreatePipeline(httpClient);

        async Task CancelAtReadAsync()
        {
            try
            {
                await source.WaitForReadAsync(HangGuard.Bound);
            }
            finally
            {
                await caller.CancelOnWorkerAsync();
            }
        }

        var canceling = CancelAtReadAsync();
        OperationCanceledException? canceled = null;
        try
        {
            _ = await pipeline.ExecuteAsync(OperationId, HttpMethod.Post, "/api/experimental/fs/write?path=a.bin",
                source, new RecordingResponseAdapter(), options: null, caller.Token);
        }
        catch (OperationCanceledException exception)
        {
            canceled = exception;
        }
        finally
        {
            await canceling;
        }

        await Assert.That(canceled).IsNotNull();
        await Assert.That(canceled!.CancellationToken).IsEqualTo(caller.Token);
        await Assert.That(canceled.InnerException).IsNotNull();
        await Assert.That(source.CanRead).IsTrue();
        await Assert.That(handler.Requests).IsEmpty();
    }

    [Test]
    public async Task ExecuteAsync_Should_Wrap_A_Timeout_Cancellation_As_A_Transport_Failure()
    {
        using var handler = new RecordingHttpHandler(static _ => throw new TaskCanceledException());
        using var httpClient = new HttpClient(handler);
        using var pipeline = CreatePipeline(httpClient);

        var exception = await Assert
            .That(async () => _ = await pipeline.ExecuteAsync(OperationId,
                HttpMethod.Get, "/api/info", new RecordingResponseAdapter(), options: null, CancellationToken.None))
            .Throws<OpenCodeTransportException>();

        await Assert.That(exception!.InnerException).IsTypeOf<TaskCanceledException>();
    }

    [Test]
    public async Task ExecuteAsync_Should_Wrap_A_Timeout_During_The_Body_Read_As_A_Transport_Failure()
    {
        using var handler = new RecordingHttpHandler(static _ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new TimeoutContent(),
        });
        using var httpClient = new HttpClient(handler);
        using var pipeline = CreatePipeline(httpClient);

        var exception = await Assert
            .That(async () => _ = await pipeline.ExecuteAsync(OperationId,
                HttpMethod.Get, "/api/info", new RecordingResponseAdapter(), options: null, CancellationToken.None))
            .Throws<OpenCodeTransportException>();

        await Assert.That(exception!.InnerException).IsTypeOf<TaskCanceledException>();
    }

    [Test]
    public async Task ExecuteAsync_Should_Fail_A_Body_That_Stalls_Past_The_Progress_Window()
    {
        using var caller = new CancellationTokenSource();
        var time = new ManualTimeProvider();
        using var content = new BlockingContent();
        using var handler = new RecordingHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = content,
        });
        using var httpClient = new HttpClient(handler);
        using var pipeline = CreatePipeline(httpClient, networkTimeout: Window, time: time);

        var execution = pipeline.ExecuteAsync(OperationId,
                HttpMethod.Get,
                "/api/info",
                new RecordingResponseAdapter(),
                options: null,
                caller.Token);
        await content.ReadStarted.WaitAsync(HangGuard.Bound);
        await time.WaitForTimerAsync(HangGuard.Bound);
        time.Advance(Window);

        var exception = await Assert.That(execution.WaitAsync(HangGuard.Bound)).Throws<OpenCodeTransportException>();

        // The window fires as a cancellation the caller never requested, so it maps to a
        // transport failure while the caller's token stays untouched. The inner exception is
        // whichever face the interrupt wore first — the classification table pins the exact
        // type mappings.
        await Assert.That(exception!.InnerException).IsNotNull();
        await Assert.That(caller.IsCancellationRequested).IsFalse();
        await content.ReadCompleted.WaitAsync(HangGuard.Bound);
        await Assert.That(content.IsDisposed).IsTrue();
    }

    /// <summary>
    /// Under progress semantics a slow body that keeps moving survives: every chunk arrives just
    /// inside the window, and the body as a whole outlives it several times over. The window runs on
    /// the test's clock, which moves only while a read waits, so no scheduling delay counts.
    /// </summary>
    [Test]
    public async Task ExecuteAsync_Should_Keep_A_Trickling_Body_That_Outlives_The_Window()
    {
        using var body = CreateGatedBody(out var content, out var time, out var scenario);
        using (content)
        using (scenario)
        {
            var started = time.GetUtcNow();
            var adapter = new RecordingResponseAdapter();

            var delivering = body.DeliverAsync(() => time.Advance(Window - TimeSpan.FromTicks(1)), HangGuard.Bound);
            _ = await scenario.Pipeline.ExecuteAsync(OperationId, HttpMethod.Get, "/api/info", adapter, options: null, CancellationToken.None).WaitAsync(HangGuard.Bound);
            await delivering;

            await Assert.That(adapter.AdaptedRawBody).IsEqualTo("{\"healthy\":true}");
            await Assert.That(time.GetUtcNow() - started).IsGreaterThan(TimeSpan.FromTicks(Window.Ticks * 3));
        }
    }

    /// <summary>
    /// The twin of the trickling body: a read that waits out the whole window fails the exchange,
    /// so the window the trickling body survives does run on the test's clock.
    /// </summary>
    [Test]
    public async Task ExecuteAsync_Should_Fail_A_Body_Read_That_Waits_Out_The_Window()
    {
        using var body = CreateGatedBody(out var content, out var time, out var scenario);
        using (content)
        using (scenario)
        {
            var execution = scenario.Pipeline.ExecuteAsync(OperationId, HttpMethod.Get, "/api/info", new RecordingResponseAdapter(), options: null, CancellationToken.None);
            await body.WaitForReadAsync(HangGuard.Bound);
            body.Release();
            await body.WaitForReadAsync(HangGuard.Bound);
            time.Advance(Window);

            var exception = await Assert.That(execution.WaitAsync(HangGuard.Bound)).Throws<OpenCodeTransportException>();
            await Assert.That(exception!.Message).IsEqualTo("The opencode response body could not be read.");
        }
    }

    [Test]
    public async Task ExecuteAsync_Should_Fail_A_Stalled_Error_Body_At_The_Progress_Window_Under_NoThrow()
    {
        using var caller = new CancellationTokenSource();
        var time = new ManualTimeProvider();
        using var content = new BlockingContent();
        using var handler = new RecordingHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized)
        {
            Content = content,
        });
        using var httpClient = new HttpClient(handler);
        using var pipeline = CreatePipeline(httpClient, networkTimeout: Window, time: time);

        var execution = pipeline.ExecuteAsync(OperationId,
                HttpMethod.Get,
                "/api/info",
                new RecordingResponseAdapter(),
                OpenCodeRequestOptions.NoThrow,
                caller.Token);
        await content.ReadStarted.WaitAsync(HangGuard.Bound);
        await time.WaitForTimerAsync(HangGuard.Bound);
        time.Advance(Window);

        _ = await Assert.That(execution.WaitAsync(HangGuard.Bound)).Throws<OpenCodeTransportException>();

        await Assert.That(caller.IsCancellationRequested).IsFalse();
        await content.ReadCompleted.WaitAsync(HangGuard.Bound);
        await Assert.That(content.IsDisposed).IsTrue();
    }

    [Test]
    [NotInParallel]
    public async Task ExecuteAsync_Should_Settle_The_Body_Read_After_Caller_Cancellation()
    {
        using var callerCancellation = new CancellationTokenSource();
        using var content = new BlockingContent();
        using var handler = new RecordingHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = content,
        });
        using var httpClient = new HttpClient(handler);
        using var pipeline = CreatePipeline(httpClient);

        var execution = pipeline.ExecuteAsync(OperationId,
            HttpMethod.Get,
            "/api/info",
            new RecordingResponseAdapter(),
            options: null,
            callerCancellation.Token);
        await content.ReadStarted.WaitAsync(TimeSpan.FromSeconds(5));
        await callerCancellation.CancelOnWorkerAsync();

        OperationCanceledException? cancellation = null;
        try
        {
            _ = await execution;
        }
        catch (OperationCanceledException exception)
        {
            cancellation = exception;
        }

        await Assert.That(cancellation).IsNotNull();
        await content.ReadCompleted.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.That(content.IsDisposed).IsTrue();
    }

    [Test]
    [NotInParallel]
    public async Task ExecuteAsync_Should_Classify_A_Fault_The_Interruption_Caused_As_Caller_Cancellation()
    {
        using var callerCancellation = new CancellationTokenSource();
        using var content = new InterruptedBufferingContent();
        using var handler = new RecordingHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = content,
        });
        using var httpClient = new HttpClient(handler);
        using var pipeline = CreatePipeline(httpClient);

        var execution = pipeline.ExecuteAsync(OperationId,
            HttpMethod.Get,
            "/api/info",
            new RecordingResponseAdapter(),
            options: null,
            callerCancellation.Token);
        await content.ReadStarted.WaitAsync(TimeSpan.FromSeconds(5));
        await callerCancellation.CancelOnWorkerAsync();

        OperationCanceledException? cancellation = null;
        try
        {
            _ = await execution;
        }
        catch (OperationCanceledException exception)
        {
            cancellation = exception;
        }

        await Assert.That(cancellation).IsNotNull();
#if NET
        // The BCL's own read-stream path surfaces the torn-down buffer's fault, and the
        // interruption classifies it. Downlevel, the polyfilled overload races the read against
        // the token and its cancellation wins first, so the fault never surfaces there.
        await Assert.That(cancellation!.InnerException).IsTypeOf<ArgumentNullException>();
#endif
        await content.ReadCompleted.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.That(content.IsDisposed).IsTrue();
    }

    [Test]
    public async Task ExecuteAsync_Should_Classify_A_Fault_The_Interruption_Caused_As_A_Transport_Failure_At_The_Progress_Window()
    {
        using var caller = new CancellationTokenSource();
        var time = new ManualTimeProvider();
        using var content = new InterruptedBufferingContent();
        using var handler = new RecordingHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = content,
        });
        using var httpClient = new HttpClient(handler);
        using var pipeline = CreatePipeline(httpClient, networkTimeout: Window, time: time);

        var execution = pipeline.ExecuteAsync(OperationId,
                HttpMethod.Get,
                "/api/info",
                new RecordingResponseAdapter(),
                options: null,
                caller.Token);
        await content.ReadStarted.WaitAsync(HangGuard.Bound);
        await time.WaitForTimerAsync(HangGuard.Bound);
        time.Advance(Window);

        var failure = await Assert.That(execution.WaitAsync(HangGuard.Bound)).Throws<OpenCodeTransportException>();

        await Assert.That(caller.IsCancellationRequested).IsFalse();
        await Assert.That(failure!.InnerException).IsNotNull();
#if NET
        await Assert.That(failure.InnerException).IsTypeOf<ArgumentNullException>();
#endif
        await content.ReadCompleted.WaitAsync(HangGuard.Bound);
        await Assert.That(content.IsDisposed).IsTrue();
    }

    [Test]
    public Task ExecuteAsync_Should_Report_Client_Disposal_While_Awaiting_Headers() =>
        AssertDisposalWhileAwaitingHeadersAsync(ownsHttpClient: true, cancelCaller: false);

    [Test]
    public Task ExecuteAsync_Should_Report_Client_Disposal_While_Awaiting_Headers_With_An_Unowned_HttpClient() =>
        AssertDisposalWhileAwaitingHeadersAsync(ownsHttpClient: false, cancelCaller: false);

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public Task ExecuteAsync_Should_Prefer_Caller_Cancellation_Over_Client_Disposal_While_Awaiting_Headers(bool ownsHttpClient) =>
        AssertDisposalWhileAwaitingHeadersAsync(ownsHttpClient, cancelCaller: true);

    private static async Task AssertDisposalWhileAwaitingHeadersAsync(bool ownsHttpClient, bool cancelCaller)
    {
        using var listener = new HeldHeadersListener();
        using var caller = new CancellationTokenSource();
        using var httpClient = new HttpClient();
        using var pipeline = CreatePipeline(httpClient, ownsHttpClient, listener.Endpoint);

        var execution = PipelineStreamTests.CaptureFailureAsync(() => pipeline.ExecuteAsync(
            OperationId, HttpMethod.Get, "/api/info", new RecordingResponseAdapter(), options: null, caller.Token));
        try
        {
            using var peer = await listener.AcceptAsync().WaitAsync(HangGuard.Bound);
            var request = new byte[4096];
            var read = await peer.GetStream().ReadAsync(request.AsMemory()).AsTask().WaitAsync(HangGuard.Bound);
            await Assert.That(read).IsGreaterThan(0);
            if (cancelCaller)
            {
                await caller.CancelOnWorkerAsync();
            }

            pipeline.Dispose();
            var failure = await execution.WaitAsync(HangGuard.Bound);
            await PipelineStreamTests.AssertInterruptionAsync(failure, cancelCaller, caller.Token);

            if (!ownsHttpClient)
            {
                await using var server = LoopbackHttpServer.Start(static _ => new LoopbackHttpResponse { StatusCode = HttpStatusCode.OK });
                using var response = await httpClient.GetAsync(server.Endpoint).WaitAsync(HangGuard.Bound);
                await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
            }
        }
        finally
        {
            await caller.CancelOnWorkerAsync();
            _ = await execution.WaitAsync(HangGuard.Bound);
        }
    }

    [Test]
    public async Task Dispose_Should_Dispose_The_Owned_Client()
    {
        using var handler = new RecordingHttpHandler();
        using var httpClient = new HttpClient(handler);
        var pipeline = CreatePipeline(httpClient, ownsHttpClient: true);

        pipeline.Dispose();

        await Assert.That(handler.IsDisposed).IsTrue();
    }

    [Test]
    public async Task Dispose_Should_Never_Dispose_An_Injected_Client()
    {
        using var handler = new RecordingHttpHandler();
        using var httpClient = new HttpClient(handler);
        var pipeline = CreatePipeline(httpClient);

        pipeline.Dispose();

        await Assert.That(handler.IsDisposed).IsFalse();
    }

    [Test]
    public async Task ExecuteAsync_Should_Refuse_After_Dispose()
    {
        using var handler = new RecordingHttpHandler();
        using var httpClient = new HttpClient(handler);
        var pipeline = CreatePipeline(httpClient);
        pipeline.Dispose();

        _ = await Assert
            .That(async () => _ = await pipeline.ExecuteAsync(OperationId,
                HttpMethod.Get, "/api/info", new RecordingResponseAdapter(), options: null, CancellationToken.None))
            .Throws<ObjectDisposedException>();
    }

    [Test]
    public async Task ExecuteAsync_Should_Refuse_A_Route_Without_A_Leading_Slash()
    {
        using var handler = new RecordingHttpHandler();
        using var httpClient = new HttpClient(handler);
        using var pipeline = CreatePipeline(httpClient);

        _ = await Assert
            .That(async () => _ = await pipeline.ExecuteAsync(OperationId,
                HttpMethod.Get, "api/health", new RecordingResponseAdapter(), options: null, CancellationToken.None))
            .Throws<ArgumentException>();
    }

    [Test]
    public async Task Create_Should_Refuse_An_Injected_Client_Without_An_Endpoint()
    {
        using var handler = new RecordingHttpHandler();
        using var httpClient = new HttpClient(handler);

        var exception = Assert.Throws<ArgumentException>(() => _ = Pipeline.Create(
            httpClient,
            new OpenCodeClientOptions()));

        await Assert.That(exception.Message).Contains("Endpoint");
    }

    [Test]
    public async Task Create_Should_Refuse_A_Missing_Endpoint_On_The_Owned_Path()
    {
        var exception = Assert.Throws<ArgumentException>(() => _ = Pipeline.Create(new OpenCodeClientOptions()));

        await Assert.That(exception.Message).Contains("Endpoint");
    }

    [Test]
    public async Task Create_Should_Build_An_Owned_Pipeline_From_The_Options()
    {
        using var pipeline = Pipeline.Create(new OpenCodeClientOptions { Endpoint = Endpoint });

        await Assert.That(pipeline).IsNotNull();
    }

    [Test]
    public async Task UserAgent_Should_Match_The_Assembly_Informational_Version()
    {
        var informational = typeof(Pipeline).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

        var product = UserAgentPolicy.Resolve().ToString();

        await Assert.That(informational).IsNotNull();
        await Assert.That(product).IsEqualTo($"OpenCode.Sdk/{informational!.Split('+')[0]}");
    }

    private static GatedStream CreateGatedBody(out StreamContent content, out ManualTimeProvider time, out PipelineScenario scenario)
    {
        var payload = Encoding.UTF8.GetBytes("{\"healthy\":true}");
        time = new ManualTimeProvider();
        StreamContent? responseContent = null;
        scenario = PipelineScenario.Responding(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = responseContent,
        }, networkTimeout: Window, time: time);
        var body = new GatedStream(payload, chunkCount: 4);
        content = responseContent = new StreamContent(body);
        return body;
    }

    private static Pipeline CreatePipeline(
        HttpClient httpClient,
        bool ownsHttpClient = false,
        Uri? endpoint = null,
        string? password = null,
        string? username = null,
        LocationSelector? location = null,
        TimeSpan? networkTimeout = null,
        TimeProvider? time = null) =>
        PipelineFactory.Create(httpClient, ownsHttpClient, endpoint, password, username, location, networkTimeout: networkTimeout, time: time);
}
