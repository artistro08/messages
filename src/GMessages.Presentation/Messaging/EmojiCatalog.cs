namespace GMessages.Presentation.Messaging;

/// <summary>
/// The emoji the reaction picker's More reactions offers: common emoji in Windows' emoji panel categories, each with a few
/// search words.
/// </summary>
/// <remarks>
/// The search words are English and never shown except the first (the emoji's tooltip); they match what is typed in the
/// picker's search box (see <see cref="Search"/>). Each category's entries are one string: an emoji, its words, then
/// <c>|</c> before the next. An emoji whose first character shows as plain text by default carries U+FE0F (the emoji
/// variation selector) after it.
/// </remarks>
// ponytail: about 550 hand-picked emoji with short search words; load Unicode's full CLDR table if people miss ones
public static class EmojiCatalog
{
    /// <summary>
    /// Gets the categories in order, each with its name (shown over its emoji) and its emoji with their search words.
    /// </summary>
    public static IReadOnlyList<(string Name, IReadOnlyList<(string Emoji, string Words)> Emoji)> Categories { get; } =
    [
        (ChatCopy.EmojiSmileys, Parse(
            "😀 grin happy smile|😃 smile happy joy|😄 smile laugh happy|😁 grin beam|😆 laugh lol|😅 sweat laugh relief|🤣 rofl laugh lol|"
            + "😂 joy tears laugh lol|🙂 smile slight|🙃 upside down silly|😉 wink|😊 blush smile|😇 angel innocent halo|🥰 love hearts adore|"
            + "😍 heart eyes love|🤩 star struck wow|😘 kiss blow|😗 kiss|😚 kiss blush|😋 yum tasty|😛 tongue|😜 wink tongue crazy|"
            + "🤪 zany crazy goofy|😝 tongue squint|🤑 money rich|🤗 hug|🤭 oops giggle|🤫 shush quiet|🤔 think hmm|🤐 zip quiet|"
            + "🤨 raised eyebrow skeptic|😐 neutral meh|😑 expressionless|😶 speechless|😏 smirk|😒 unamused meh|🙄 eye roll|😬 grimace awkward|"
            + "😌 relieved calm|😔 pensive sad|😪 sleepy|🤤 drool|😴 sleep tired zzz|😷 mask sick|🤒 sick fever|🤕 hurt bandage|"
            + "🤢 nausea sick|🤮 vomit sick|🥵 hot heat|🥶 cold freeze|🥴 woozy dizzy|😵 dizzy|🤯 mind blown|🤠 cowboy|"
            + "🥳 party celebrate|😎 cool sunglasses|🤓 nerd geek|🧐 monocle curious|😕 confused|😟 worried|🙁 frown sad|😮 wow open mouth surprise|"
            + "😯 hushed surprise|😲 astonished shock|😳 flushed embarrassed|🥺 pleading puppy eyes|😦 frown|😧 anguish|😨 fear scared|😰 anxious sweat|"
            + "😥 sad relieved|😢 cry sad tear|😭 sob cry|😱 scream fear|😖 confounded|😣 persevere|😞 disappointed sad|😓 sweat|"
            + "😩 weary tired|😫 tired|🥱 yawn bored|😤 triumph huff|😡 angry mad rage|😠 angry mad|🤬 swear curse angry|😈 devil smile|"
            + "💀 skull dead|💩 poop|🤡 clown|👻 ghost|👽 alien|🤖 robot|😺 cat smile|😹 cat joy|😻 cat heart eyes|🙈 see no evil monkey|"
            + "🙉 hear no evil monkey|🙊 speak no evil monkey")),
        (ChatCopy.EmojiPeople, Parse(
            "👍 thumbs up yes like|👎 thumbs down no dislike|👌 ok perfect|🤌 pinched fingers|✌️ peace victory|🤞 fingers crossed luck|"
            + "🤟 love you|🤘 rock horns|🤙 call me shaka|👈 point left|👉 point right|👆 point up|👇 point down|☝️ point up one|"
            + "✋ hand stop high five|🤚 raised back hand|🖐️ hand fingers|🖖 vulcan|👋 wave hello bye|👏 clap applause|🙌 raise hands praise yay|"
            + "👐 open hands|🤲 palms up|🤝 handshake deal|🙏 pray please thanks|✍️ write|💅 nail polish|💪 muscle strong flex|🦾 arm strong|"
            + "👀 eyes look|👁️ eye|👅 tongue|👄 lips mouth|💋 kiss lips|🧠 brain smart|🫶 heart hands love|🤷 shrug|🤦 facepalm|"
            + "🙋 raise hand hi|🙆 ok gesture|🙅 no gesture|💁 tipping hand|🙇 bow sorry|🧍 standing|🏃 run|🚶 walk|💃 dance|🕺 dance man|"
            + "👶 baby|🧒 child|👦 boy|👧 girl|🧑 person|👨 man|👩 woman|🧓 older person|👴 old man|👵 old woman|👪 family")),
        (ChatCopy.EmojiHearts, Parse(
            "❤️ red heart love|🧡 orange heart|💛 yellow heart|💚 green heart|💙 blue heart|💜 purple heart|🖤 black heart|🤍 white heart|"
            + "🤎 brown heart|💔 broken heart sad|❣️ heart exclamation|💕 two hearts love|💞 revolving hearts|💓 beating heart|💗 growing heart|"
            + "💖 sparkling heart|💘 cupid heart arrow|💝 heart gift ribbon|💟 heart decoration|❤️‍🔥 heart fire|❤️‍🩹 mending heart|💯 hundred perfect|"
            + "💢 anger|💥 boom collision|💫 dizzy star|💦 sweat drops|💨 dash wind|💬 speech chat|💭 thought|💤 zzz sleep")),
        (ChatCopy.EmojiAnimals, Parse(
            "🐶 dog puppy|🐱 cat kitten|🐭 mouse|🐹 hamster|🐰 rabbit bunny|🦊 fox|🐻 bear|🐼 panda|🐨 koala|🐯 tiger|🦁 lion|🐮 cow|"
            + "🐷 pig|🐸 frog|🐵 monkey|🐔 chicken|🐧 penguin|🐦 bird|🐤 chick|🦆 duck|🦅 eagle|🦉 owl|🐺 wolf|🐴 horse|🦄 unicorn|"
            + "🐝 bee|🦋 butterfly|🐌 snail|🐞 ladybug|🐢 turtle|🐍 snake|🐙 octopus|🐬 dolphin|🐳 whale|🦈 shark|🐠 fish|🦖 dinosaur t-rex|"
            + "🌸 cherry blossom flower|🌹 rose flower|🌻 sunflower|🌷 tulip|🌱 seedling plant|🌲 tree evergreen|🌴 palm tree|🌵 cactus|🍀 clover luck|"
            + "🍁 maple leaf fall|🍂 leaves autumn|🌈 rainbow|☀️ sun sunny|🌙 moon night|⭐ star|🌟 glowing star|⚡ lightning zap|🔥 fire hot lit|"
            + "❄️ snowflake cold|☃️ snowman|🌊 wave ocean|☔ umbrella rain|🌍 earth world globe")),
        (ChatCopy.EmojiFood, Parse(
            "🍎 apple|🍊 orange|🍋 lemon|🍌 banana|🍉 watermelon|🍇 grapes|🍓 strawberry|🍒 cherries|🍑 peach|🥭 mango|🍍 pineapple|"
            + "🥥 coconut|🥑 avocado|🍅 tomato|🌶️ hot pepper spicy|🌽 corn|🥕 carrot|🥦 broccoli|🍞 bread|🧀 cheese|🥚 egg|🍳 cooking egg|"
            + "🥓 bacon|🍔 burger hamburger|🍟 fries|🍕 pizza|🌭 hot dog|🌮 taco|🌯 burrito|🍝 pasta spaghetti|🍜 ramen noodles|🍣 sushi|"
            + "🍱 bento|🍤 shrimp|🍩 donut|🍪 cookie|🎂 birthday cake|🍰 cake|🧁 cupcake|🍫 chocolate|🍬 candy|🍭 lollipop|🍿 popcorn|"
            + "☕ coffee|🍵 tea|🧋 bubble tea boba|🥤 drink soda|🍺 beer|🍻 beers cheers|🥂 cheers champagne|🍷 wine|🍸 cocktail|🍹 tropical drink")),
        (ChatCopy.EmojiActivities, Parse(
            "⚽ soccer football|🏀 basketball|🏈 football|⚾ baseball|🎾 tennis|🏐 volleyball|🎱 pool billiards|🏓 ping pong|🏸 badminton|"
            + "🥊 boxing|⛳ golf|🎣 fishing|🏊 swim|🚴 bike cycling|🏋️ weights gym|🧘 yoga|🏆 trophy win|🥇 gold medal first|🏅 medal|"
            + "🎉 party tada celebrate|🎊 confetti|🎈 balloon|🎁 gift present|🎄 christmas tree|🎃 pumpkin halloween|🎆 fireworks|✨ sparkles|"
            + "🎮 video game|🎲 dice game|🧩 puzzle|🎯 target bullseye|🎨 art paint|🎬 movie film|🎤 microphone sing karaoke|🎧 headphones music|"
            + "🎵 music note|🎶 music notes|🎸 guitar|🎹 piano|🥁 drum|🎻 violin")),
        (ChatCopy.EmojiTravel, Parse(
            "🚗 car|🚕 taxi|🚌 bus|🚓 police car|🚑 ambulance|🚒 fire truck|🚚 truck|🏎️ race car|🏍️ motorcycle|🚲 bicycle bike|🛴 scooter|"
            + "🚂 train|🚆 train|✈️ airplane plane flight|🚀 rocket launch|🛸 ufo|🚁 helicopter|⛵ sailboat|🚢 ship|⚓ anchor|⛽ gas fuel|"
            + "🚦 traffic light|🏠 house home|🏡 home garden|🏢 office building|🏥 hospital|🏫 school|🏰 castle|⛪ church|🗽 statue liberty|"
            + "🗼 tower|🏖️ beach|🏝️ island|⛰️ mountain|🏕️ camping|🌋 volcano|🌃 night city|🌅 sunrise|🌇 sunset|🗺️ map")),
        (ChatCopy.EmojiObjects, Parse(
            "📱 phone mobile|💻 laptop computer|⌨️ keyboard|🖥️ desktop computer|🖨️ printer|📷 camera|📸 camera flash photo|🎥 movie camera|"
            + "📺 tv television|📞 telephone call|⏰ alarm clock|⌛ hourglass time|💡 light bulb idea|🔦 flashlight|🕯️ candle|💰 money bag|"
            + "💵 dollar money|💳 credit card|💎 gem diamond|🔧 wrench tool|🔨 hammer|🛠️ tools|🔑 key|🔒 lock|🔓 unlock|🚪 door|🛏️ bed|"
            + "🛋️ couch|🚿 shower|🛁 bath|🧻 toilet paper|🧼 soap|🛒 shopping cart|🎒 backpack|👓 glasses|🕶️ sunglasses|👕 shirt|👖 jeans|"
            + "👗 dress|👟 sneaker shoe|👠 heel shoe|🧢 cap hat|👑 crown king queen|💍 ring wedding|📚 books|📖 book read|✏️ pencil|"
            + "📝 memo note|📌 pin|📎 paperclip|✂️ scissors|📅 calendar date|📦 package box|✉️ envelope mail|📧 email|🔔 bell|📣 megaphone|"
            + "💊 pill medicine|💉 syringe shot vaccine|🩺 stethoscope doctor")),
        (ChatCopy.EmojiSymbols, Parse(
            "✅ check yes done|☑️ check box|✔️ check mark|❌ cross no wrong|❎ cross box|➕ plus|➖ minus|❓ question|❔ question white|"
            + "❗ exclamation|‼️ double exclamation|⁉️ interrobang|⚠️ warning|🚫 prohibited no|⛔ no entry|🔞 18 adult|♻️ recycle|"
            + "🆗 ok button|🆕 new|🆒 cool|🆓 free|🔴 red circle|🟠 orange circle|🟡 yellow circle|🟢 green circle|🔵 blue circle|🟣 purple circle|"
            + "⚫ black circle|⚪ white circle|🔺 red triangle|🔻 down triangle|➡️ right arrow|⬅️ left arrow|⬆️ up arrow|⬇️ down arrow|🔄 repeat|"
            + "🔁 repeat|💲 dollar sign|©️ copyright|®️ registered|™️ trademark|#️⃣ hash|🔟 ten|☮️ peace|☯️ yin yang|♈ aries|♉ taurus|"
            + "♊ gemini|♋ cancer|♌ leo|♍ virgo|♎ libra|♏ scorpio|♐ sagittarius|♑ capricorn|♒ aquarius|♓ pisces|🏳️‍🌈 rainbow flag pride|"
            + "🏁 checkered flag|🚩 red flag")),
    ];

