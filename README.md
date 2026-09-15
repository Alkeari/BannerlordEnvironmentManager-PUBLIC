# Bannerlord Environment Manager

Keep several Bannerlord versions on one machine, each with its own mods and saves, and switch between them from a dropdown. Install and order your mods inside the version you picked, and see which one broke it when the game crashes.

> **Windows 10 or later, 64-bit. Finds Steam, GOG, Epic and Game Pass installs. No .NET runtime to install.**

---

## Availability

- [Nexus Mods](https://www.nexusmods.com/mountandblade2bannerlord/mods/11049)

## What It Does

Mods are built for a particular Bannerlord version, so the first thing BEM asks is which version you mean. It keeps as many versions as you want side by side, each a separate install, and everything else here happens inside the one you picked.

### Versions: as many Bannerlord versions as you want

- **A version is a whole install of its own** - its own game files, mods, saves, configs, logs and load order, so a heavily modded 1.4.7 and a clean 1.5.2 sit on the same machine without touching each other.
- **Download a version from Steam** - BEM fetches it with DepotDownloader, signed in to your own Steam account, and says what it costs to download and on disk before it starts. BEM hosts no game files.
- **With a DLC or without** - owning War Sails, "v1.5.2 + War Sails" is a version in its own right and coexists with plain v1.5.2 instead of replacing it. War Sails is the DLC BEM knows today.
- **Add War Sails to a version you already have** - it downloads into that version's existing game folder, without fetching the base game again.
- **Adopt the install you already have** - the Bannerlord already on this machine becomes your first version where it stands, and a copy of its saves and settings as they were is kept in a folder you can restore from.
- **Stop a download, and pick it up later** - stopping keeps what already arrived, and unfinished downloads are listed with what they cost on disk, to carry on from or send to the Recycle Bin.
- **Remove a version** - its game files go to the Recycle Bin and its saves and settings stay, unless you tick the box that takes them too, which says how many items and how much disk before you confirm.
- **Name your versions** - a version you name wears that name in its folder and in the Play dropdown, so two installs of one version are told apart.
- **The row menu carries the rest** - Rename, Open Folder, Copy Path, Set as Active, Set as Resting, Remove and Show Details, acting on the row you right-clicked.
- **Keep two of the same version** - install a second copy of a version you already have and name it as it downloads, without disturbing the copy you had.
- **It notices when Steam moves underneath you** - an adopted install that Steam has since updated or re-branched is flagged, along with saves written by a version other than the one running.
- **The resting version survives a Steam update** - its folder is named Resting Instance, and at startup BEM re-reads the version from the game's own files so the record follows Steam.
- **Leaving is as documented as arriving** - Prepare for Uninstall lists everything BEM would leave on this machine, with sizes, and refuses to call it safe while a version is still switched in.

### Shared Settings: one set of game settings across your versions, if you want it

- **Off until you turn it on** - off, every version uses its own settings file, which is how BEM has always worked. The switch is Share base game settings across instances, on the Settings page.
- **What it covers** - the game's own Video, Performance, Audio and Gameplay settings and your keybindings: `engine_config.txt`, `BannerlordConfig.txt` and `BannerlordGameKeys.xml`, and nothing else.
- **What it never covers** - mod settings, load orders and saves. Those belong to the version they came from and stay there.
- **Applied as a version launches, folded back after the run** - a graphics tweak or a rebound key made in one version is there in the next one you play.
- **A newer game version's settings never reach an older one** - BEM only writes keys the version's own file already has, so a setting 1.5.2 introduced is never pushed onto 1.4.7.
- **A mod's keybinding category is never created where that mod is absent** - the hotkey categories your overhaul owns stay in the version that has the overhaul.
- **A version's first launch never rewrites your shared settings** - whatever a newly downloaded version writes into its settings on that first run stays in that version, and its next launch gets your shared settings.
- **A version that has never been launched still gets them** - a freshly downloaded version starts on your settings rather than the game's defaults.
- **Turning it off puts everything back at once** - every version that had its own settings gets those files back, and one that never had any returns to the game's defaults.
- **Never while the game is running** - the change is refused with a message and the box goes back, because those files are in use.

### Play: picks the version, orders the load order and launches it

- **The version dropdown decides everything below it** - the load order, the checks and Launch are all that version's. Only versions already on this machine are listed, so switching can never start a download.
- **Auto-Sort follows the rules the mods declare** - a topological pass over load-before, load-after and dependency rules, tie-broken by module id.
- **With a sane tier order underneath** - crash handlers, then the named infrastructure stack, then the game's own official modules, then libraries other mods depend on, then ordinary mods, then trailing z-prefixed patches.
- **It refuses to hand you a broken order** - the result is re-checked against every declared rule, and if anything is violated your order is left exactly as it was and BEM says so.
- **Pin what you want kept** - a pin a declared rule cannot honor is reported with the rule that beat it, and a setting lets you order everything by hand instead.
- **Sections to fold the list down** - insert a named section above or below any module, rename it, collapse it with a count of what is hidden. Sections never reach the file the game reads.
- **Select modules the way Explorer does** - Ctrl+click, Shift+click, Ctrl+A, and a click on empty space to clear. One right-click then acts on the whole selection through thirteen batch actions.
- **A batch action promises what it will touch** - each one names the count it is about to act on, and afterwards names every module it had to skip and why.
- **The menu carries what you came to do** - Enable or Disable, Uninstall, Move to Top, Move to Bottom, Open Mod Page and Pin, with the maintenance items behind Shift+right-click.
- **Steam Workshop mods are Steam's** - a subscribed module is described as a subscription with no Nexus options, and uninstalling one offers its Workshop page in your web browser so you can unsubscribe.
- **An unsubscribed mod's leftovers clear normally** - what a Workshop item leaves behind after you unsubscribe is cleared like any other leftovers.
- **Import and export what you already have** - in from .bmlist, Novus .xml and ModdingTools .mtorder, out to .bmlist, Novus .xml or plain text, with named profiles, dated backups and a one-step undo.
- **Hand a load order to another player** - export a profile to a file that records the version it was built for; importing one saves it beside your own and lists the mods you do not have yet.
- **Mark an order known-good and come back to it** - one press puts the last order you vouched for back in place.
- **Launch it your way** - BLSE standalone, launcher or launcher-ex, the game exe, the TaleWorlds launcher, or Steam, with your own arguments; the BLSE crash-handler flags apply to the standalone target.
- **A preflight before it starts** - eleven checks run first and anything worth knowing is named. It never blocks you: fix it, launch anyway, or cancel.
- **A finding you have judged stays judged** - accept one and it stops gating your launches while still showing in the report. Critical findings can never be accepted away.

### Library: installs and updates mods, into the version you picked

- **Mod Manager Download works** - click it on any Bannerlord mod page and the file lands in the folder BEM watches, ready to install with one more press, once BEM has registered itself as the handler for nxm:// links.
- **Install the BUTR Stack in one press** - Harmony, ButterLib, UIExtenderEx, MCM and BLSE downloaded and installed into every version you tick in Install Into, whether or not your Nexus account is Premium.
- **One archive into several versions at once** - tick them in Install Into, and Results by Instance says what installed, what had nothing to do and what failed in each.
- **A browser link names the version it is for** - your browser does not know which version BEM is on, so a link from it says which install the mod would land in and asks before downloading.
- **Archives read by their header, not their name** - zip, RAR, 7z, gz, bz2, xz and zst are recognized from their own bytes, so a mod saved under the wrong extension still opens.
- **Zip natively, the rest through 7-Zip** - .7z, .rar, .tar, .gz, .bz2, .xz and .zst, and the .tgz, .tbz2 and .txz spellings of them.
- **A whole downloads folder at once** - point BEM at it, optionally searched recursively, and every archive in it is listed for install.
- **Checked before anything is extracted** - with the safety check on, every archive is tested against the trojanized-mod fingerprint, and a flagged one stops with a choice: install anyway, skip the flagged archives, or stop.
- **It works out where a mod came from** - by manifest, filename, archive hash and BUTR's index, and you confirm or reject each match.
- **Updates through the same pipeline** - signed in to Nexus, BEM checks installed mods against their pages and can plan, download and install what is available.
- **An install can be undone** - replaced module folders, overwritten game files and bin backups are kept and put back on request, rather than lost to the mod that landed on them.
- **Stray shader and platform files are quarantined, not deleted** - loose folders and binaries left in the game's own folders are scanned, set aside and restorable.

### Health: checks whether this version's setup looks sound

- **Install Checks** - rig conflicts, mods built for another game version, shadowed assemblies, Harmony patches aimed at methods your install does not have, and your display adapters.
- **And the mistakes a folder makes** - copies of the game's own assemblies inside a mod, two folders declaring the same module id with both paths named in full, the same module installed twice, and where Native sorts.
- **Mod Overlaps** - replays the game's own XML merge rules to show which mod wins where two mods set the same game data, and names the datasets that merge cannot process at all.
- **Mod Safety** - scans installed mods, Workshop subscriptions and downloaded archives for the trojanized-mod fingerprint, using the blocklist published by Rely1234's Calradia Warden.
- **And what each mod can reach** - network, process, shell, dynamic code and registry use, with each file's signer, reported as context rather than a verdict.
- **Boot Checks** - test-launches the load order with the companion module aboard and reports whether it boots and what threw if it did not.
- **SubModule.xml Validator** - widens overly strict dependency wildcards across installed mods, restores elements a bad edit dropped, and restores its own backups.
- **Findings you have judged stay out of the way** - accept one and it stops asking, for the version you accepted it on.

### Forensics: answers "which mod did it"

- **Every source of evidence, ranked** - crash reports from the game, ButterLib and Crash Doctor, the game's own crash folders, Windows crash dumps, the event log, and BEM's own copies taken before the game deletes them.
- **Turned into suspects, with the reasoning stated** - the sources are joined against each other and against what the watched runs saw patched, and what was ruled out is listed too.
- **Every log in one place** - the game's, ButterLib's, Crash Doctor's, each mod's own, and BEM's own captures, gathered and read without hunting for folders.
- **Clearing is reversible too** - logs, crash reports and captures go into dated batches you can restore from, whole or file by file, rather than being deleted.
- **Watched play sessions** - every module loading and patching, with a heartbeat, so a hang can be told apart from a session that simply has not ended.
- **Bisection, automatic or guided** - turns mods off and on until the one actually causing the crash is found, driving the runs itself or letting you reproduce the crash by hand.
- **A bug report drafted for you** - from the evidence, to copy or save. Contributing a crash report to BUTR is the one thing that leaves your machine, it is off until you turn it on, and it shows you the whole payload first.

### Saves: protects your saves

- **Each version keeps its own saves** - crash evidence, mod settings, profiles and backups belong to the version they came from as well.
- **What changed since you saved** - each save's own recorded module list, compared against now, telling a mod you uninstalled from one you merely turned off.
- **Play it as it was written** - launch straight into a save with its original load order, or restore that order to the game with one click.
- **Read out of the save itself** - character, level, in-game days and when it was written, with the save's module list ready to copy into a bug report and a row menu that recycles one.

### Settings: keys, folders, and the tools that touch your machine

- **Your sign-in and links** - sign in with Nexus, and switch update checking, nxm:// handling and BUTR compatibility scores on or off one at a time. Update checking needs you signed in before it can look at anything.
- **Told when a newer BEM is out** - once a day as it starts, BEM asks its own Nexus page, with no key needed, and shows one bar you can open, skip for that version, or close until the next day. Switch it off in Settings.
- **One thing is off until you ask for it** - contributing a crash report to BUTR, because it is the only setting that sends your data to somebody else.
- **Unblock Files** - strips the "downloaded from the internet" flag from files and folders.
- **Mod Settings** - archives, restores and shares a mod's MCM settings folder, and points out settings left behind by mods you no longer have.
- **A settings bundle you share is read first** - your Nexus key can never travel in one, and anything that looks like a secret is left out unless you approve it by name.
- **Toolkit** - installs 7-Zip through the Windows Package Manager, printing the exact command before it runs, and will only uninstall a tool it installed itself.

Advanced Mode shows everything BEM has: the Forensics destination, sort keys and pins, the mod-id repair tools, the restore panels and the launch tuning, and it is where Unblock Files, Mod Settings and the Toolkit page are reached from. With it off nothing stops running, and anything a check finds is shown either way.

## Requirements

- Windows 10 build 17763, the October 2018 Update, or later. 64-bit only.
- Mount & Blade II: Bannerlord from Steam, GOG, Epic Games or Xbox Game Pass PC.
- A Steam account that owns the game, to download versions. Any install can be adopted and managed; only Steam can be downloaded from.
- Disk space for every version you keep, because each one holds its own copy of the game. BEM says what a version costs before it fetches it.
- No separate .NET runtime: BEM is a self-contained, single-file executable.
- BLSE, for the three BLSE launch targets and for launching straight into a save. BEM works without it, launches the game exe directly instead, and tells you what needs it.
- 7-Zip, for every archive that is not a zip: .7z, .rar, .tar, .gz, .tgz, .bz2, .tbz2, .xz, .txz and .zst. Zip needs nothing, and Settings, Toolkit installs 7-Zip for you.

## Installation

1. Extract the archive anywhere and run the exe. Keep the `Companion` folder beside it; that is what makes watched play sessions and Boot Checks work. There is no installer.
2. Point BEM at your Bannerlord folder on the Settings page, then Adopt Current Install on the Versions page to record it as your first version. The Library and Play pages fill in.

Windows SmartScreen may warn about an unknown publisher the first time, because BEM is not code-signed: click "More info", then "Run anyway".

Once BEM is running it can register itself as the nxm:// handler, so Mod Manager Download on other mods' Nexus pages routes straight to it.

## Configuration

Most of it is on the Settings page, and BEM's own settings, logs, backups, profiles and quarantines live under `%LOCALAPPDATA%\Bannerlord Environment Manager`. Downloaded versions live under the Games Root, which is set on the Versions page and holds each version's game files, saves and settings together.

| Group | What you can change |
|---|---|
| Installing | replace or merge module folders, delete archives after install, search the archives folder recursively, install only the rows you ticked |
| Folders | the Bannerlord install folder and the archives folder |
| Display | Advanced Mode, module row density and the Language picker |
| Tools | Unblock Files, Mod Settings and the Toolkit page, in Advanced Mode |
| Load Order | whether manual load-order overrides are permitted, and whether sections follow their modules on Auto-Sort |
| Shared Game Settings | whether every version shares one set of base game settings, off until you turn it on |
| Diagnostics | opens the folder BEM logs to |
| Launch | launch target, extra launch arguments, and whether BEM minimizes when the game starts |
| Crash Handling | keep the vanilla crash handler, and stop BLSE catching auto-generated exceptions |
| Toolkit | whether BEM may install 7-Zip for you when a mod needs it |
| Network Features | Nexus sign-in, update checking and how often, BEM's own update check and whether it runs at startup, nxm:// handling, BUTR compatibility scores, and crash contribution |

How many replaced module folders to keep is not here: it is on the Library page, beside the option that keeps them.

Toolkit lists the programs BEM can use but cannot ship and says what each one adds. "Install 7-Zip for me when a mod needs it" is off by default; turned on, BEM runs that same command the first time an archive 7-Zip has to open turns up, and carries on with the install.

If your 7-Zip lives outside `Program Files`, point the `7ZIP_EXE` environment variable at that copy's `7z.exe`, the executable itself and not its folder, then restart BEM. That variable is read only at startup.

### Language

BEM ships in English, German, Spanish, French, Italian, Japanese, Korean, Polish, Brazilian Portuguese, Russian, Turkish and Simplified Chinese. It picks your Windows language on first run; Settings has a Language picker to change it. Changing the language restarts BEM so nothing is left half translated.

Every translation but English was machine-written, so expect rough edges. Corrections are welcome and do not need a new build.

To correct a translation or add a language of your own, put a JSON file in a `Languages` folder beside `Bannerlord Environment Manager.exe`. Name the file for the language it corrects, such as `de.json`, and it overrides whatever BEM ships for that language, so a correction only needs the lines you want to change:

```json
{ "Play.LaunchButton": "Starten" }
```

A file that adds a language BEM does not ship names itself instead, and that name is what the Language picker shows:

```json
{ "$language": { "code": "nl", "native": "Nederlands", "english": "Dutch" } }
```

The header wins over the file name whenever both are there. BEM reads the folder at startup, so restart it to see a change.

## Compatibility

- **Your files.** Everything BEM removes on your behalf goes to the Recycle Bin or a quarantine with a restore button: replaced module folders, shader folders, platform binaries, bin backups, cleared logs, archived settings.
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

- Include the version from the Settings page and the log from the folder Settings opens for you, or use the bug report the Forensics page drafts.

## For Mod Authors

The Files tab carries a second, optional download: the Developer Build. It is the same BEM, with everything the main file has, plus a set of tools aimed at people who write mods rather than play them. Its title bar says Developer Build, and it runs alongside the normal one.

Take the main file unless you build mods. Three of these can cost you, or somebody else, real work:

- **Keep a full crash dump for the game**, on Forensics, writes Windows' own crash-dump registry keys, machine-wide, and needs administrator rights: it changes your machine outside BEM.
- **Show the Faulting Method** and **Decompile the Selected Frame** read another author's shipped assembly back into C#. Nothing is loaded or run to do it, and nothing is decompiled until you press the button.
- **Update All Versions** and **Update This Module's Versions** rewrite, in bulk, the dependency versions another author declared in their own SubModule.xml. Each file is backed up first.

The registry script is shown to you in full before it runs, and Put the Dump Settings Back restores every key it touched. Rewriting a version somebody else declared is a judgment about what their manifest should say rather than a repair, which is why it is not in the main file.

The rest are quieter:

- **Purpose** on a version row, For Testing, For Playing or Not Declared, so a mod's build tool can tell a test install from the one you play in.
- **Open SubModule.xml** on a module row, to read a manifest raw, and **Mark as Built on This Machine** one module at a time, which every build can already do to a selection of two or more.
- **Read Deleted Archive Names**, **Ask About Unrecognized Files Again**, **Forget This Mod ID** and a by-hand refresh of the turned-down list, on Library.
- **Keep the BLSE crash handler on when a debugger is attached**, in Settings, for when you attach a debugger to the game.
- **dotPeek** in the Toolkit, and an Open the Assembly in dotPeek button on a crash frame when you have it installed.

## License

License terms are in the [Alkeari License Agreement](https://gist.github.com/Alkeari/2c6ec0cdf3dafee375b1a00b28ca190a).
