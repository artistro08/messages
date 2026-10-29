namespace GMessages.Protocol.Tests.Fakes;

/// <summary>
/// A response body that returns some bytes, then stays open until <see cref="End"/> or cancellation.
/// </summary>
/// <remarks>Test-only. Stands in for a long-poll connection that is open but quiet.</remarks>
/// <param name="initial">The bytes to return first.</param>
internal sealed class OpenStream(byte[] initial) : Stream
{
    private readonly TaskCompletionSource _ended   = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _reading = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Queue<(byte[] Bytes, TaskCompletionSource Taken)> _pushed = new();
    private TaskCompletionSource _signal = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private TaskCompletionSource? _takenPending;
    private int _position;

    /// <inheritdoc/>
    public override bool CanRead => true;

    /// <inheritdoc/>
    public override bool CanSeek => false;

    /// <inheritdoc/>
    public override bool CanWrite => false;

    /// <inheritdoc/>
    public override long Length => throw new NotSupportedException();

    /// <inheritdoc/>
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    /// <summary>Completes once the reader has taken the initial bytes and waits for more.</summary>
    public Task Waiting => _reading.Task;

    /// <summary>
    /// Hands more bytes to the reader.
    /// </summary>
    /// <param name="bytes">The bytes; must fit in one read.</param>
    /// <returns>A task that completes once the reader has taken them and started its next read (idle timer reset).</returns>
    public Task Push(byte[] bytes)
    {
        var taken = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_pushed)
        {
            _pushed.Enqueue((bytes, taken));
            _signal.TrySetResult();
        }

        return taken.Task;
    }

    /// <summary>Ends the stream: pending and later reads return 0.</summary>
    public void End() => _ended.TrySetResult();

    /// <inheritdoc/>
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        // A New Read Means The Reader Is Done With The Last Bytes And Has Reset Its Idle Timer
        _takenPending?.TrySetResult();
        _takenPending = null;
        if (_position < initial.Length)
        {
            int n = Math.Min(buffer.Length, initial.Length - _position);
            initial.AsMemory(_position, n).CopyTo(buffer);
            _position += n;
            return n;
        }

        while (true)
        {
            Task signal;
            lock (_pushed)
            {
                if (_pushed.TryDequeue(out (byte[] Bytes, TaskCompletionSource Taken) next))
                {
                    next.Bytes.CopyTo(buffer);
                    _takenPending = next.Taken;
                    return next.Bytes.Length;
                }

                _signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                signal  = _signal.Task;
            }

            _reading.TrySetResult();
            await Task.WhenAny(_ended.Task, signal).WaitAsync(cancellationToken);
            if (_ended.Task.IsCompleted)
            {
                return 0;
            }
        }
    }

    /// <inheritdoc/>
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    /// <inheritdoc/>
    public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer, offset, count, CancellationToken.None).GetAwaiter().GetResult();

    /// <inheritdoc/>
    public override void Flush()
    {
    }

    /// <inheritdoc/>
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    /// <inheritdoc/>
    public override void SetLength(long value) => throw new NotSupportedException();

    /// <inheritdoc/>
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