    /// <summary>
    /// Gets the categories with only the emoji that match what was typed, leaving out categories with none.
    /// </summary>
    /// <remarks>
    /// Every typed word must appear in the emoji's search words, ignoring case, anywhere in them ("eye" finds "eyes"); a
    /// typed plural also matches its singular ("eyes" finds "eye"). Nothing typed returns every category.
    /// </remarks>
    /// <param name="query">What was typed in the search box.</param>
    /// <returns>The matching categories in order, each with its matching emoji in order.</returns>
    public static IReadOnlyList<(string Name, IReadOnlyList<(string Emoji, string Words)> Emoji)> Search(string? query)
    {
        string[] typed = (query ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (typed.Length == 0)
        {
            return Categories;
        }

        var found = new List<(string Name, IReadOnlyList<(string Emoji, string Words)> Emoji)>();
        foreach ((string name, IReadOnlyList<(string Emoji, string Words)> emoji) in Categories)
        {
            List<(string Emoji, string Words)> matches = emoji.Where(e => typed.All(t => Matches(e.Words, t))).ToList();
            if (matches.Count > 0)
            {
                found.Add((name, matches));
            }
        }

        return found;
    }

    // A Typed Word In The Search Words (Ignoring Case), Or Its Singular: "Eyes" Also Finds "Eye", "Peaches" Also "Peach"
    private static bool Matches(string words, string typed)
    {
        if (words.Contains(typed, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (typed.Length > 3 && typed.EndsWith("es", StringComparison.OrdinalIgnoreCase) && words.Contains(typed[..^2], StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return typed.Length > 2 && typed.EndsWith("s", StringComparison.OrdinalIgnoreCase) && words.Contains(typed[..^1], StringComparison.OrdinalIgnoreCase);
    }

    // One Category's String: "Emoji Words|Emoji Words|..."
    private static List<(string Emoji, string Words)> Parse(string entries)
    {
        var list = new List<(string Emoji, string Words)>();
        foreach (string entry in entries.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            int space = entry.IndexOf(' ', StringComparison.Ordinal);
            list.Add(space < 0 ? (entry, string.Empty) : (entry[..space], entry[(space + 1)..]));
        }

        return list;
    }
}
