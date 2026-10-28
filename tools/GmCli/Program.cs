using GMessages.Protocol.Proto;
using Google.Protobuf;

// Commands
if (args.Length == 1 && args[0] == "selftest")
{
    return SelfTest();
}

Console.Error.WriteLine("usage: GmCli selftest");
return 2;

// Self Test
static int SelfTest()
{
    // Round-Trip A Config Version Through Binary
    var version = new ConfigVersion { Year = 2026, Month = 9, Day = 2, V1 = 4, V2 = 6 };
    var parsed  = ConfigVersion.Parser.ParseFrom(version.ToByteArray());

    if (parsed.Year != 2026 || parsed.Month != 9 || parsed.Day != 2 || parsed.V1 != 4 || parsed.V2 != 6)
    {
        Console.Error.WriteLine("selftest failed: config version round-trip");
        return 1;
    }

    Console.WriteLine("selftest ok");
    return 0;
}
