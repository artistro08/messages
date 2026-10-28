namespace GMessages.Protocol.Pairing;

/// <summary>
/// The emoji lists the phone shows when pairing with a Google account, and the pick of one emoji from a number.
/// </summary>
/// <remarks>
/// Ported from <c>pairingEmojisV0</c> and <c>init()</c> in
/// <see href="https://github.com/mautrix/gmessages/blob/ba33f4875a491f5cb47db8a6baf42a8869f5f383/pkg/libgm/pair_google.go">pair_google.go</see>.
/// <see cref="V0"/> is copied verbatim, duplicates and missing variation selectors included, because the index
/// into it must match the phone's. <see cref="V1"/> is built the way Go's <c>init()</c> does: V0 without
/// duplicates (first one kept), plus the added emojis, minus the removed ones. All comparisons are ordinal.
/// </remarks>
public static class PairingEmojis
{
    // Copied Verbatim From pairingEmojisV0
    private static readonly string[] V0List = ["😁", "😅", "🤣", "🫠", "🥰", "😇", "🤩", "😘", "😜", "🤗", "🤔", "🤐", "😴", "🥶", "🤯", "🤠", "🥳", "🥸", "😎", "🤓", "🧐", "🥹", "😭", "😱", "😖", "🥱", "😮‍💨", "🤡", "💩", "👻", "👽", "🤖", "😻", "💌", "💘", "💕", "❤", "💢", "💥", "💫", "💬", "🗯", "💤", "👋", "🙌", "🙏", "✍", "🦶", "👂", "🧠", "🦴", "👀", "🧑", "🧚", "🧍", "👣", "🐵", "🐶", "🐺", "🦊", "🦁", "🐯", "🦓", "🦄", "🐑", "🐮", "🐷", "🐿", "🐰", "🦇", "🐻", "🐨", "🐼", "🦥", "🐾", "🐔", "🐥", "🐦", "🕊", "🦆", "🦉", "🪶", "🦩", "🐸", "🐢", "🦎", "🐍", "🐳", "🐬", "🦭", "🐠", "🐡", "🦈", "🪸", "🐌", "🦋", "🐛", "🐝", "🐞", "🪱", "💐", "🌸", "🌹", "🌻", "🌱", "🌲", "🌴", "🌵", "🌾", "☘", "🍁", "🍂", "🍄", "🪺", "🍇", "🍈", "🍉", "🍋", "🍌", "🍍", "🍎", "🍐", "🍒", "🍓", "🥝", "🥥", "🥑", "🥕", "🌽", "🌶", "🫑", "🥦", "🥜", "🍞", "🥐", "🥨", "🧀", "🍗", "🍔", "🍟", "🍕", "🌭", "🌮", "🥗", "🥣", "🍿", "🦀", "🦑", "🍦", "🍩", "🍪", "🍫", "🍰", "🍬", "🍭", "☕", "🫖", "🍹", "🥤", "🧊", "🥢", "🍽", "🥄", "🧭", "🏔", "🌋", "🏕", "🏖", "🪵", "🏗", "🏡", "🏰", "🛝", "🚂", "🛵", "🛴", "🛼", "🚥", "⚓", "🛟", "⛵", "✈", "🚀", "🛸", "🧳", "⏰", "🌙", "🌡", "🌞", "🪐", "🌠", "🌧", "🌀", "🌈", "☂", "⚡", "❄", "⛄", "🔥", "🎇", "🧨", "✨", "🎈", "🎉", "🎁", "🏆", "🏅", "⚽", "⚾", "🏀", "🏐", "🏈", "🎾", "🎳", "🏓", "🥊", "⛳", "⛸", "🎯", "🪁", "🔮", "🎮", "🧩", "🧸", "🪩", "🖼", "🎨", "🧵", "🧶", "🦺", "🧣", "🧤", "🧦", "🎒", "🩴", "👟", "👑", "👒", "🎩", "🧢", "💎", "🔔", "🎤", "📻", "🎷", "🪗", "🎸", "🎺", "🎻", "🥁", "📺", "🔋", "💻", "💿", "☎", "🕯", "💡", "📖", "📚", "📬", "✏", "✒", "🖌", "🖍", "📝", "💼", "📋", "📌", "📎", "🔑", "🔧", "🧲", "🪜", "🧬", "🔭", "🩹", "🩺", "🪞", "🛋", "🪑", "🛁", "🧹", "🧺", "🔱", "🏁", "🐪", "🐘", "🦃", "🍞", "🍜", "🍠", "🚘", "🤿", "🃏", "👕", "📸", "🏷", "✂", "🧪", "🚪", "🧴", "🧻", "🪣", "🧽", "🚸"];

    // Copied Verbatim From init()
    private static readonly string[] AddedV1   = ["🍋‍🟩", "🐦‍🔥", "🐲", "🪅", "🦜", "🏺", "🗿", "🫐", "⛽", "🍱", "🥡", "🧋", "🍼", "📐"];
    private static readonly string[] RemovedV1 = ["💻", "🤗", "💬", "👋", "😁", "😎", "😇", "🥰", "🤓", "🤩"];

    /// <summary>
    /// Gets the version 0 list.
    /// </summary>
    public static IReadOnlyList<string> V0 { get; } = Array.AsReadOnly(V0List);

    /// <summary>
    /// Gets the version 1 list.
    /// </summary>
    public static IReadOnlyList<string> V1 { get; } = Array.AsReadOnly(BuildV1());

    /// <summary>
    /// Picks the emoji for an auth number.
    /// </summary>
    /// <remarks>
    /// The number is taken modulo the list length. Go converts the <c>uint32</c> to a 64-bit <c>int</c> first, so
    /// the result is never negative; unsigned math here gives the same index.
    /// </remarks>
    /// <param name="authNumber">The first four bytes of the UKEY2 auth key, big-endian.</param>
    /// <param name="version">The confirmed verification code version: 0 or 1.</param>
    /// <returns>The emoji.</returns>
    /// <exception cref="InvalidDataException">The version is not 0 or 1 (Go's error text).</exception>
    public static string Pick(uint authNumber, int version)
    {
        IReadOnlyList<string> list = version switch
        {
            0 => V0,
            1 => V1,
            _ => throw new InvalidDataException($"unsupported verification code version {version}"),
        };
        return list[(int)(authNumber % (uint)list.Count)];
    }

    // Port Of init(): Deduplicate (First Kept), Append Added, Delete Removed
    private static string[] BuildV1()
    {
        var removed = new HashSet<string>(RemovedV1, StringComparer.Ordinal);
        return [.. V0List.Distinct(StringComparer.Ordinal).Concat(AddedV1).Where(e => !removed.Contains(e))];
    }
}
