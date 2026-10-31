using System.Runtime.InteropServices.WindowsRuntime;
using GMessages.Presentation.Messaging;
using Windows.Devices.Enumeration;
using Windows.Media.Devices;
using Windows.Media;
using Windows.Media.Audio;
using Windows.Media.Capture;
using Windows.Media.MediaProperties;
using Windows.Media.Render;
using Windows.Media.Transcoding;
using Windows.Security.Authorization.AppCapabilityAccess;
using Windows.Storage;
using Windows.Storage.Streams;

namespace GMessages.App.Media;

/// <summary>
/// Thrown when the microphone can't record: access is off (Settings > Privacy &amp; security > Microphone) or there is no
/// microphone.
/// </summary>
internal sealed class MicrophoneUnavailableException : Exception
{
    /// <summary>
    /// Creates the exception.
    /// </summary>
    /// <param name="reason">Why, for the log: <c>AccessDenied</c>, <c>NoDevice</c>, the error type or the graph's status.</param>
    public MicrophoneUnavailableException(string reason)
        : base(reason)
    {
    }

    /// <summary>
    /// Creates the exception without a reason.
    /// </summary>
    public MicrophoneUnavailableException()
    {
    }

    /// <summary>
    /// Creates the exception with the error that caused it.
    /// </summary>
    /// <param name="reason">Why, for the log.</param>
    /// <param name="inner">The error that caused it.</param>
    public MicrophoneUnavailableException(string reason, Exception inner)
        : base(reason, inner)
    {
    }
}

/// <summary>
/// Records a voice message from the microphone chosen in Settings (or Windows' default) straight to an M4A/AAC temp
/// file, with live input levels.
/// </summary>
/// <remarks>
/// <para>
/// One <see cref="AudioGraph"/>: the device input node feeds a file output node (M4A, AAC) and a frame output node,
/// whose frames give the level the live waveform shows (<see cref="TakeLevel"/>). Reading a frame copies its samples
/// with <c>Buffer.CreateCopyFromMemoryBuffer</c>, so no COM interface (<c>IMemoryBufferByteAccess</c>) or unsafe code
/// is needed under Native AOT; the copy's <c>Length</c> must be set to the frame's, since it comes back 0.
/// </para>
/// <para>
/// Before the graph, <see cref="AppCapability"/> checks that microphone access is on. If the user hasn't decided yet,
/// <see cref="MicrophoneConsent"/> asks through <c>ActivateAudioInterfaceAsync</c>, which shows Windows' consent prompt
/// to desktop apps (<c>AppCapability.RequestAccessAsync</c> needs a CoreWindow, which a desktop app doesn't have).
/// Access that ends up off throws, and the chat pane's own dialog points to Windows' settings.
/// </para>
/// <para>
/// <see cref="StopAsync"/> finishes the file; disposing without it drops the graph (the caller deletes the file). Access
/// off or no microphone throws <see cref="MicrophoneUnavailableException"/>. A test run never touches the real
/// microphone: the test hook either refuses or copies a fixture file (see <see cref="TestHooks.FakeMicrophone"/>).
/// The audio and the path are never logged.
/// </para>
/// </remarks>
internal sealed class VoiceRecorder : IDisposable
{
    // AAC At Windows' Medium Quality: Clear Speech, Small Files
    private const AudioEncodingQuality Quality = AudioEncodingQuality.Medium;

    private readonly AudioGraph? _graph;
    private readonly AudioFileOutputNode? _file;
    private readonly AudioFrameOutputNode? _frames;

    // The File, For Its Size In The Log (Never Its Path)
    private readonly string _path;

    // A Test Run's Fake Level Follows The Clock; Its "unfinished" Microphone Fails To Finish The File
    private readonly long _fakeStart = Environment.TickCount64;
    private readonly bool _fakeUnfinished;

    private bool _disposed;

    private VoiceRecorder(string path, AudioGraph? graph, AudioFileOutputNode? file, AudioFrameOutputNode? frames, bool fakeUnfinished = false)
    {
        _path           = path;
        _graph          = graph;
        _file           = file;
        _frames         = frames;
        _fakeUnfinished = fakeUnfinished;
    }

