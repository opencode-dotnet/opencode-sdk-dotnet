using System.Text.Json;
using OpenCode.Sdk.Internal.BackgroundService.Registration;

namespace OpenCode.Sdk.Tests.BackgroundService.Registration;

/// <summary>
/// The CLI's schema integer (<c>Number.isSafeInteger</c> over the value <c>JSON.parse</c> produced) as
/// the strict readers apply it: the notation an integer is written in does not matter, a fraction or a
/// magnitude past 2^53 - 1 is not one, and a process id is that integer within the <see cref="int"/> range.
/// </summary>
public sealed class StrictJsonTests
{
    [Test]
    [Arguments("8080", 8080L)]
    [Arguments("8080.0", 8080L)]
    [Arguments("8.08e3", 8080L)]
    [Arguments("8.08E+3", 8080L)]
    [Arguments("0", 0L)]
    [Arguments("-0.0", 0L)]
    [Arguments("-1", -1L)]
    [Arguments("9007199254740991", 9_007_199_254_740_991L)]
    [Arguments("-9007199254740991", -9_007_199_254_740_991L)]
    public async Task TryGetSafeInteger_Should_Read_An_Integer_In_Any_Number_Notation(string json, long expected)
    {
        var read = StrictJson.TryGetSafeInteger(Parse(json), out var value);

        await Assert.That(read).IsTrue();
        await Assert.That(value).IsEqualTo(expected);
    }

    /// <summary>
    /// Both parsers round a JSON number to the nearest double before the test, so a fraction too small
    /// for a double near 2^53 vanishes the same way on each side: <c>JSON.parse</c> reads
    /// 9007199254740991.4 as 9007199254740991, which <c>Number.isSafeInteger</c> admits.
    /// </summary>
    [Test]
    public async Task TryGetSafeInteger_Should_Round_Like_Json_Parse_Before_Testing()
    {
        var read = StrictJson.TryGetSafeInteger(Parse("9007199254740991.4"), out var value);

        await Assert.That(read).IsTrue();
        await Assert.That(value).IsEqualTo(9_007_199_254_740_991L);
    }

    [Test]
    [Arguments("8080.5")]
    [Arguments("8080.0000001")]
    [Arguments("1.5")]
    [Arguments("9007199254740992")]
    [Arguments("9007199254740992.0")]
    [Arguments("-9007199254740992")]
    [Arguments("1e300")]
    [Arguments("1e400")]
    [Arguments("\"8080\"")]
    [Arguments("true")]
    [Arguments("null")]
    [Arguments("[8080]")]
    public async Task TryGetSafeInteger_Should_Refuse_A_Value_That_Is_Not_A_Safe_Integer(string json)
    {
        var read = StrictJson.TryGetSafeInteger(Parse(json), out var value);

        await Assert.That(read).IsFalse();
        await Assert.That(value).IsEqualTo(0L);
    }

    [Test]
    [Arguments("1234", 1234)]
    [Arguments("1234.0", 1234)]
    [Arguments("1.234e3", 1234)]
    [Arguments("2147483647", int.MaxValue)]
    [Arguments("-2147483648", int.MinValue)]
    public async Task TryGetProcessId_Should_Read_A_Safe_Integer_Within_The_Int32_Range(string json, int expected)
    {
        var read = StrictJson.TryGetProcessId(Parse(json), out var processId);

        await Assert.That(read).IsTrue();
        await Assert.That(processId).IsEqualTo(expected);
    }

    [Test]
    [Arguments("1.5")]
    [Arguments("2147483648")]
    [Arguments("-2147483649")]
    [Arguments("9007199254740992")]
    [Arguments("\"1234\"")]
    public async Task TryGetProcessId_Should_Refuse_A_Value_That_Is_Not_A_Process_Id(string json)
    {
        var read = StrictJson.TryGetProcessId(Parse(json), out var processId);

        await Assert.That(read).IsFalse();
        await Assert.That(processId).IsEqualTo(0);
    }

    private static JsonElement Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}
