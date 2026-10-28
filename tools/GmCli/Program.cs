using System.Text;
using GMessages.Protocol.PbLite;
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

    // PBLite Through Descriptor Access (the AOT risk): strings copied from golden pblite.json
    var auth = new AuthMessage
    {
        RequestID        = "req-1",
        Network          = "GDitto",
        TachyonAuthToken = ByteString.CopyFrom(1, 2, 3, 0xfe, 0xff),
        ConfigVersion    = version,
    };
    const string auth_pblite = """["req-1",null,"GDitto",null,null,"AQID/v8=",[null,null,2026,9,2,null,4,null,6]]""";

    var sign_in = new SignInGaiaRequest
    {
        AuthMessage = new AuthMessage { RequestID = "r2", Network = "Bugle", ConfigVersion = version },
        Inner       = new SignInGaiaRequest.Types.Inner
        {
            DeviceID = new SignInGaiaRequest.Types.Inner.Types.DeviceID { UnknownInt1 = 3, DeviceID_ = "messages-web-00112233445566778899aabbccddeeff" },
            SomeData = new SignInGaiaRequest.Types.Inner.Types.Data { SomeData = ByteString.CopyFrom(Enumerable.Range(0x10, 33).Select(i => (byte)i).ToArray()) },
        },
        Network     = "Bugle",
    };
    const string sign_in_pblite = """[["r2",null,"Bugle",null,null,null,[null,null,2026,9,2,null,4,null,6]],[[3,"messages-web-00112233445566778899aabbccddeeff"],null,null,null,null,null,null,null,null,null,null,null,null,null,null,null,null,null,null,null,null,null,null,null,null,null,null,null,null,null,null,null,null,null,null,"GiEQERITFBUWFxgZGhscHR4fICEiIyQlJicoKSorLC0uLzA="],null,"Bugle"]""";

    if (Encoding.UTF8.GetString(PbLiteSerializer.Serialize(auth)) != auth_pblite
        || Encoding.UTF8.GetString(PbLiteSerializer.Serialize(sign_in)) != sign_in_pblite)
    {
        Console.Error.WriteLine("selftest failed: pblite serialize");
        return 1;
    }

    if (!PbLiteSerializer.Deserialize<AuthMessage>(Encoding.UTF8.GetBytes(auth_pblite)).Equals(auth)
        || !PbLiteSerializer.Deserialize<SignInGaiaRequest>(Encoding.UTF8.GetBytes(sign_in_pblite)).Equals(sign_in))
    {
        Console.Error.WriteLine("selftest failed: pblite deserialize");
        return 1;
    }

    // PBLite With An Enum, Bools, Repeated Message And Repeated String (golden Conversation case)
    var conversation = new Conversation { ConversationID = "c1", Unread = true, Status = ConversationStatus.Archived, Pinned = true };
    conversation.OtherParticipants.Add(["p1", "p2"]);
    conversation.Participants.Add(new Participant { FullName = "A", IsMe = true });
    const string conversation_pblite = """["c1",null,null,null,null,true,null,null,null,null,null,2,null,null,null,null,null,null,null,[[null,null,"A",null,null,true,null,null,null,null,null,null,null,null,null,null,null,null,null,null]],["p1","p2"],null,null,null,null,true,null,null,null,null,null,null,null,null,null]""";

    if (Encoding.UTF8.GetString(PbLiteSerializer.Serialize(conversation)) != conversation_pblite
        || !PbLiteSerializer.Deserialize<Conversation>(Encoding.UTF8.GetBytes(conversation_pblite)).Equals(conversation))
    {
        Console.Error.WriteLine("selftest failed: pblite enum/bool/repeated");
        return 1;
    }

    Console.WriteLine("selftest ok");
    return 0;
}
