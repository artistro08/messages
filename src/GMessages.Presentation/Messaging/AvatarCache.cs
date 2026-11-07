using System.Security.Cryptography;
using System.Text;
using GMessages.Protocol.Proto;

namespace GMessages.Presentation.Messaging;

/// <summary>
/// Fetches the photos of participants from the phone in batches, and keeps the newest 200 in memory.
/// </summary>
/// <remarks>
/// <para>
/// The cache sends all IDs that callers ask for within 50 ms to the phone in 1 <c>GetParticipantThumbnail</c>
/// request. A request for an ID that is already on its way shares that answer. The cache remembers a participant
/// without a photo as null, so it does not ask the phone again. A failed request answers null and is not remembered.
/// The request fails when the phone is not connected or does not answer.
/// </para>
/// <para>
/// <see cref="GetFileUriAsync"/> writes a photo to <c>{avatar folder}\{id hash}.png</c> for notifications. The file
/// name is a hash, so no number or name goes to disk. The folder holds the photos of contacts, so
/// <see cref="Connection.ConnectionController"/> deletes it with the session. The log never holds photos. Any thread
/// can call the cache, and awaits continue on the context of the caller.
/// </para>
/// </remarks>
public sealed class AvatarCache
{
    private const int Capacity = 200;

    private static readonly TimeSpan BatchWindow = TimeSpan.FromMilliseconds(50);

    private readonly ChatStore _store;
    private readonly string _folder;
    private readonly Lock _lock = new();

    // The least recently used list, newest first. The map finds the node of an ID.
    private readonly LinkedList<(string Id, byte[]? Photo)> _recent = [];
    private readonly Dictionary<string, LinkedListNode<(string Id, byte[]? Photo)>> _byId = [];

    // The answers that are not in yet, because they wait for the batch or are on their way. _batch holds the IDs of
    // the batch that still collects.
    private readonly Dictionary<string, TaskCompletionSource<byte[]?>> _pending = [];
    private List<string>? _batch;

    /// <summary>
    /// Creates the cache.
    /// </summary>
    /// <param name="store">Gives the phone client.</param>
    /// <param name="avatarFolder">
    /// The folder where <see cref="GetFileUriAsync"/> writes photos: <c>{DataFolder}\Avatars</c>.
    /// </param>
    public AvatarCache(ChatStore store, string avatarFolder)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentException.ThrowIfNullOrEmpty(avatarFolder);

        _store  = store;
        _folder = avatarFolder;
    }

    /// <summary>
    /// Gets the photo of a participant.
    /// </summary>
    /// <param name="participantId">The participant ID.</param>
    /// <param name="ct">Cancels the wait. It does not cancel the shared request.</param>
    /// <returns>The image bytes, or null when the participant has no photo or the request failed.</returns>
    public Task<byte[]?> GetAsync(string participantId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(participantId);

        lock (_lock)
        {
            if (_byId.TryGetValue(participantId, out LinkedListNode<(string Id, byte[]? Photo)>? node))
            {
                _recent.Remove(node);
                _recent.AddFirst(node);
                return Task.FromResult(node.Value.Photo);
            }

            if (_pending.TryGetValue(participantId, out TaskCompletionSource<byte[]?>? waiting))
            {
                return waiting.Task.WaitAsync(ct);
            }

            // The ID joins the batch that collects, and starts a batch when none is open.
            var answer = new TaskCompletionSource<byte[]?>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pending[participantId] = answer;
            if (_batch is null)
            {
                _batch = [];
                _      = SendBatchAsync();
            }

            _batch.Add(participantId);
            return answer.Task.WaitAsync(ct);
        }
    }

    /// <summary>
    /// Gets a file URI of the photo of a participant for a notification, and writes the file the first time.
    /// </summary>
    /// <param name="participantId">The participant ID.</param>
    /// <param name="ct">Cancels the wait.</param>
    /// <returns>A <c>file:///</c> URI, or null when the participant has no photo.</returns>
    /// <exception cref="IOException">The avatar folder cannot be written.</exception>
    /// <exception cref="UnauthorizedAccessException">The avatar folder cannot be written.</exception>
    public async Task<string?> GetFileUriAsync(string participantId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(participantId);

        string path = Path.Combine(_folder, FileName(participantId));
        if (File.Exists(path))
        {
            return new Uri(path).AbsoluteUri;
        }

        byte[]? photo = await GetAsync(participantId, ct).ConfigureAwait(false);
        if (photo is null)
        {
            return null;
        }

        // The method writes to a temp file and then moves it in, so a toast never reads half a file.
        Directory.CreateDirectory(_folder);
        string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        await File.WriteAllBytesAsync(temp, photo, ct).ConfigureAwait(false);
        try
        {
            File.Move(temp, path, overwrite: true);
        }
        catch (IOException)
        {
            // Another call moved its file in first, and a toast holds it open. That file is the same photo.
            File.Delete(temp);
        }

        return new Uri(path).AbsoluteUri;
    }

    /// <summary>
    /// Forgets every photo in memory. The files stay.
    /// </summary>
    public void Clear()
    {
        lock (_lock)
        {
            _recent.Clear();
            _byId.Clear();
        }
    }

    // Waits for the batch window, then asks the phone for each collected ID.
    private async Task SendBatchAsync()
    {
        await Task.Delay(BatchWindow).ConfigureAwait(false);

        List<string> ids;
        lock (_lock)
        {
            ids    = _batch!;
            _batch = null;
        }

        // Any failure answers null, and the cache does not remember it.
        Dictionary<string, byte[]> photos = [];
        bool asked                        = false;
        try
        {
            GetThumbnailResponse response = await _store.RequireClient().GetParticipantThumbnailAsync(ids, CancellationToken.None).ConfigureAwait(false);
            foreach (GetThumbnailResponse.Types.Thumbnail t in response.Thumbnail)
            {
                if (t.Data?.ImageBuffer is { IsEmpty: false } buffer)
                {
                    photos[t.Identifier] = buffer.ToByteArray();
                }
            }

            asked = true;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // The log gets nothing, because the caller shows the initial instead.
        }

        List<(TaskCompletionSource<byte[]?> Answer, byte[]? Photo)> answers = [];
        lock (_lock)
        {
            foreach (string id in ids)
            {
                byte[]? photo = photos.GetValueOrDefault(id);
                if (asked)
                {
                    Remember(id, photo);
                }

                if (_pending.Remove(id, out TaskCompletionSource<byte[]?>? answer))
                {
                    answers.Add((answer, photo));
                }
            }
        }

        foreach ((TaskCompletionSource<byte[]?> answer, byte[]? photo) in answers)
        {
            answer.TrySetResult(photo);
        }
    }

    // Adds the photo as the newest and drops the oldest past the capacity. The caller holds the lock.
    private void Remember(string id, byte[]? photo)
    {
        if (_byId.Remove(id, out LinkedListNode<(string Id, byte[]? Photo)>? old))
        {
            _recent.Remove(old);
        }

        _byId[id] = _recent.AddFirst((id, photo));

        if (_recent.Count > Capacity)
        {
            _byId.Remove(_recent.Last!.Value.Id);
            _recent.RemoveLast();
        }
    }

    // The first 16 bytes of the SHA-256 of the ID, as hex. Thus no number or name goes to disk.
    private static string FileName(string participantId) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(participantId)), 0, 16) + ".png";
}
