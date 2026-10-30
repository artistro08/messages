using System.Runtime.InteropServices.WindowsRuntime;
using GMessages.Presentation.Messaging;
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
/// Records a voice message from the default microphone straight to an M4A/AAC temp file, with live input levels.
/// </summary>
/// <remarks>
/// <para>
/// One <see cref="AudioGraph"/>: the device input node feeds a file output node (M4A, AAC) and a frame output node,
/// whose frames give the level the live waveform shows (<see cref="TakeLevel"/>). Reading a frame copies its samples
/// with <c>Buffer.CreateCopyFromMemoryBuffer</c>, so no COM interface (<c>IMemoryBufferByteAccess</c>) or unsafe code
/// is needed under Native AOT; the copy's <c>Length</c> must be set to the frame's, since it comes back 0.
/// </para>
/// <para>
/// Before the graph, <see cref="AppCapability"/> checks that microphone access is on. A desktop app can't make Windows
/// ask for it (the <c>Request*</c> methods aren't supported in desktop apps), so access that isn't on throws and the
/// chat pane's own dialog points to Windows' settings.
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

    // A Test Run's Fake Level Follows The Clock
    private readonly long _fakeStart = Environment.TickCount64;

    private bool _disposed;

    private VoiceRecorder(AudioGraph? graph, AudioFileOutputNode? file, AudioFrameOutputNode? frames)
    {
        _graph  = graph;
        _file   = file;
        _frames = frames;
    }

    /// <summary>
    /// Starts recording to a new file.
    /// </summary>
    /// <param name="path">The temp file to record to (its folder is made if missing).</param>
    /// <returns>The running recorder.</returns>
    /// <exception cref="MicrophoneUnavailableException">Microphone access is off, or there is no microphone.</exception>
    public static async Task<VoiceRecorder> StartAsync(string path)
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

            File.Copy(fake, path, overwrite: true);
            return new VoiceRecorder(null, null, null);
        }

        // Access Must Be On First: A Desktop App Can't Show Windows' Prompt, And An AudioGraph Alone Is Only Refused
        CheckMicrophone();

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
            CreateAudioDeviceInputNodeResult input = await graph.CreateDeviceInputNodeAsync(MediaCategory.Speech);
            if (input.Status != AudioDeviceNodeCreationStatus.Success)
            {
                throw new MicrophoneUnavailableException(input.Status.ToString());
            }

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

            var recorder = new VoiceRecorder(graph, out_file.FileOutputNode, frames);
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
    /// Checks whether Windows lets Messages use the microphone, without asking.
    /// </summary>
    /// <remarks>
    /// A desktop app can't show Windows' "Let Messages use your microphone?" prompt: Microsoft lists the <c>Request*</c>
    /// methods as unsupported in desktop apps, and <see cref="AppCapability.RequestAccessAsync"/> failed with
    /// <c>RPC_E_DISCONNECTED</c> (0x80010108) on the UI thread (owner's 0.5.2 log), as <c>MediaCapture</c> did in 0.5.1.
    /// An <see cref="AudioGraph"/> alone is only refused. So anything but <see cref="AppCapabilityAccessStatus.Allowed"/>
    /// (undecided, refused, or off for the whole PC) throws <see cref="MicrophoneUnavailableException"/> with the status
    /// name, and the chat pane shows its own dialog that opens Windows' microphone settings.
    /// </remarks>
    /// <exception cref="MicrophoneUnavailableException">Access isn't on, or the check failed.</exception>
    private static void CheckMicrophone()
    {
        AppCapabilityAccessStatus status;
        try
        {
            status = AppCapability.Create("microphone").CheckAccess();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            throw new MicrophoneUnavailableException($"{ex.GetType().Name} 0x{ex.HResult:X8}", ex);
        }

        if (status != AppCapabilityAccessStatus.Allowed)
        {
            throw new MicrophoneUnavailableException(status.ToString());
        }
    }

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
    /// Stops recording and finishes the file.
    /// </summary>
    /// <returns>A task that completes when the file is complete.</returns>
    /// <exception cref="IOException">The file couldn't be finished.</exception>
    public async Task StopAsync()
    {
        if (_graph is null || _file is null)
        {
            return;
        }

        _graph.Stop();
        TranscodeFailureReason result = await _file.FinalizeAsync();
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
