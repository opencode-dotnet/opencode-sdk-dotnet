using System.Text;

namespace OpenCode.Sdk.Tests.Hosting.BackgroundService.Support;

internal static class ServiceConfigWriteData
{
    public const string Config = "{\"port\":4096}";
    public const string LegacyRepeated = "{\"remote\":true,\"hostname\":\"first\",\"hostname\":\"last\"}";
    public const string SurrogateHostname = "{\"hostname\":\"\\ud800\"}";
    public const string RepeatedEnvironment = "{\"env\":{\"A\":1,\"A\":\"last\"}}";
    public static string Deep(bool malformed) =>
        "{\"unknown\":" + new string('[', 70) + "0" + new string(']', 70) + (malformed ? ",}" : "}");
    public const string Registration = "{\"url\":\"http://localhost:4096\",\"pid\":123,\"version\":\"0.0.0-beta-1\"}";
    public static byte[] Donor(string json, bool bom) => bom
        ? [0xef, 0xbb, 0xbf, .. Encoding.UTF8.GetBytes(json)]
        : [.. Encoding.UTF8.GetBytes(json[..^1] + ",\"note\":\"a"), 0xff, .. "b\"}"u8.ToArray()];
    public static byte[] ConfigDecoded(bool bom) => bom ? "{\"port\":4096}"u8.ToArray()
        : [.. "{\"port\":4096,\"note\":\"a"u8.ToArray(), 0xef, 0xbf, 0xbd, .. "b\"}"u8.ToArray()];
    public static byte[] RegistrationDecoded(bool bom) => bom
        ? "{\"url\":\"http://localhost:4096\",\"pid\":123,\"version\":\"0.0.0-beta-1\"}"u8.ToArray()
        : [.. "{\"url\":\"http://localhost:4096\",\"pid\":123,\"version\":\"0.0.0-beta-1\",\"note\":\"a"u8.ToArray(),
            0xef, 0xbf, 0xbd, .. "b\"}"u8.ToArray()];
    public static byte[] InvalidHostname => [.. "{\"hostname\":\""u8.ToArray(), 0xff, .. "\"}"u8.ToArray()];
    public static byte[] InvalidRegistrationId => [.. Encoding.UTF8.GetBytes(Registration[..^1] + ",\"id\":\""), 0xff, .. "\"}"u8.ToArray()];
    public const string DecodedRegistrationId = "{\"url\":\"http://localhost:4096\",\"pid\":123,\"version\":\"0.0.0-beta-1\",\"id\":\"\uFFFD\"}";
    public static byte[] DoubleBomRegistration => [0xef, 0xbb, 0xbf, 0xef, 0xbb, 0xbf, .. Encoding.UTF8.GetBytes(Registration)];
    public const string DecodedHostname = "{\"hostname\":\"\uFFFD\"}";
    public const string DecodedEnvironmentValue = "{\"env\":{\"A\":\"\uFFFD\"}}";
    public static byte[] InvalidEnvironmentValue => [.. "{\"env\":{\"A\":\""u8.ToArray(), 0xff, .. "\"}}"u8.ToArray()];
    public static byte[] DoubleBomDonor => [0xef, 0xbb, 0xbf, 0xef, 0xbb, 0xbf, .. Encoding.UTF8.GetBytes(Config)];
    public static byte[] CollidingDonorNames => [.. "{\"a"u8.ToArray(), 0xff, .. "\":1,\"a"u8.ToArray(), 0xfe, .. "\":2}"u8.ToArray()];
    public static byte[] CollidingRegistrationNames =>
        [.. Encoding.UTF8.GetBytes(Registration[..^1] + ",\"a"), 0xff, .. "\":1,\"a"u8.ToArray(), 0xfe, .. "\":2}"u8.ToArray()];
}
