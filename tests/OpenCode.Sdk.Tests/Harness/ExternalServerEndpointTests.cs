namespace OpenCode.Sdk.Tests.Harness;

public sealed class ExternalServerEndpointTests
{
    private const string Endpoint = "http://localhost:4097";

    private const string Password = "secret";

    [Test]
    public async Task FromEnvironment_Should_Return_The_Pair_When_Both_Variables_Are_Set()
    {
        var endpoint = ExternalServerEndpoint.FromEnvironment(name => name switch
        {
            "OPENCODE_SDK_TESTS_ENDPOINT" => Endpoint,
            "OPENCODE_SDK_TESTS_PASSWORD" => Password,
            _ => null,
        });

        await Assert.That(endpoint).IsNotNull();
        await Assert.That(endpoint!.Endpoint).IsEqualTo(new Uri(Endpoint));
        await Assert.That(endpoint.Password).IsEqualTo(Password);
    }

    [Test]
    public async Task FromEnvironment_Should_Return_Null_When_Neither_Variable_Is_Set()
    {
        var endpoint = ExternalServerEndpoint.FromEnvironment(static _ => null);

        await Assert.That(endpoint).IsNull();
    }

    [Test]
    [Arguments("OPENCODE_SDK_TESTS_ENDPOINT")]
    [Arguments("OPENCODE_SDK_TESTS_PASSWORD")]
    public async Task FromEnvironment_Should_Throw_Naming_Both_Variables_When_Only_One_Is_Set(string onlySet)
    {
        string? Read(string name) => name == onlySet ? "value" : null;

        var exception = await Assert
            .That(() => ExternalServerEndpoint.FromEnvironment(Read))
            .Throws<InvalidOperationException>();

        await Assert.That(exception!.Message).Contains("OPENCODE_SDK_TESTS_ENDPOINT");
        await Assert.That(exception.Message).Contains("OPENCODE_SDK_TESTS_PASSWORD");
    }

    [Test]
    [Arguments("")]
    [Arguments("   ")]
    public async Task FromEnvironment_Should_Carry_A_Blank_Password_As_Written(string blankPassword)
    {
        // The client sends any non-null password as written, as the pinned client does.
        string? Read(string name) => name switch
        {
            "OPENCODE_SDK_TESTS_ENDPOINT" => Endpoint,
            "OPENCODE_SDK_TESTS_PASSWORD" => blankPassword,
            _ => null,
        };

        var endpoint = ExternalServerEndpoint.FromEnvironment(Read);

        await Assert.That(endpoint).IsNotNull();
        await Assert.That(endpoint!.Password).IsEqualTo(blankPassword);
    }

    [Test]
    public async Task FromEnvironment_Should_Throw_Naming_The_Variable_When_The_Endpoint_Does_Not_Parse()
    {
        static string? Read(string name) => name switch
        {
            "OPENCODE_SDK_TESTS_ENDPOINT" => "not-a-uri",
            "OPENCODE_SDK_TESTS_PASSWORD" => Password,
            _ => null,
        };

        var exception = await Assert
            .That(() => ExternalServerEndpoint.FromEnvironment(Read))
            .Throws<InvalidOperationException>();

        await Assert.That(exception!.Message).Contains("OPENCODE_SDK_TESTS_ENDPOINT");
    }

    [Test]
    public async Task ToString_Should_Name_The_Endpoint_And_Never_The_Password()
    {
        var endpoint = ExternalServerEndpoint.FromEnvironment(static name => name switch
        {
            "OPENCODE_SDK_TESTS_ENDPOINT" => Endpoint,
            "OPENCODE_SDK_TESTS_PASSWORD" => Password,
            _ => null,
        });

        var rendered = endpoint!.ToString();

        await Assert.That(rendered).Contains(Endpoint);
        await Assert.That(rendered).DoesNotContain(Password, StringComparison.Ordinal);
    }
}