    /// <summary>
    /// Starts recording to a new file.
    /// </summary>
    /// <param name="path">The temp file to record to (its folder is made if missing).</param>
    /// <param name="microphoneId">
    /// The Windows device ID of the microphone chosen in Settings, or null for Windows' default. A chosen microphone that
    /// isn't plugged in records from the default instead.
    /// </param>
    /// <returns>The running recorder.</returns>
    /// <exception cref="MicrophoneUnavailableException">Microphone access is off, or there is no microphone.</exception>
    public static async Task<VoiceRecorder> StartAsync(string path, string? microphoneId)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        // Test Run: Refuse, Or Record The Fixture
        if (TestHooks.FakeMicrophone() is { } fake)
        {
            if (fake is "denied" or "none")
            {
                throw new MicrophoneUnavailableException(fake == "denied" ? "AccessDenied" : "NoDevice");
            }

            // A File That Never Finishes, For The Failure Note
            if (fake == "unfinished")
            {
                await File.WriteAllBytesAsync(path, []);
                return new VoiceRecorder(path, null, null, null, fakeUnfinished: true);
            }

            File.Copy(fake, path, overwrite: true);
            return new VoiceRecorder(path, null, null, null);
        }

        // Access Must Be On First: Ask Windows (Its Prompt) If Undecided, Since An AudioGraph Alone Is Only Refused
        await EnsureMicrophoneAsync();

