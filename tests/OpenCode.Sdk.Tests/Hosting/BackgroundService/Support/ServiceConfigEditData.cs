using System.Globalization;

namespace OpenCode.Sdk.Tests.Hosting.BackgroundService.Support;

internal static class ServiceConfigEditData
{
    public static IReadOnlyList<ServiceConfigEditCase> Cases { get; } =
    [
        new(["set", "disabled", "true"], 1), new(["set", "disabled", "false"], 0),
        new(["set", "hostname", "changed"], 1), new(["set", "port", "4097"], 1),
        new(["set", "password", "changed"], 1), new(["set", "cors", "https://a.example"], 1),
        new(["set", "env", "A", "changed"], 1), new(["set", "remote", "false"], 1),
        new(["unset", "disabled"], 0), new(["unset", "hostname"], 1),
        new(["unset", "port"], 1), new(["unset", "password"], 1),
        new(["unset", "cors"], 1), new(["unset", "env", "A"], 1),
    ];

    public static Task InvokeAsync(ServiceConfigScenario scenario, IReadOnlyList<string> command, CancellationToken token)
    {
        var options = scenario.Options;
        var editor = scenario.Editor;
        if (command[0] == "set")
        {
            return command[1] switch
            {
                "disabled" => OpenCodeServiceConfiguration.SetDisabledWithSeamsAsync(bool.Parse(command[2]), options, editor, token),
                "hostname" => OpenCodeServiceConfiguration.SetHostnameWithSeamsAsync(command[2], options, editor, token),
                "port" => OpenCodeServiceConfiguration.SetPortWithSeamsAsync(int.Parse(command[2], CultureInfo.InvariantCulture), options, editor, token),
                "password" => OpenCodeServiceConfiguration.SetPasswordWithSeamsAsync(command[2], options, editor, token),
                "cors" => OpenCodeServiceConfiguration.SetCorsOriginsWithSeamsAsync(command[2].Split(','), options, editor, token),
                "env" => OpenCodeServiceConfiguration.SetEnvironmentVariableWithSeamsAsync(command[2], command[3], options, editor, token),
                "remote" => OpenCodeServiceConfiguration.DisableRemoteWithSeamsAsync(options, editor, token),
                _ => throw new InvalidOperationException("Unknown config set command."),
            };
        }

        return command[1] switch
        {
            "env" => OpenCodeServiceConfiguration.UnsetEnvironmentVariableWithSeamsAsync(command[2], options, editor, token),
            "remote" => OpenCodeServiceConfiguration.DisableRemoteWithSeamsAsync(options, editor, token),
            _ => OpenCodeServiceConfiguration.UnsetWithSeamsAsync(command[1] switch
            {
                "disabled" => OpenCodeServiceSetting.Disabled,
                "hostname" => OpenCodeServiceSetting.Hostname,
                "port" => OpenCodeServiceSetting.Port,
                "password" => OpenCodeServiceSetting.Password,
                "cors" => OpenCodeServiceSetting.CorsOrigins,
                _ => throw new InvalidOperationException("Unknown config unset command."),
            }, options, editor, token),
        };
    }
}
