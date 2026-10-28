using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GMessages.Protocol;
using GMessages.Protocol.Auth;
using GMessages.Protocol.Events;
using GMessages.Protocol.Http;
using GMessages.Protocol.Pairing;
using GMessages.Protocol.PbLite;
using GMessages.Protocol.Proto;
using GMessages.Protocol.Session;
using Google.Protobuf;
using Microsoft.Extensions.Logging;

// Commands
Console.OutputEncoding = Encoding.UTF8;

bool verbose   = args.Contains("--verbose");
string[] words = args.Where(a => a != "--verbose").ToArray();

if (words.Length == 1)
{
    switch (words[0])
    {
        case "selftest": return SelfTest();
        case "pair":     return await PairAsync(verbose);
        case "listen":   return await ListenAsync(verbose);
        case "unpair":   return await UnpairAsync(verbose);
    }
}

Console.Error.WriteLine("usage: GmCli selftest | pair | listen | unpair [--verbose]");
return 2;

// Paths
static string DevDir() => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GMessages.Dev");

// Logger Factory (simple console formatter is AOT-safe)
static ILoggerFactory CreateLogs(bool verbose) => LoggerFactory.Create(b => b
    .SetMinimumLevel(verbose ? LogLevel.Debug : LogLevel.Information)
    .AddSimpleConsole(o => o.SingleLine = true));

// Loads The Saved Session, Or Null
static AuthData? LoadSession() => new SessionStore(Path.Combine(DevDir(), "session.bin")).Load();

/// <summary>
/// Pairs with the phone using the cookies the sign-in spike saved, showing the emoji to tap.
/// </summary>
/// <param name="verbose">Debug-level logging.</param>
/// <returns>The exit code.</returns>
static async Task<int> PairAsync(bool verbose)
{
    // Guard: Spike Cookies Must Exist
    string cookie_file = Path.Combine(DevDir(), "spike-cookies.bin");
    if (!File.Exists(cookie_file))
    {
        Console.Error.WriteLine("No spike cookies. Run SignInSpike first.");
        return 1;
    }

    // Load Cookies (DPAPI JSON {name: value}; values are never printed)
    var cookies = new Dictionary<string, string>();
    try
    {
        using var doc = JsonDocument.Parse(ProtectedData.Unprotect(File.ReadAllBytes(cookie_file), null, DataProtectionScope.CurrentUser));
        foreach (var prop in doc.RootElement.EnumerateObject())
        {
            cookies[prop.Name] = prop.Value.GetString() ?? "";
        }
    }
    catch (Exception ex) when (ex is CryptographicException or JsonException or InvalidOperationException)
    {
        Console.Error.WriteLine("Spike cookies unreadable. Re-run SignInSpike.");
        return 1;
    }

    var auth = AuthData.CreateNew();
    auth.Cookies.Set(cookies);

    using var logs = CreateLogs(verbose);
    using var stop = new CancellationTokenSource(TimeSpan.FromMinutes(10));
    ConsoleCancelEventHandler on_cancel = (_, e) => { e.Cancel = !stop.IsCancellationRequested; stop.Cancel(); };
    Console.CancelKeyPress += on_cancel;

    // Keep The Handler Until The Client Is Disposed, So Ctrl+C Cannot Kill A Session Save
    try
    {
        await using var client = GmClient.Create(auth, new SessionStore(Path.Combine(DevDir(), "session.bin")), GmEndpoints.Production, TimeProvider.System, logs);
        try
        {
            await client.PairAsync(emoji => Console.WriteLine($"\n    Tap this on your phone:  {emoji}\n"), stop.Token);
        }
        catch (PairingException ex)
        {
            Console.Error.WriteLine($"pairing failed: {ex.Failure}: {ex.Message}");
            return 1;
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("pairing failed: canceled or timed out");
            return 1;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"pairing failed: {ex.GetType().Name}: {ex.Message}");
            if (ex is GmRequestException { StatusCode: 401 or 403 })
            {
                Console.Error.WriteLine("Google rejected the sign-in cookies (likely stale). Re-run SignInSpike, then pair right away.");
            }

            return 1;
        }

        Console.WriteLine("paired");
        return 0;
    }
    finally
    {
        Console.CancelKeyPress -= on_cancel;
    }
}