        AudioGraph? graph = null;
        try
        {
            // The Graph; Its Clock Needs An Output Device, Which Any PC With A Microphone Has
            CreateAudioGraphResult created = await AudioGraph.CreateAsync(new AudioGraphSettings(AudioRenderCategory.Speech));
            if (created.Status != AudioGraphCreationStatus.Success)
            {
                throw new MicrophoneUnavailableException(created.Status.ToString());
            }

            graph = created.Graph;

            // The Microphone
            CreateAudioDeviceInputNodeResult input = await CreateInputAsync(graph, microphoneId);
            if (input.Status != AudioDeviceNodeCreationStatus.Success)
            {
                MicrophoneConsent.Log("device input", input.Status.ToString());
                throw new MicrophoneUnavailableException(input.Status.ToString());
            }

            // TODO: Diagnostic (Owner: The Chosen Microphone Isn't Used). Cause Found In 0.5.9: A Pick On A Reopened Settings
            // Page Was Never Saved, So The Old Microphone Recorded. Trim Once The Owner Confirms The Fix
            LogOpenedDevice(input.DeviceInputNode.Device?.Id, microphoneId);

            // The File (M4A, AAC) And The Levels
            await File.WriteAllBytesAsync(path, []);
            StorageFile storage                      = await StorageFile.GetFileFromPathAsync(path);
            CreateAudioFileOutputNodeResult out_file = await graph.CreateFileOutputNodeAsync(storage, MediaEncodingProfile.CreateM4a(Quality));
            if (out_file.Status != AudioFileNodeCreationStatus.Success)
            {
                throw new IOException(out_file.Status.ToString());
            }

            AudioFrameOutputNode frames = graph.CreateFrameOutputNode();
            input.DeviceInputNode.AddOutgoingConnection(out_file.FileOutputNode);
            input.DeviceInputNode.AddOutgoingConnection(frames);
            graph.Start();

            var recorder = new VoiceRecorder(path, graph, out_file.FileOutputNode, frames);
            graph = null;
            return recorder;
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new MicrophoneUnavailableException("AccessDenied", ex);
        }
        finally
        {
            graph?.Dispose();
        }
    }

    /// <summary>
    /// Makes the graph's input node from the microphone chosen in Settings, or from Windows' default microphone when
    /// none is chosen or the chosen one isn't plugged in.
    /// </summary>
    /// <remarks>
    /// The chosen device comes from <see cref="DeviceInformation.CreateFromIdAsync(string)"/>; a device that's gone either
    /// throws there or reads <see cref="DeviceInformation.IsEnabled"/> false. Only "chosen" or "default" is logged, never
    /// the device's name or ID.
    /// </remarks>
    /// <param name="graph">The graph to add the node to.</param>
    /// <param name="microphoneId">The chosen microphone's device ID, or null for the default.</param>
    /// <returns>The node creation result.</returns>
    private static async Task<CreateAudioDeviceInputNodeResult> CreateInputAsync(AudioGraph graph, string? microphoneId)
    {
        DeviceInformation? device = null;
        if (microphoneId is not null)
        {
            try
            {
                device = await DeviceInformation.CreateFromIdAsync(microphoneId);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // Unplugged Or Removed: Record From The Default
                device = null;
            }
        }

        // Windows' Default Microphone: The Other Category Follows Settings' "Default" Device; Speech Would Follow The
        // Communications Default Instead (On The Owner's PC The Yeti, While The Default Is The Surface)
        if (device is not { IsEnabled: true })
        {
            MicrophoneConsent.Log("device", "default");
            return await graph.CreateDeviceInputNodeAsync(MediaCategory.Other);
        }

        // The Chosen Microphone
        MicrophoneConsent.Log("device", "chosen");
        return await graph.CreateDeviceInputNodeAsync(MediaCategory.Other, null, device);
    }

    // Which Device The Graph Really Opened, Against The Chosen One And Windows' Defaults; Names And IDs Never Logged
    private static void LogOpenedDevice(string? openedId, string? chosenId)
    {
        string default_id       = MediaDevice.GetDefaultAudioCaptureId(AudioDeviceRole.Default);
        string communication_id = MediaDevice.GetDefaultAudioCaptureId(AudioDeviceRole.Communications);
        string opened           = openedId is null ? "unknown"
            : string.Equals(openedId, chosenId, StringComparison.OrdinalIgnoreCase) ? "chosen"
            : string.Equals(openedId, default_id, StringComparison.OrdinalIgnoreCase) ? "default"
            : string.Equals(openedId, communication_id, StringComparison.OrdinalIgnoreCase) ? "communications default"
            : "other";

        MicrophoneConsent.Log("opened", $"{opened}; chosen is default: {string.Equals(chosenId, default_id, StringComparison.OrdinalIgnoreCase)}");
    }

    /// <summary>
    /// Makes sure Windows lets Messages use the microphone, asking with Windows' own prompt if the user hasn't decided.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Runs on the UI thread (no <c>ConfigureAwait(false)</c> on the way from the chat pane), since
    /// <c>ActivateAudioInterfaceAsync</c> must be called there for Windows to show its prompt. Microsoft documents that
    /// call for desktop apps; <see cref="AppCapability.RequestAccessAsync"/> isn't supported in them (it needs a
    /// CoreWindow) and failed with <c>RPC_E_DISCONNECTED</c> (0x80010108) in the owner's 0.5.2 log, as
    /// <c>MediaCapture</c> did in 0.5.1. An <see cref="AudioGraph"/> alone is only refused.
    /// </para>
    /// <para>
    /// Allowed records. Anything else asks through <see cref="MicrophoneConsent"/> (Windows prompts when the user can
    /// still decide, and answers at once when they can't), then checks again, and <see cref="MicrophoneAccess.CanRecord"/>
    /// decides. The first check alone isn't trusted: it read "DeniedByUser" in the owner's 0.5.5 log while Windows'
    /// consent store said "Prompt". Anything that can't record throws <see cref="MicrophoneUnavailableException"/>, and the
    /// chat pane shows its own dialog that opens Windows' microphone settings.
    /// </para>
    /// </remarks>
    /// <returns>A task that completes once access is on.</returns>
    /// <exception cref="MicrophoneUnavailableException">Access isn't on, there's no microphone, or the check failed.</exception>
    private static async Task EnsureMicrophoneAsync()
    {
        AppCapability capability;
        AppCapabilityAccessStatus before;
        try
        {
            capability = AppCapability.Create("microphone");
            before     = capability.CheckAccess();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            throw new MicrophoneUnavailableException($"{ex.GetType().Name} 0x{ex.HResult:X8}", ex);
        }

        MicrophoneConsent.Log("check before", before.ToString());

        // Allowed Already: Record
        if (!MicrophoneAccess.ShouldAsk(AccessOf(before)))
        {
            return;
        }

        // Not Allowed Yet: Windows Shows Its Prompt If The User Can Still Decide, Then Check Again
        int activated = await MicrophoneConsent.RequestAsync();
        AppCapabilityAccessStatus after;
        try
        {
            after = capability.CheckAccess();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            throw new MicrophoneUnavailableException($"{ex.GetType().Name} 0x{ex.HResult:X8}", ex);
        }

        MicrophoneConsent.Log("check after", after.ToString());
        if (!MicrophoneAccess.CanRecord(AccessOf(before), activated, AccessOf(after)))
        {
            throw new MicrophoneUnavailableException("AccessDenied");
        }
    }

    // Windows' Access Status, As The Decision Reads It
    private static MicrophoneAccessStatus AccessOf(AppCapabilityAccessStatus status) => status switch
    {
        AppCapabilityAccessStatus.Allowed            => MicrophoneAccessStatus.Allowed,
        AppCapabilityAccessStatus.UserPromptRequired => MicrophoneAccessStatus.Undecided,
        _                                            => MicrophoneAccessStatus.Refused,
    };

    /// <summary>
    /// Gives the loudest input since the last call, for the live waveform.
    /// </summary>
    /// <returns>The level, from 0 to 1.</returns>
    public float TakeLevel()
    {
        // Test Run: A Steady Wave
        if (_frames is null)
        {
            return (float)(0.5 + (0.4 * Math.Sin((Environment.TickCount64 - _fakeStart) / 150.0)));
        }

        // The Graph's Samples Are 32-Bit Floats
        using AudioFrame frame   = _frames.GetFrame();
        using AudioBuffer buffer = frame.LockBuffer(AudioBufferAccessMode.Read);
        if (buffer.Length == 0)
        {
            return 0;
        }

        // The Copy Comes Back With Length 0 (Only Its Capacity Set): Its Bytes Count Once Its Length Is The Frame's
        var copy    = Windows.Storage.Streams.Buffer.CreateCopyFromMemoryBuffer(buffer);
        copy.Length = Math.Min(copy.Capacity, buffer.Length);
        return VoiceWave.LevelOf(copy.ToArray());
    }

    /// <summary>
    /// Pauses recording: the graph stops, so no audio reaches the file or the levels until <see cref="Resume"/>.
    /// </summary>
    /// <remarks>
    /// A stopped graph processes no quanta, and the file output node isn't finalized, so resuming adds to the same file
    /// and the paused time is never in it.
    /// </remarks>
    public void Pause() => _graph?.Stop();

    /// <summary>
    /// Resumes a paused recording into the same file.
    /// </summary>
    public void Resume() => _graph?.Start();

    /// <summary>
    /// Stops recording, finishes the file and lets go of it, so it can be read back at once.
    /// </summary>
    /// <remarks>
    /// The owner's 0.5.10 log shows Done failing with an <see cref="IOException"/> whose cause wasn't logged. A finalize
    /// that fails (<c>Unknown</c>) can leave the file open while the graph lives (a sharing violation on read, seen in a
    /// scratch graph), so the graph is disposed here before the file is read, and the finalize result and the file's size
    /// in bytes are logged (never its path). A finalized file output node is already closed: closing it again throws
    /// <see cref="ObjectDisposedException"/>, so only the graph is disposed.
    /// </remarks>
    /// <returns>A task that completes when the file is complete and closed.</returns>
    /// <exception cref="IOException">The file couldn't be finished.</exception>
    public async Task StopAsync()
    {
        // Test Run: The Fixture Is Already Complete, Or Never Finishes
        if (_graph is null || _file is null)
        {
            if (_fakeUnfinished)
            {
                MicrophoneConsent.Log("finalize", "Unknown (test)");
                throw new IOException("Unknown");
            }

            return;
        }

        _graph.Stop();
        TranscodeFailureReason result = await _file.FinalizeAsync();
        Dispose();

        long bytes = File.Exists(_path) ? new FileInfo(_path).Length : -1;
        MicrophoneConsent.Log("finalize", $"{result}; {bytes} bytes");
        if (result != TranscodeFailureReason.None)
        {
            throw new IOException(result.ToString());
        }
    }

    /// <summary>
    /// Stops the graph and lets go of the microphone and the file (unfinished unless <see cref="StopAsync"/> ran).
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _graph?.Stop();
        _graph?.Dispose();
    }
}
