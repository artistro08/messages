using System.Security.Cryptography;
using System.Text;
using GMessages.Protocol.Proto;

namespace GMessages.Presentation.Messaging;

/// <summary>
/// Fetches participants' photos from the phone, in batches, and keeps the newest 200 in memory.
/// </summary>
/// <remarks>
/// <para>
/// Every ID asked for within 50 ms goes to the phone in one <c>GetParticipantThumbnail</c> request; asking for an ID
/// already on its way shares that answer. A participant without a photo is remembered as null, so the phone isn't
/// asked again. A failed request (not connected, phone silent) answers null without remembering it.
/// </para>
/// <para>
/// <see cref="GetFileUriAsync"/> writes a photo to <c>{avatar folder}\{id hash}.png</c> for notifications; the file
/// name is a hash, so no number or name lands on disk. The folder holds contacts' photos, so
/// <see cref="Connection.ConnectionController"/> deletes it with the session. Photos are never logged. Safe to call
/// from any thread; awaits resume on the caller's context.
/// </para>
/// </remarks>
public sealed class AvatarCache
{
    private const int Capacity = 200;

    private static readonly TimeSpan BatchWindow = TimeSpan.FromMilliseconds(50);

    private readonly ChatStore _store;
    private readonly string _folder;
    private readonly Lock _lock = new();

    // LRU: Newest First; The Map Finds An ID's Node
    private readonly LinkedList<(string Id, byte[]? Photo)> _recent = [];
    private readonly Dictionary<string, LinkedListNode<(string Id, byte[]? Photo)>> _byId = [];

    // Answers Not In Yet (Waiting For The Batch Or On Their Way), And The IDs Of The Batch Still Collecting
    private readonly Dictionary<string, TaskCompletionSource<byte[]?>> _pending = [];
    private List<string>? _batch;

    /// <summary>
    /// Creates the cache.
    /// </summary>
    /// <param name="store">Gives the phone client.</param>
    /// <param name="avatarFolder">Where <see cref="GetFileUriAsync"/> writes photos (<c>{DataFolder}\Avatars</c>).</param>
    public AvatarCache(ChatStore store, string avatarFolder)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentException.ThrowIfNullOrEmpty(avatarFolder);

        _store  = store;
        _folder = avatarFolder;
    }

    /// <summary>
    /// Gets a participant's photo.
    /// </summary>
    /// <param name="participantId">The participant ID.</param>
    /// <param name="ct">Cancels the wait (not the shared request).</param>
    /// <returns>The image bytes, or null when the participant has no photo or the phone couldn't be asked.</returns>
    public Task<byte[]?> GetAsync(string participantId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(participantId);

        lock (_lock)
        {
            // Cached: Mark As Newest
            if (_byId.TryGetValue(participantId, out var node))
            {
                _recent.Remove(node);
                _recent.AddFirst(node);
                return Task.FromResult(node.Value.Photo);
            }

            // Already Asked For: Share The Answer
            if (_pending.TryGetValue(participantId, out var waiting))
            {
                return waiting.Task.WaitAsync(ct);
            }

            // Join The Collecting Batch, Starting One When None Is Open
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
    /// Gets a file URI of a participant's photo for a notification, writing the file the first time.
    /// </summary>
    /// <param name="participantId">The participant ID.</param>
    /// <param name="ct">Cancels the wait.</param>
    /// <returns>A <c>file:///</c> URI, or null when the participant has no photo.</returns>
    /// <exception cref="IOException">The avatar folder can't be written.</exception>
    /// <exception cref="UnauthorizedAccessException">The avatar folder can't be written.</exception>
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

        // Written Aside, Then Moved In, So A Toast Never Reads Half A File
        Directory.CreateDirectory(_folder);
        string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        await File.WriteAllBytesAsync(temp, photo, ct).ConfigureAwait(false);
        try
        {
            File.Move(temp, path, overwrite: true);
        }
        catch (IOException)
        {
            // Another Call Moved It In First And A Toast Has It Open: That Copy Is The Same Photo
            File.Delete(temp);
        }

        return new Uri(path).AbsoluteUri;
    }

    /// <summary>
    /// Forgets every photo kept in memory (files stay).
    /// </summary>
    public void Clear()
    {
        lock (_lock)
        {
            _recent.Clear();
            _byId.Clear();
        }
    }

    // =========================================================================
    // BATCHING
    // =========================================================================

    // Waits Out The Batch Window, Then Asks The Phone For Every ID Collected
    private async Task SendBatchAsync()
    {
        await Task.Delay(BatchWindow).ConfigureAwait(false);

        List<string> ids;
        lock (_lock)
        {
            ids    = _batch!;
            _batch = null;
        }

        // Ask; Any Failure Answers Null Without Remembering It
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
            // Nothing Logged: The Caller Shows The Initial Instead
        }

        // Remember And Answer
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

                if (_pending.Remove(id, out var answer))
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

    // Caller Holds The Lock. Adds As Newest, Dropping The Oldest Past Capacity
    private void Remember(string id, byte[]? photo)
    {
        if (_byId.Remove(id, out var old))
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

    // SHA-256 Of The ID, First 16 Bytes As Hex: No Number Or Name On Disk
    private static string FileName(string participantId) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(participantId)), 0, 16) + ".png";
}
