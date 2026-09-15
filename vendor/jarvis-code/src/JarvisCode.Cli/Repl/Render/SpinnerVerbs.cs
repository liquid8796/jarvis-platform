namespace JarvisCode.Cli.Repl.Render;

/// <summary>
/// The reference's spinner vocabulary (CLI 2.1.257, its <c>h</c> array read out
/// of the binary): the 186 present participles the status row picks from while
/// a turn runs, plus the reference's two settings-driven modes — a configured
/// list either <em>replaces</em> this one or is appended to it.
/// </summary>
internal static class SpinnerVerbs
{
    /// <summary>The reference's own list, in its order.</summary>
    public static readonly IReadOnlyList<string> Defaults =
    [
        "Accomplishing", "Actioning", "Actualizing", "Architecting", "Baking", "Beaming", "Beboppin'", "Befuddling",
        "Billowing", "Blanching", "Bloviating", "Boogieing", "Boondoggling", "Booping", "Bootstrapping", "Brewing",
        "Bunning", "Burrowing", "Calculating", "Canoodling", "Caramelizing", "Cascading", "Catapulting",
        "Cerebrating", "Channeling", "Channelling", "Choreographing", "Churning", "Clauding", "Coalescing",
        "Cogitating", "Combobulating", "Composing", "Computing", "Concocting", "Considering", "Contemplating",
        "Cooking", "Crafting", "Creating", "Crunching", "Crystallizing", "Cultivating", "Deciphering",
        "Deliberating", "Determining", "Dilly-dallying", "Discombobulating", "Doing", "Doodling", "Drizzling",
        "Ebbing", "Effecting", "Elucidating", "Embellishing", "Enchanting", "Envisioning", "Fermenting",
        "Fiddle-faddling", "Finagling", "Flambéing", "Flibbertigibbeting", "Flowing", "Flummoxing", "Fluttering",
        "Forging", "Forming", "Frolicking", "Frosting", "Gallivanting", "Galloping", "Garnishing", "Generating",
        "Gesticulating", "Germinating", "Gitifying", "Grooving", "Gusting", "Harmonizing", "Hashing", "Hatching",
        "Herding", "Honking", "Hullaballooing", "Hyperspacing", "Ideating", "Imagining", "Improvising",
        "Incubating", "Inferring", "Infusing", "Ionizing", "Jitterbugging", "Julienning", "Kneading", "Leavening",
        "Levitating", "Lollygagging", "Manifesting", "Marinating", "Meandering", "Metamorphosing", "Misting",
        "Moonwalking", "Moseying", "Mulling", "Mustering", "Musing", "Nebulizing", "Nesting", "Newspapering",
        "Noodling", "Nucleating", "Orbiting", "Orchestrating", "Osmosing", "Perambulating", "Percolating",
        "Perusing", "Philosophising", "Photosynthesizing", "Pollinating", "Pondering", "Pontificating", "Pouncing",
        "Precipitating", "Prestidigitating", "Processing", "Proofing", "Propagating", "Puttering", "Puzzling",
        "Quantumizing", "Razzle-dazzling", "Razzmatazzing", "Recombobulating", "Reticulating", "Roosting",
        "Ruminating", "Sautéing", "Scampering", "Schlepping", "Scurrying", "Seasoning", "Shenaniganing",
        "Shimmying", "Simmering", "Skedaddling", "Sketching", "Slithering", "Smooshing", "Sock-hopping",
        "Spelunking", "Spinning", "Sprouting", "Stewing", "Sublimating", "Swirling", "Swooping", "Symbioting",
        "Synthesizing", "Tempering", "Thinking", "Thundering", "Tinkering", "Tomfoolering", "Topsy-turvying",
        "Transfiguring", "Transmuting", "Twisting", "Undulating", "Unfurling", "Unravelling", "Vibing", "Waddling",
        "Wandering", "Warping", "Whatchamacalliting", "Whirlpooling", "Whirring", "Whisking", "Wibbling", "Working",
        "Wrangling", "Zesting", "Zigzagging",
    ];

    /// <summary>The reference's default when no verb has been picked yet.</summary>
    public const string Fallback = "Working";

    /// <summary>
    /// The verbs in force: the reference's <c>replace</c> mode swaps its list in
    /// whole, anything else appends the user's to the built-in ones.
    /// </summary>
    public static IReadOnlyList<string> Resolve(IReadOnlyList<string>? custom, bool replace)
    {
        if (custom is not { Count: > 0 })
        {
            return Defaults;
        }

        return replace ? custom : [.. Defaults, .. custom];
    }

    /// <summary>One verb for a turn, chosen without a shared random source so a test can pin it.</summary>
    public static string Pick(IReadOnlyList<string> verbs, int seed) =>
        verbs.Count == 0 ? Fallback : verbs[Math.Abs(seed) % verbs.Count];
}