/// <summary>
/// Connects from the saved session and prints one line per message until Ctrl+C.
/// </summary>
/// <param name="verbose">Debug-level logging.</param>
/// <returns>The exit code.</returns>
static async Task<int> ListenAsync(bool verbose)
{
    var auth = LoadSession();
    if (auth is null)
    {
        Console.Error.WriteLine("No session. Run 'GmCli pair' first.");
        return 1;
    }

    using var logs = CreateLogs(verbose);
    using var stop = new CancellationTokenSource();
    ConsoleCancelEventHandler on_cancel = (_, e) => { e.Cancel = !stop.IsCancellationRequested; stop.Cancel(); };
    Console.CancelKeyPress += on_cancel;

    // Keep The Handler Until The Client Is Disposed, So Ctrl+C Cannot Kill A Session Save
    try
    {
        // Fatal Listen Error Stops The Run With A Non-Zero Exit
        bool fatal = false;
        await using var client = GmClient.Create(auth, new SessionStore(Path.Combine(DevDir(), "session.bin")), GmEndpoints.Production, TimeProvider.System, logs);
        client.EventRaised += evt =>
        {
            PrintEvent(evt);
            if (evt is ListenFatalError)
            {
                Volatile.Write(ref fatal, true);
                stop.Cancel();
            }
        };

        try
        {
            await client.ConnectAsync(stop.Token);
            Console.WriteLine("listening (Ctrl+C to stop)");
            await Task.Delay(Timeout.Infinite, stop.Token);
        }
        catch (SessionExpiredException)
        {
            Console.Error.WriteLine("Session expired. Run 'GmCli pair' again.");
            return 1;
        }
        catch (OperationCanceledException)
        {
            // Ctrl+C Or Fatal Error: Fall Through To Disconnect
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"listen failed: {ex.GetType().Name}: {ex.Message}");
            return 1;
        }

        await client.DisconnectAsync();
        Console.WriteLine("stopped");
        return Volatile.Read(ref fatal) ? 1 : 0;
    }
    finally
    {
        Console.CancelKeyPress -= on_cancel;
    }
}

/// <summary>
/// Tells the phone to forget this PC and deletes the saved session.
/// </summary>
/// <param name="verbose">Debug-level logging.</param>
/// <returns>The exit code.</returns>
static async Task<int> UnpairAsync(bool verbose)
{
    var auth = LoadSession();
    if (auth is null)
    {
        Console.Error.WriteLine("No session. Run 'GmCli pair' first.");
        return 1;
    }

    using var logs = CreateLogs(verbose);
    using var stop = new CancellationTokenSource();
    ConsoleCancelEventHandler on_cancel = (_, e) => { e.Cancel = !stop.IsCancellationRequested; stop.Cancel(); };
    Console.CancelKeyPress += on_cancel;

    // Keep The Handler Until The Client Is Disposed, So Ctrl+C Cannot Kill A Session Save
    try
    {
        await using var client = GmClient.Create(auth, new SessionStore(Path.Combine(DevDir(), "session.bin")), GmEndpoints.Production, TimeProvider.System, logs);
        try
        {
            await client.UnpairAsync(stop.Token);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"unpair failed ({(ex is OperationCanceledException ? "canceled" : "request error")}; the local session is removed either way): {ex.GetType().Name}: {ex.Message}");
            return 1;
        }

        Console.WriteLine("unpaired");
        return 0;
    }
    finally
    {
        Console.CancelKeyPress -= on_cancel;
    }
}

/// <summary>
/// Prints a message as one line, and connection-health events as status lines.
/// </summary>
/// <param name="evt">The event.</param>
static void PrintEvent(object evt)
{
    switch (evt)
    {
        case MessageReceived m:
            string text = string.Join(" ", m.Message.MessageInfo.Select(i => i.MessageContent?.Content).Where(c => !string.IsNullOrEmpty(c)));
            Console.WriteLine($"[{(m.IsOld ? "old" : "live")}] {m.Message.ConversationID} {m.Message.ParticipantID} {text}");
            break;
        case ListenFatalError e:     Console.WriteLine($"status: listen fatal error: {e.Error.Message}"); break;
        case ListenTemporaryError e: Console.WriteLine($"status: listen temporary error: {e.Error.Message}"); break;
        case ListenRecovered:        Console.WriteLine("status: listen recovered"); break;
        case PhoneNotResponding:     Console.WriteLine("status: phone not responding"); break;
        case PhoneRespondingAgain:   Console.WriteLine("status: phone responding again"); break;
        case GaiaLoggedOut:          Console.WriteLine("status: Google account logged out"); break;
    }
}

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
