namespace GMessages.Protocol.Tests.Fakes;

/// <summary>
/// A response body that returns some bytes, then stays open until <see cref="End"/> or cancellation.
/// </summary>
/// <remarks>Test-only. Stands in for a long-poll connection that is open but quiet.</remarks>
/// <param name="initial">The bytes to return first.</param>
internal sealed class OpenStream(byte[] initial) : Stream
{
    private readonly TaskCompletionSource _ended = new(TaskCreationOptions.RunContinuationsAsynchronously);
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

    /// <summary>Ends the stream: pending and later reads return 0.</summary>
    public void End() => _ended.TrySetResult();

    /// <inheritdoc/>
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (_position < initial.Length)
        {
            int n = Math.Min(buffer.Length, initial.Length - _position);
            initial.AsMemory(_position, n).CopyTo(buffer);
            _position += n;
            return n;
        }

        await _ended.Task.WaitAsync(cancellationToken);
        return 0;
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
