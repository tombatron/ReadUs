using System;

namespace ReadUs.Extras;

public static class EnvironmentTools
{
    public static void StartTesting() =>
        Environment.SetEnvironmentVariable("__UNIT_TESTING__", "1");

    public static void StopTesting() =>
        Environment.SetEnvironmentVariable("__UNIT_TESTING__", "0");

    public static bool IsTesting() =>
        Environment.GetEnvironmentVariable("__UNIT_TESTING__") == "1";
}