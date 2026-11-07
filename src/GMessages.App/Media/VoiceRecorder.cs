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
/// The exception that is thrown when the microphone cannot record. Access is off in Settings > Privacy &amp; security
/// > Microphone, or there is no microphone.
/// </summary>
internal sealed class MicrophoneUnavailableException : Exception
{
    /// <summary>
    /// Initializes a new instance of the <see cref="MicrophoneUnavailableException"/> class with a reason.
    /// </summary>
    /// <param name="reason">
    /// The reason for the log: <c>AccessDenied</c>, <c>NoDevice</c>, the error type or the status of the graph.
    /// </param>
    public MicrophoneUnavailableException(string reason)
        : base(reason)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="MicrophoneUnavailableException"/> class without a reason.
    /// </summary>
    public MicrophoneUnavailableException()
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="MicrophoneUnavailableException"/> class with a reason and the error
    /// that caused it.
    /// </summary>
    /// <param name="reason">The reason for the log.</param>
    /// <param name="inner">The error that caused the exception.</param>
    public MicrophoneUnavailableException(string reason, Exception inner)
        : base(reason, inner)
    {
    }
}

/// <summary>
/// Records a voice message to an M4A/AAC temp file, with live input levels. The microphone is the one chosen in
/// Settings, or the Windows default.
/// </summary>
/// <remarks>
/// <para>
/// The recorder uses 1 <see cref="AudioGraph"/>. Its device input node feeds an M4A/AAC file output node and a frame
/// output node. The frames give the level that the live waveform shows. See <see cref="TakeLevel"/>. The read of a
/// frame copies its samples with <c>Buffer.CreateCopyFromMemoryBuffer</c>. Thus Native AOT needs no COM interface such
/// as <c>IMemoryBufferByteAccess</c> and no unsafe code. The copy has a <c>Length</c> of 0. The code must
/// set it to the length of the frame.
/// </para>
/// <para>
/// Before the graph starts, <see cref="AppCapability"/> checks that microphone access is on. When the user has not
/// decided yet, <see cref="MicrophoneConsent"/> asks through <c>ActivateAudioInterfaceAsync</c>. That call shows the
/// consent prompt of Windows to desktop apps. <c>AppCapability.RequestAccessAsync</c> needs a CoreWindow, and a desktop
/// app has none. When access stays off, the recorder throws, and the dialog of the chat pane points to the settings of
/// Windows.
/// </para>
/// <para>
/// <see cref="StopAsync"/> finishes the file. A dispose without it drops the graph, and the caller deletes the file.
/// When access is off or there is no microphone, the recorder throws <see cref="MicrophoneUnavailableException"/>. A
/// test run never touches the real microphone. The test hook refuses or copies a fixture file. See
/// <see cref="TestHooks.FakeMicrophone"/>. The log never gets the audio or the path.
/// </para>
/// </remarks>
internal sealed class VoiceRecorder : IDisposable
{
    // The medium AAC quality of Windows gives clear speech in small files.
    private const AudioEncodingQuality Quality = AudioEncodingQuality.Medium;

    private readonly AudioGraph? _graph;
    private readonly AudioFileOutputNode? _file;
    private readonly AudioFrameOutputNode? _frames;

    // The path of the file, for its size in the log. The log never gets the path.
    private readonly string _path;

    // The fake level of a test run follows the clock. The "unfinished" fake microphone fails to finish the file.
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
    /// Starts a recording to a new file.
    /// </summary>
    /// <param name="path">The temp file for the recording. The method makes its folder when it is missing.</param>
    /// <param name="microphoneId">
    /// The Windows device ID of the microphone chosen in Settings, or null for the Windows default. When the chosen
    /// microphone is not plugged in, the recording uses the default.
    /// </param>
    /// <returns>The running recorder.</returns>
    /// <exception cref="MicrophoneUnavailableException">
    /// Microphone access is off, or there is no microphone.
    /// </exception>
    public static async Task<VoiceRecorder> StartAsync(string path, string? microphoneId)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        if (TestHooks.FakeMicrophone() is { } fake)
        {
            if (fake is "denied" or "none")
            {
                throw new MicrophoneUnavailableException(fake == "denied" ? "AccessDenied" : "NoDevice");
            }

            // The "unfinished" fake makes a file that never finishes, for the failure note.
            if (fake == "unfinished")
            {
                await File.WriteAllBytesAsync(path, []);
                return new VoiceRecorder(path, null, null, null, fakeUnfinished: true);
            }

            File.Copy(fake, path, overwrite: true);
            return new VoiceRecorder(path, null, null, null);
        }

