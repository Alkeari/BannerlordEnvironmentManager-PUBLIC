# Bannerlord Environment Manager

Keep several Bannerlord versions on one machine, each with its own mods and saves, and switch between them from a dropdown. Install and order your mods inside the version you picked, and see which one broke it when the game crashes.

## Availability

- [Nexus Mods](https://www.nexusmods.com/mountandblade2bannerlord/mods/11049)

## AI Disclosure

Bannerlord Environment Manager is made by Alkeari.

AI is solely used for the following items:

- Minimal assistance with code: bug fixes and updates
- Restructuring code for organization and clarity
- README
- Changelog
- Translations

## What It Does

Mods are built for a particular Bannerlord version, so the first thing BEM asks is which version you mean. It keeps as many versions as you want side by side, each a separate install with its own game files, mods, saves, configs, logs and load order. Everything else happens inside the one you picked.

- **Versions** - Download Steam versions with your own account, with or without War Sails, or adopt an existing install. Rename, copy or remove a version while keeping its saves and settings.
- **Shared Settings** - off until you turn it on. When on, the game's Video, Performance, Audio and Gameplay settings and your keybindings follow you from version to version. Mod settings, load orders and saves never do.
- **Play** - Sort and preflight your load order, pin modules, group them into sections and exchange orders with other tools. Launch with BLSE, the game launcher or Steam where supported. Record Mod Patches logs patches.
- **Library** - Install Nexus downloads, collections and the BUTR stack. BEM scans archives before extraction, installs one archive into several versions, undoes installs and keeps Workshop mods copied in Modules.
- **Troubleshoot** has five tabs:
  - Problems finds missing game methods, version mismatches, duplicate modules, bad XML, conflicting data and unsafe files, and keeps the risks you accepted.
  - Crashes joins crash reports, logs, dumps and watched sessions into ranked suspects with the reasoning stated.
  - Find the Culprit test-boots the load order and bisects your mods, automatically or by hand, to find the one causing a crash.
  - Mod Settings archives, restores and cleans up each mod's settings folders; every mod row also has its own Settings button.
  - Tools lists the optional programs BEM can use, such as 7-Zip, and what each adds.
- **Saves** - each version keeps its own, shows what changed since each save was written, and can launch a save with its original load order.
- **Settings** - Manage Nexus sign-in, download links and archive options, and add this game folder to Vortex, Mod Organizer 2 or Novus Launcher.
  - Files you downloaded are unblocked automatically before every launch.
  - BEM's update check asks the public Nexus API for its published version. Crash sharing with BUTR is opt-in.

The Bannerlord Exception Manager is on by default for every game BEM starts. When the game crashes it holds the game and shows the error, the suspect mods, the Harmony patches on the stack, the load order and the game log, and lets you continue, save and close, or close the game.

Advanced Mode shows everything BEM has, including the Versions and Troubleshoot destinations when they would otherwise be hidden, and the repair and restore tools. With it off nothing stops running.

## Requirements

- Windows 10 build 17763, the October 2018 Update, or later. 64-bit only.
- Mount & Blade II: Bannerlord from Steam, GOG, Epic Games or Xbox Game Pass PC.
- A Steam account that owns the game, to download versions. BEM downloads DepotDownloader the first time it needs it. Any install can be adopted and managed; only Steam can be downloaded from.
- Disk space for every version you keep, because each one holds its own copy of the game. BEM says what a version costs before it fetches it.
- No separate .NET runtime: BEM is a self-contained, single-file executable.
- BLSE, for the three BLSE launch targets and for launching straight into a save. BEM works without it, launches the game exe directly instead, and tells you what needs it.
- 7-Zip, for every archive that is not a zip: .7z, .rar, .tar, .gz, .tgz, .bz2, .tbz2, .xz, .txz and .zst. Zip needs nothing, and Troubleshoot's Tools tab installs 7-Zip for you.

## Installation

1. Extract the archive anywhere and run the exe. Keep the `Companion` folder beside it; that is what makes watched play sessions and Boot Checks work. There is no installer.
2. Point BEM at your Bannerlord folder on the Settings page, then Adopt Current Install on the Versions page to record it as your first version. The Library and Play pages fill in.

Windows SmartScreen may warn about an unknown publisher the first time, because BEM is not code-signed: click "More info", then "Run anyway".

Once BEM is running it can register itself as the nxm:// handler, so Mod Manager Download on other mods' Nexus pages routes straight to it.

## Configuration

Most of it is on the Settings page, and BEM's own settings, logs, backups, profiles and quarantines live under `%LOCALAPPDATA%\Bannerlord Environment Manager`. Downloaded versions live under the Games Root, set on the Versions page.

| Group | What you can change |
|---|---|
| The options at the top of Settings | replace or merge module folders, delete archives after install, move kept archives to INSTALLED, search the archives folder recursively, install only the rows you ticked |
| Folders | the Bannerlord install folder and the archives folder |
| How Much to Show | Advanced Mode, module row density and the Language picker |
| Other Mod Managers | add this game folder to Mod Organizer 2 or Novus Launcher, or see how to add it in Vortex |
| Load Order | whether manual load-order overrides are permitted, and whether sections follow their modules on Auto-Sort |
| Shared Game Settings | whether every version shares one set of base game settings, off until you turn it on |
| Launch | launch target, extra launch arguments, whether BEM minimizes when the game starts, whether the game's physics frame rate setting is put back before launch if the game reset it, and whether the launch check prefers the installed BUTR stack over a mod's bundled copies |
| Bannerlord Exception Manager | on or off, on the Settings page under Launch, on by default; off, the game crashes as usual |
| BLSE Crash Handling (Advanced Mode) | keep the vanilla crash handler, and stop BLSE catching auto-generated exceptions |
| Tools (on Troubleshoot) | whether BEM may install 7-Zip for you when a mod needs it |
| Network Features | Nexus sign-in, update checking and how far back it looks, BEM's own update check and whether it runs at startup, nxm:// handling, BUTR compatibility scores, and crash contribution |

The Tools tab on Troubleshoot lists the programs BEM can use but cannot ship. If your 7-Zip lives outside `Program Files`, set the `7ZIP_EXE` environment variable to that copy's `7z.exe` and restart BEM.

### Language

BEM ships in English, German, Spanish, French, Italian, Japanese, Korean, Polish, Brazilian Portuguese, Russian, Turkish and Simplified Chinese. It picks your Windows language on first run; Settings has a Language picker to change it. Changing the language restarts BEM so nothing is left half translated.

Every translation but English was machine-written; corrections are welcome and need no new build.

To correct a translation or add a language of your own, put a JSON file in a `Languages` folder beside `Bannerlord Environment Manager.exe`. Name the file for the language it corrects, such as `de.json`, and it overrides whatever BEM ships for that language, so a correction only needs the lines you want to change:

```json
{ "Environment.LaunchButtonText.Normal": "Starten" }
```

A file that adds a language BEM does not ship names itself instead, and that name is what the Language picker shows:

```json
{ "$language": { "code": "nl", "native": "Nederlands", "english": "Dutch" } }
```

The header wins over the file name. Restart BEM to see a change.

## Compatibility

- **Your files.** Shader folders, platform binaries, bin backups, cleared logs, archived settings and replaced module folders go to a quarantine or the Recycle Bin with a restore button.
- **Replaced module folders.** Kept only while Keep a copy of the folder being replaced is on, and only up to the count you keep; older ones are deleted permanently.
- **A removed version.** One too large for its drive's Recycle Bin, or on a drive with the Recycle Bin off, is deleted permanently, and BEM says so before you confirm.
- **Between versions.** Installing mods on one version never touches another version's module folders, and restoring a replaced game file puts it back in the version it came from.
- **The version at rest.** One version sits at the game's own folders between launches, so a launch from Steam or a desktop shortcut reaches that one.
- **Playing any other version.** BEM offers only the direct launch targets while you are on one, because a launcher starts the game and exits, leaving nothing to keep that version's saves and settings apart for the run.
- **Downloading versions.** Versions are fetched from Steam with your own account and entitlements. A GOG, Epic or Game Pass install can be adopted and managed, but not downloaded from.
- **Architecture.** The download is 64-bit only. There is no 32-bit or ARM64 release.
- **Game installs.** Steam and Epic installs are found wherever they are. A Game Pass one is found on any drive under XboxGames, WindowsApps or ModifiableWindowsApps, and a GOG one only in GOG's default folders.
- **Anything elsewhere.** Point BEM at the folder by hand on the Settings page; it reads the platform out of the install rather than guessing from the path.
- **Game Pass.** Supported: the platform is read from the install itself, and only the binaries that install can load are touched.
- **Official modules.** The game's own modules, DLC included, sort by their SubModule.xml module type. A DLC that does not declare itself official sorts as an ordinary mod.
- A ModdingTools .mtorder load order can be imported but not exported.

## Support

- Include the version from the Settings page and the log from the folder Settings opens for you, or use the bug report the Crashes tab of Troubleshoot drafts.

## For Mod Authors

The Files tab carries a second, optional download: the Developer Build. It is the same BEM, with everything the main file has, plus a set of tools aimed at people who write mods rather than play them. Its title bar says Developer Build, and it runs alongside the normal one.

Take the main file unless you build mods. Three of these can cost you, or somebody else, real work:

- **Keep a full crash dump for the game**, on Troubleshoot, writes Windows' own crash-dump registry keys, machine-wide, and needs administrator rights: it changes your machine outside BEM.
- **Show the Faulting Method** and **Decompile the Selected Frame** read another author's shipped assembly back into C#. Nothing is loaded or run to do it, and nothing is decompiled until you press the button.
- **Update All Versions** and **Update This Module's Versions** rewrite, in bulk, the dependency versions another author declared in their own SubModule.xml. Each file is backed up first.

The registry script is shown to you in full before it runs, and Put the Dump Settings Back restores every key it touched. Rewriting a version somebody else declared is a judgment about what their manifest should say rather than a repair, which is why it is not in the main file.

The rest are quieter:

- **Purpose** on a version row, For Testing, For Playing or Not Declared, so a mod's build tool can tell a test install from the one you play in.
- **Open SubModule.xml** on a module row, to read a manifest raw, and **Mark as built on this machine** one module at a time, which every build can already do to a selection of two or more.
- **Read Deleted Archive Names**, **Ask About Unrecognized Files Again**, **Forget This Mod ID** and a by-hand refresh of the turned-down list, on Library.
- **Keep the BLSE crash handler on when a debugger is attached**, in Settings, for when you attach a debugger to the game.
- **dotPeek** on the Tools tab, and an Open the Assembly in dotPeek button on a crash frame when you have it installed.

## License

License terms are in the [Alkeari License Agreement](https://alkeari.com/license/).
