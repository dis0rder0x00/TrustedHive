namespace TrustedHive;

/// <summary>
/// Configurable strings and paths. Edit these to customize the build.
/// </summary>
public static class AppConfig
{
    // --- Fonts (embedded resources - change the TTF files in Resources/Fonts/) ---
    public const string PixelFontFamily = "./Resources/Fonts/#Press Start 2P";
    public const string TerminalFontFamily = "./Resources/Fonts/#VT323";
    public const string UiFontFamily = "Tahoma";
    public const string MonoFontFamily = "Consolas";
    public const string DisplayFontFamily = "./Resources/Fonts/#dis0rder_goth";

    // --- Header text ---
    public const string Wordmark = "TrustedHive";
    public const string WordmarkDot = ".";
    public const string WordmarkSuffix = "PoC";
    public const string Subtitle = "PWND BY DIS0RDER0x00";
    public const string GreetzLine = "~ GREETZ TO ALL THE FRIENDLY HACKERS ~";

    // --- Title bar ---
    public const string TitleText = "TrustedHive.PoC v1.0 :: unregistered UI release";

    // --- Scroller text ---
    public const string ScrollerText = "*** TRUSTEDHIVE.POC BY dis0rder0x00 *** GREETZ FLYING OUT TO EVERYONE SHARING KNOWLEDGE AND MOTIVATION *** ";

    // --- Contact links ---
    public static readonly (string Prefix, string Label, string Url)[] ContactLinks =
    [
        ("x", "@dis0rder_0x00", "https://x.com/dis0rder_0x00"),
        ("www", "x90x90.dev", "https://x90x90.dev"),
        ("git", "dis0rder0x00", "https://github.com/dis0rder0x00"),
    ];

    // --- Help / About modal text ---
    public const string AboutTitle = "TrustedHive.PoC";
    public const string WhatItDoes =
            "TrustedHive.PoC exploits a local privilege escalation in the Windows " +
            "Update servicing stack (cbscore.dll) on Windows 11. The CBS session COM object " +
            "(TrustedInstaller.exe, SYSTEM) exposes CreateExternalTransformerExecutor to Interactive " +
            "Users without calling EnsureCallerIsAdministrator, giving a medium-IL process a SYSTEM " +
            "file-write primitive.\n\n" +
            "The PoC builds a registry hive with a permissive DACL, sets up an NTFS junction and " +
            "object-manager symlinks to redirect the hive's log-file creation, then triggers the " +
            "ungated COM method. TrustedInstaller follows the redirected path and creates the log " +
            "file, which is owned by SYSTEM at the attacker-chosen location. Because the file inherits " +
            "the hive's permissive DACL, the medium-IL process can then overwrite its contents with " +
            "an arbitrary payload (the input file).\n\n" +
            "Patched in the September 2026 servicing stack update (likely CVE-2026-81963).";

    // --- Greetz ---
    public static readonly (string Name, string Reason)[] Greetz =
    [
        ("r-tec", "love to the whole offensive services team <3"),
        ("@Nico_n_art", "for the pixelart gothic font used in this UI (you can find the .ttf on my GitHub profile)"),
        ("Space_Cat", "for publishing a royalty free chiptune banger (the background music)"),
        ("Hoodinformatik", "for filling my doomscrolling on instagram with great content")
    ];
    public const string GreetzClosing = "...and to everyone sharing knowledge and motivation <3";

    // --- Special filenames ---
    public static readonly (string Pattern, bool ExactWithExt, string Message)[] MagicFilenames =
    [
        ("hello", true, "hello :)"),
        ("peng", false, "Hi Fabian :)"),
        ("bumm", false, "Hi Fabian :)"),
        ("blub", false, "Hi Philipp :)"),
        ("bla", false, "Hi Philipp :)"),
    ];

    // --- Avatar (embedded resource path) ---
    public const string AvatarResourcePath = "Resources/avatar.jpg";
}
