namespace JarvisCode.App.Services;

/// <summary>What the empty chat screen says.</summary>
/// <param name="Greeting">The one-line greeting, when the ordinary screen is shown.</param>
/// <param name="OnboardingGreeting">The first-chat greeting, when that one is.</param>
/// <param name="OnboardingIntro">Its second paragraph.</param>
/// <param name="OnboardingStart">Its third.</param>
public readonly record struct ChatWelcomeView(
    string? Greeting,
    string? OnboardingGreeting,
    string? OnboardingIntro,
    string? OnboardingStart)
{
    public bool IsOnboarding => OnboardingGreeting is not null;
}

/// <summary>
/// The empty chat screen's copy, measured from the reference desktop's chat page
/// (<c>ca2ef848d-D8BWZk64.js</c>): its <c>$y</c> renders a centred figure with the
/// mascot and either a supplied greeting or "What can I help you with today?", and
/// no suggestion chips at all; its <c>Xy</c>/<c>Jy</c> render the first-chat
/// onboarding — a greeting that names the user, an intro paragraph and a line
/// introducing what comes next.
/// </summary>
public static class ChatWelcome
{
    /// <summary>The ordinary empty-chat greeting.</summary>
    public const string Greeting = "What can I help you with today?";              // 1PnN/uP0zv

    /// <summary>The onboarding greeting when the user's name is not known.</summary>
    public const string OnboardingGreetingAnonymous = "Welcome! I’m Jarvis.";      // G1lCCCnQ7Z

    // One literal, however long: the manifest check looks for the sentence in the
    // file, and a concatenation would hide it.
    public const string OnboardingIntro = "Bring me anything—a tough problem, a half-formed idea, something you need to write. We’ll figure it out together."; // xUS/qYZOWh

    public const string OnboardingStart = "Where do you want to start?";           // Ej5f9tjtu2

    /// <summary>The onboarding greeting for a known name.</summary>
    public static string OnboardingGreetingFor(string name) =>
        $"Welcome, {name}! I’m Jarvis.";                                            // ex7Clidxsc

    /// <summary>The greeting an incognito chat opens with, in place of the ordinary one.</summary>
    public const string Incognito = "You’re incognito";                            // MQrAwjjefp

    /// <summary>What an incognito chat promises, and this build can keep.</summary>
    public const string IncognitoNotice = "Incognito chats aren’t saved to history."; // 8EiWE4PL22

    /// <summary>
    /// What to show. The reference gates its onboarding on a rollout variant
    /// ("own_topic") that this build has no way to read, so the first chat of all —
    /// which is what that experience is called and when it is meant to appear — is
    /// what stands in for it here.
    /// </summary>
    public static ChatWelcomeView Describe(bool firstChatEver, string? userName, bool incognito = false) =>
        incognito
            ? new ChatWelcomeView(Incognito, null, null, null)
            : firstChatEver
                ? new ChatWelcomeView(
                    null,
                    string.IsNullOrWhiteSpace(userName)
                        ? OnboardingGreetingAnonymous
                        : OnboardingGreetingFor(userName.Trim()),
                    OnboardingIntro,
                    OnboardingStart)
                : new ChatWelcomeView(Greeting, null, null, null);

    /// <summary>
    /// The mascot's type size on the ordinary empty screen. The reference's
    /// <c>h2</c> carries <c>font-title</c>, which its stylesheet resolves to the UI
    /// serif at 1.75rem, weight 500 (<c>"wght" 460</c>) and <c>line-height:1.3</c>.
    /// </summary>
    public const double GreetingFontSize = 28;

    /// <summary>Its <c>line-height:1.3</c>, in pixels at <see cref="GreetingFontSize"/>.</summary>
    public const double GreetingLineHeight = GreetingFontSize * 1.3;

    /// <summary>Its <c>gap-2</c> between the mascot and the greeting.</summary>
    public const double GreetingGap = 8;

    /// <summary>
    /// Its <c>sm:</c> breakpoint: below this the <c>h2</c> is a column with the mascot
    /// above the greeting, and at or above it they sit in a row.
    /// </summary>
    public const double GreetingRowBreakpoint = 640;

    /// <summary>
    /// What the mascot says when it is poked, ported from the reference's own
    /// <c>q_</c> (<c>ca2ef848d-C_oPm_EH.js</c>, desktop 1.44121.2.0). These are
    /// hardcoded in the reference rather than catalogued, so they carry no message
    /// id; the last one names the assistant and is rebranded like every other line a
    /// user reads. Note the ladder has no arm above 31, so the thirty-second poke
    /// falls back to the greeting — that is the reference's behaviour, not a gap here.
    /// </summary>
    public static string PokeReply(int pokes) => pokes switch
    {
        > 24 and < 32 => "Ugh, well you can’t do that forever",
        > 18 and <= 24 => "Alright, alright, you have my attention!",
        > 12 and <= 18 => "Are you still doing that?",
        > 5 and <= 12 => "Yes, yes. What can I do for you?",
        _ => "Hi, I’m Jarvis. How can I help you today?",
    };

    /// <summary>
    /// Which state the onboarding mascot is in: the reference's
    /// <c>S = y ? "idle" : "writing"</c>, where <c>y</c> is set once the last
    /// paragraph has finished revealing.
    /// </summary>
    public static SparkState OnboardingMascot(bool revealed) =>
        revealed ? SparkState.Idle : SparkState.Writing;
}