        // Access must be on first. An AudioGraph alone gets only a refusal. Thus the app asks Windows for its prompt
        // when the user is undecided.
        await EnsureMicrophoneAsync();

        AudioGraph? graph = null;
        try
        {
            // The clock of the graph needs an output device, which any PC with a microphone has.
            CreateAudioGraphResult created = await AudioGraph.CreateAsync(new AudioGraphSettings(AudioRenderCategory.Speech));
            if (created.Status != AudioGraphCreationStatus.Success)
            {
                throw new MicrophoneUnavailableException(created.Status.ToString());
            }

            graph = created.Graph;

            CreateAudioDeviceInputNodeResult input = await CreateInputAsync(graph, microphoneId);
            if (input.Status != AudioDeviceNodeCreationStatus.Success)
            {
                MicrophoneConsent.Log("device input", input.Status.ToString());
                throw new MicrophoneUnavailableException(input.Status.ToString());
            }

            // TODO: This diagnostic is for the owner report that the app did not use the chosen microphone. Version
            // 0.5.9 found the cause. A pick on a reopened Settings page was not saved, so the old microphone recorded.
            // Remove the diagnostic when the owner confirms the fix.
            LogOpenedDevice(input.DeviceInputNode.Device?.Id, microphoneId);

            await File.WriteAllBytesAsync(path, []);
            StorageFile storage                      = await StorageFile.GetFileFromPathAsync(path);
            CreateAudioFileOutputNodeResult outFile  = await graph.CreateFileOutputNodeAsync(storage, MediaEncodingProfile.CreateM4a(Quality));
            if (outFile.Status != AudioFileNodeCreationStatus.Success)
            {
                throw new IOException(outFile.Status.ToString());
            }

            AudioFrameOutputNode frames = graph.CreateFrameOutputNode();
            input.DeviceInputNode.AddOutgoingConnection(outFile.FileOutputNode);
            input.DeviceInputNode.AddOutgoingConnection(frames);
            graph.Start();

            var recorder = new VoiceRecorder(path, graph, outFile.FileOutputNode, frames);
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
    /// Makes the input node of the graph from the microphone chosen in Settings. The method uses the Windows default
    /// when no microphone is chosen or the chosen one is not plugged in.
    /// </summary>
    /// <remarks>
    /// The chosen device comes from <see cref="DeviceInformation.CreateFromIdAsync(string)"/>. A removed device throws
    /// there or reads <see cref="DeviceInformation.IsEnabled"/> as false. The log gets only "chosen" or "default",
    /// never the name or the ID of the device.
    /// </remarks>
    /// <param name="graph">The graph to add the node to.</param>
    /// <param name="microphoneId">The device ID of the chosen microphone, or null for the default.</param>
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
                // The device is unplugged or removed, so the recording uses the default.
                device = null;
            }
        }

        // MediaCategory.Other follows the "Default" device in Settings. Speech follows the communications default
        // instead. On the PC of the owner, that is the Yeti, and the default is the Surface.
        if (device is not { IsEnabled: true })
        {
            MicrophoneConsent.Log("device", "default");
            return await graph.CreateDeviceInputNodeAsync(MediaCategory.Other);
        }

        MicrophoneConsent.Log("device", "chosen");
        return await graph.CreateDeviceInputNodeAsync(MediaCategory.Other, null, device);
    }

    // Logs which device the graph opened, compared with the chosen device and the defaults of Windows. The log never
    // gets names or IDs.
    private static void LogOpenedDevice(string? openedId, string? chosenId)
    {
        string defaultId        = MediaDevice.GetDefaultAudioCaptureId(AudioDeviceRole.Default);
        string communicationId  = MediaDevice.GetDefaultAudioCaptureId(AudioDeviceRole.Communications);
        string opened           = openedId is null ? "unknown"
            : string.Equals(openedId, chosenId, StringComparison.OrdinalIgnoreCase) ? "chosen"
            : string.Equals(openedId, defaultId, StringComparison.OrdinalIgnoreCase) ? "default"
            : string.Equals(openedId, communicationId, StringComparison.OrdinalIgnoreCase) ? "communications default"
            : "other";

        MicrophoneConsent.Log("opened", $"{opened}; chosen is default: {string.Equals(chosenId, defaultId, StringComparison.OrdinalIgnoreCase)}");
    }

    /// <summary>
    /// Checks that Windows lets Messages use the microphone. When the user has not decided, the method asks with the
    /// prompt of Windows.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The method runs on the UI thread, so the call path from the chat pane must have no <c>ConfigureAwait(false)</c>.
    /// Windows shows its prompt only when <c>ActivateAudioInterfaceAsync</c> runs there. Microsoft documents that call
    /// for desktop apps. Desktop apps do not support <see cref="AppCapability.RequestAccessAsync"/>, because it needs a
    /// CoreWindow. It failed with <c>RPC_E_DISCONNECTED</c> 0x80010108 in the 0.5.2 log of the owner.
    /// <c>MediaCapture</c> failed in the same way in 0.5.1. An <see cref="AudioGraph"/> alone gets only a refusal.
    /// </para>
    /// <para>
    /// When access is allowed, the recording starts. Else the method asks through <see cref="MicrophoneConsent"/> and
    /// checks again. Windows prompts when the user can still decide, and answers at once when the user cannot. Then
    /// <see cref="MicrophoneAccess.CanRecord"/> decides. The method does not trust the first check alone. In the 0.5.5
    /// log of the owner, that check read "DeniedByUser" while the consent store of Windows said "Prompt". When the app
    /// cannot record, the method throws <see cref="MicrophoneUnavailableException"/>. The chat pane then shows its own
    /// dialog, which opens the microphone settings of Windows.
    /// </para>
    /// </remarks>
    /// <returns>A task that completes when access is on.</returns>
    /// <exception cref="MicrophoneUnavailableException">
    /// Access is not on, there is no microphone, or the check failed.
    /// </exception>
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

        if (!MicrophoneAccess.ShouldAsk(AccessOf(before)))
        {
            return;
        }

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
        // A test run gets a steady wave.
        if (_frames is null)
        {
            return (float)(0.5 + (0.4 * Math.Sin((Environment.TickCount64 - _fakeStart) / 150.0)));
        }

        // The samples of the graph are 32-bit floats.
        using AudioFrame frame   = _frames.GetFrame();
        using AudioBuffer buffer = frame.LockBuffer(AudioBufferAccessMode.Read);
        if (buffer.Length == 0)
        {
            return 0;
        }

        // The copy has a Length of 0 and only its Capacity set. Its bytes count only when its Length is the
        // length of the frame.
        var copy    = Windows.Storage.Streams.Buffer.CreateCopyFromMemoryBuffer(buffer);
        copy.Length = Math.Min(copy.Capacity, buffer.Length);
        return VoiceWave.LevelOf(copy.ToArray());
    }

    /// <summary>
    /// Pauses the recording. The graph stops, so no audio reaches the file or the levels until <see cref="Resume"/>.
    /// </summary>
    /// <remarks>
    /// A stopped graph processes no quanta, and the file output node is not finalized. Thus a resume adds to the same
    /// file, and the file never holds the paused time.
    /// </remarks>
    public void Pause() => _graph?.Stop();

    /// <summary>
    /// Resumes a paused recording into the same file.
    /// </summary>
    public void Resume() => _graph?.Start();

    /// <summary>
    /// Stops the recording, then finishes and closes the file, so that the caller can read it at once.
    /// </summary>
    /// <remarks>
    /// In the 0.5.10 log of the owner, Done failed with an <see cref="IOException"/> whose cause was not in the log.
    /// A finalize that fails with <c>Unknown</c> can keep the file open while the graph lives. A scratch graph showed
    /// this as a sharing violation on read. Thus the method disposes the graph before the file is read. The log gets
    /// the finalize result and the size of the file in bytes, never its path. A finalized file output node is already
    /// closed. A second close throws <see cref="ObjectDisposedException"/>, so the method disposes only the graph.
    /// </remarks>
    /// <returns>A task that completes when the file is complete and closed.</returns>
    /// <exception cref="IOException">The recorder cannot finish the file.</exception>
    public async Task StopAsync()
    {
        // In a test run, the fixture is already complete or never finishes.
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
    /// Stops the graph and releases the microphone and the file. The file stays unfinished unless
    /// <see cref="StopAsync"/> ran.
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
