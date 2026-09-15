# Changelog

## v3.5.0 - 2026-09-15

- Added: once a day as it starts, BEM checks its own Nexus page and, when a newer BEM is out, shows
  one bar across the top of the window with Open Page and Skip This Version; closing it hides it
  until the next day.
- Added: a Settings option, on by default, to turn that startup check off.
- Changed: Check for a Newer BEM in Settings no longer needs a Nexus API key.

## v3.4.2 - 2026-09-15

- Fixed: with Shared Settings on, the first launch of a newly downloaded version no longer lowers
  your graphics settings in every version; what that first run writes stays in that version, and its
  next launch gets your shared settings.
- Fixed: raising your graphics settings again after they dropped is no longer mistaken for the game
  refusing them, so the change reaches your other versions at their next launch.

## v3.4.1 - 2026-09-15

- Fixed: a launch that could not put a version's Documents, ProgramData or AppData folder back no
  longer leaves every later launch refused with "could not be put back" and "waiting on the staged
  copy"; the next launch or BEM start finishes the interrupted restore instead of refusing again.
- Fixed: a second copy of your game data that an interrupted restore left beside the real one no
  longer blocks launching, and is removed only once every file in it is proven identical to the copy
  that stays.
- Changed: a failed launch names why a folder could not be put back and no longer suggests running
  as administrator.

## v3.4.0 - 2026-09-12

- Added: Library installs one archive into several versions at once through an Install Into list,
  with only the version you are on ticked, and a Results by Instance list saying what installed,
  what had nothing to do and what failed in each; Install BUTR Stack installs into the versions you
  tick there too.
- Added: the resting version's folder is named Resting Instance and keeps that name through a Steam
  update, and at startup BEM re-reads the game version from an adopted install's own files so its
  record follows the update.
- Changed: a Steam Workshop module is described on the Play page as a Steam subscription, with no
  Nexus information or Nexus options, since it has no Nexus page.
- Changed: uninstalling Steam Workshop mods, one at a time, in a selection or among leftovers, offers
  to open all of their Workshop pages in your web browser so you can unsubscribe.
- Fixed: uninstalling a Steam Workshop mod no longer deletes its folder, which left Steam reporting
  it installed and made the game refuse to load it in every version; a folder Steam still holds is
  never deleted, moved or quarantined, while what an unsubscribed item leaves behind is cleared like
  any other leftovers.
- Fixed: Library no longer writes into a version's Modules folder while the game has files open
  there.
- Fixed: an archive read under one version and installed after switching versions no longer writes
  its loose files into the version it was read under.
- Fixed: every page stays usable at any window size: no scrolling area sits inside another, a row too
  wide for the window scrolls sideways instead of running off the edge, and the Library archive list
  keeps its height while an install reports.
- Fixed: a long version name no longer cuts off the facts under the Play title or pushes the launch
  controls into a scroller.
- Fixed: labels that wrote ID as Id now write it as ID.

## v3.3.1 - 2026-09-10

- Fixed: a button, checkbox or menu item of six words or more no longer capitalizes every word, so
  "Permit Manual Override of Load Order Rules" now reads "Permit manual override of load order
  rules". Eighty-three labels in English, and three hundred in Turkish, where the whole set had been
  capitalized the English way, which Turkish never does.
- Fixed: the mod page said Mod Manager Download installs a mod through BEM and that you can sign in
  to Nexus from Settings. The first lands the file ready for one more press, and the second is not
  available. Fifteen claims on the page were checked against the program and corrected.

## v3.3.0 - 2026-09-10

- Added: one setting in Settings gives every install BEM manages the same base game Video,
  Performance, Audio, Gameplay and Keybinding settings, off until you turn it on, so a graphics
  tweak or a rebound key made in one install is there in all of them; mod settings, load orders and
  saves are never shared, a setting a newer game version introduced is never pushed onto an older
  install, a keybinding category a mod owns is never created in an install without that mod, and
  turning it back off puts each install back on the settings it had.
- Added: the Play page's module list takes a selection the way Windows Explorer does, with
  Ctrl+click, Shift+click, Ctrl+A and a click on empty space to clear it, and one right-click then
  acts on the whole selection through thirteen batch actions, each naming the count it will act on
  and naming every module it had to skip and why.
- Added: Health's Install Checks reports two folders under Modules that declare the same module id,
  naming both paths in full and dating each from its own files, which is what the game's own
  duplicate id error leaves out.
- Changed: a right-click on a module carries what you came to do, Enable or Disable, Uninstall, Move
  to Top, Move to Bottom, Open Mod Page and Pin, with the maintenance items revealed by
  Shift+right-click; an action with nothing to act on is now hidden rather than shown grayed out,
  while one that is merely blocked still appears with the reason it cannot run.
- Changed: an instance is named for what it is rather than for its version alone, so its folder
  reads "Realm of Thrones - v1.3.15 + WS" and the Play dropdown says the same, two installs you
  named alike are told apart, every download offers a name instead of only a second copy of a
  version, and folders you already have are renamed at startup with their saves moving with them.
- Fixed: a downloaded version no longer lands in a folder that states the version twice or repeats
  a DLC's full name, and the name offered for a second copy of a version is now a plain "Copy 2"
  rather than one that restates the version.
- Fixed: the Play page's module list no longer empties and jumps back to the top whenever something
  writes into the game's Modules folder, so your place in a long list is kept.
- Fixed: a mod's build tool can deploy into the game's Modules folder while BEM is open, instead of
  being refused because BEM still had the files inside that folder open.
- Fixed: an Install BUTR Stack run holds the Library page busy for its whole length, so Install
  Selected can no longer write into the same Modules folder at the same time, and a download started
  during the walk no longer cancels the walk's own transfer and reports that mod as failed.
- Fixed: Auto-Sort runs its own default plan and leaves the Sort menu's label, its ticks and the
  keys you chose exactly as they were.
- Removed: the tools only a mod author could use are gone from the published build: Forensics' Read
  the Compiled Method panel with its Show the Faulting Method, Decompile the Selected Frame and
  dotPeek buttons, its Keep a Full Crash Dump for the Game setting, Open SubModule.xml on a module
  row, and the SubModule Validator's Update All Dependency Versions and Update This Module's
  Versions.

## v3.2.0 - 2026-09-08

- Added: an Install BUTR Stack button on Library downloads and installs Harmony, ButterLib,
  UIExtenderEx, MCM and BLSE into the selected version in one press, walking a non-Premium Nexus
  account through them one Mod Manager Download at a time rather than refusing, and leaving alone
  any mod whose installed copy is already current or newer.
- Added: a load order profile can be exported to a file and handed to another player; it records the
  game version and the mod versions it was built with, and importing it saves it beside your own
  profiles while naming every mod you have at a different version and every one you do not have.
  Restoring a profile can never turn one of the game's own modules off or turn on a mod you do not
  have, and says how many entries it left as they were.
- Added: right-clicking a version on the Versions page opens a menu carrying Rename, Open Folder,
  Copy Path, Set as Active, Set as Resting, Remove and Show Details, acting on the row you clicked;
  the resting install is marked (Resting) so two installs of one version are told apart, and a
  version you already have can be installed a second time from that menu or the download list,
  named as it downloads, without disturbing the copy you already had. Set as Resting moves the
  marker as soon as you press it, and Install Another Copy says BEM has not read Steam's version
  list yet rather than blaming Steam for dropping the version.
- Fixed: the BUTR Stack run only claims what it actually did: a run that could not read a single mod
  page reports that rather than reporting the stack as installed and current, the guided walk credits
  each download to the mod it was started for and ignores a different file on the same page, its
  notice cannot be dismissed while it is still waiting on you, and a failure part way through reports
  the mod it happened on instead of leaving the walk spinning with nothing that can end it.
- Fixed: downloading a game version no longer stops seconds after it starts, and no longer asks for
  a Steam Guard code that nothing is waiting for; an ordinary progress line was being read as a
  question, and a sign-in that had worked was being reported as a failure and run again.
- Fixed: BEM is honest about its own work getting in the way: closing it during a download asks once,
  in one dialog that also covers an unwritten load order change, and says what has transferred is
  kept; and the Launch button no longer returns while BEM is still putting the shared game folders
  back, so a press is no longer refused with a claim that another BEM window is busy.
- Fixed: the notice about BEM's own module being left in your game folder now looks in every game
  folder BEM knows about rather than only the version in play, names each folder by the name that
  version is listed under everywhere else, and reports every folder separately when you take the
  module back out.
- Fixed: a version's DLC variant and the same version without it no longer share one identity, and
  an install that gained a DLC outside BEM has its recorded DLC corrected from its own files at
  startup instead of staying recorded as the base game.
- Fixed: a module signed with a made-up certificate that merely names TaleWorlds, and a folder
  holding nothing but a SubModule.xml, are no longer trusted as one of the game's own, and the
  warning about a folder copying an official module's id now names that folder.
- Fixed: an archive BEM downloaded itself now outranks a module's own declared page when the two
  disagree, so a mod whose SubModule.xml points at the wrong Nexus page is no longer identified by
  that page instead of by the download BEM performed; and Identify Archives On Nexus no longer
  spends your Nexus allowance on archives it can already answer from a mod page it has read, or on
  a file hash Nexus has recently said it does not recognize, says which of the two emptied the
  queue, and offers a button that puts the unrecognized ones back.
- Fixed: text in the profile import, load order import and save restore dialogs is no longer cut off
  at the right edge, and Library's progress bar now covers every job it runs and says what it is
  doing, replacing the separate spinner that reported the same work twice, without printing the
  percentage twice or captioning a running job with a finished one's words.
- Changed: module notes start hidden in a newly installed version, the launch target note says
  "resting" where it used to say "active", and both keep whatever you set them to.

## v3.1.1 - 2026-09-08

- Fixed: removing a version is refused while Bannerlord is running and while that version is the
  one in play, so a delete can no longer take the save folder the running game is writing to.
- Fixed: removing the version Play was set to launch hands the next launch to the resting version
  instead of leaving Play pointing at something that is gone.
- Fixed: completely removing a version that only references a Steam install no longer claims the
  Steam install went to the Recycle Bin; it says what was removed and what was left alone.
- Fixed: the Official badge no longer follows from a mod declaring one of the game's own module ids
  from a folder of another name, a copy of an official module's folder no longer costs the real
  module its badge and its protection from batch actions, and one file BEM could not open no longer
  speaks for a folder of unsigned ones.
- Fixed: Minimize on Launch minimizes when the game starts rather than when you come back, and the
  launch confirmation appears at the start of the run.
- Fixed: Disable on a mod reported the load order as unreadable whenever Play had not loaded it,
  and restoring a backup or a bisection state had its result overwritten by its own reload.
- Fixed: an archive whose SubModule.xml declares no module id could crash Library, and an error
  while refreshing the archive list could close BEM instead of being written to the log.
- Changed: Library's archive list updates the moment an archive is added, removed or analyzed,
  rather than a third of a second later.
- Fixed: every dialog wraps its title and button labels and shows the whole of each on hover, so a
  long account name or path is no longer cut off.
- Fixed: counts of cleared logs and crash reports used a noun that agreed with the number of
  folders rather than the number of files.
- Added: Stop Download on the Versions page ends a download that is running and keeps everything
  already transferred, so Unfinished Downloads carries it on; the Steam downloader is closed with
  it rather than left running once BEM has given up on it, and a download stopped because Steam
  asked for a second Steam Guard code says so instead of blaming you for not finishing the sign-in.
- Fixed: a stray JSON file in the Languages folder no longer appears as a language, the drag
  refusal no longer ends in English in every other language, and the import preview keeps its noun
  when more than one module in the file is missing.

## v3.1.0 - 2026-09-08

- Fixed: a freshly downloaded game version no longer warns that Native, SandBox and the rest of the
  game's own modules are not signed by TaleWorlds; they are signed, with a certificate Windows will
  not trust, and BEM now recognizes the game's own modules by name and folder as well. A folder
  wearing an official module's name is still demoted when the assemblies inside it are signed by
  someone else, and now also when they carry no TaleWorlds signature at all.
- Added: removing a version can now take the whole version folder, saves and settings included, on a
  tick box that is off by default and tells you how many items and how much disk go to the Recycle
  Bin before you agree to it. A download that stopped short is listed on the same page, with Resume
  to carry it on from the bytes already fetched and Discard to send it to the Recycle Bin.
- Fixed: the Play page no longer jumps while a version downloads, and its status line no longer runs
  off the end of the row: both keep a fixed footprint, wrap to two lines and put the whole message on
  the hover. Launch also comes back after a session on a version that is not the resting one, and
  while a launch is held it says whether it is starting, playing or releasing that version's folders
  instead of looking broken.
- Added: BEM speaks German, Spanish, French, Italian, Japanese, Korean, Polish, Brazilian
  Portuguese, Russian, Turkish and Simplified Chinese as well as English. It picks your Windows
  language on first run, and Settings has a Language picker to change it.
- Added: support for a Languages folder beside the app, so you can correct a translation or add a
  language of your own without waiting for a new build.
- Fixed: messages that count things now read correctly when there is exactly one, instead of
  leaving a literal "(s)" on screen or saying "1 mods". Buttons, menu items, headings, tabs and
  every other label are capitalized consistently as well, so "Resume download" now reads
  "Resume Download". The sentences reporting what enabling or disabling a module did to your load
  order, and where a module was moved to, are written out in full in every language instead of
  being pieced together from single words, so they agree with the count and no longer read as
  nonsense in a translated build.
- Changed: downloading a game version moved from Play's version dropdown to a Download button on
  the Versions page. Play's dropdown now lists only versions already on this machine, so switching
  can never start a multi-hour download; a download already running is still shown on Play, read
  only, and the Steam sign-in code appears on Versions with the button that asked for it. Steam is
  signed in to before the download starts rather than partway through it, so a Steam Guard code can
  no longer end a download that has been running for hours; your Steam Guard code is asked for
  before the downloader starts, so it is ready the instant Steam asks and is never typed while the
  downloader waits, and one sign-in at the start covers every part of the download, so a version
  built with War Sails no longer asks for a second code when it reaches the DLC hours later; a run
  that ends before the code reaches it says exactly that, and BEM tries it
  once more on its own rather than leaving you to start the download again; how a download went is
  reported once, on the page it was started from; and BEM's own log now records what the downloader
  was asked for, how far it got and how it ended.
- Added: owning War Sails, a version can be built with it. Pick "v1.5.2 + War Sails" on the
  Versions page to download both together, or add War Sails to an already-built v1.5.2 there
  without re-downloading the base game.
- Fixed: a SubModule.xml a download is still writing no longer reads as a broken module for the
  half-minute it takes to finish.
- Fixed: Play now watches whichever version is selected, not only the resting one, so installing
  into a non-resting version shows up right away instead of after a restart, and the refresh that
  follows a file change no longer erases what Undo could put back.
- Fixed: the Versions page reads which DLC an instance actually has from its own game files
  instead of only what BEM itself downloaded, so an instance that already owns a DLC is never
  offered a redundant download of it.
- Added: column headers on the Versions page, and its version column no longer repeats itself. The
  version dropdowns mark only the version you are on, as Active, rather than labeling every row
  with what the list is already for, and a dialog button carrying your Steam account name keeps the
  whole name instead of cutting it off.

## v3.0.0 - 2026-09-05

- Added: BEM manages more than one Bannerlord version at once. Pick a version from the dropdown at the top of Play, BEM downloads it if it is not installed yet, and a version you have just added starts with the game's own modules enabled in the order the game expects.
- Fixed: Saves, mod settings, logs, crash evidence, load orders, pins, profiles, backups, accepted findings and update records belong to the version they came from instead of being shared, and boot checks, guided searches, watched sessions and launching into a save all run against the version you picked.
- Fixed: Installing mods on one version no longer permanently deletes module folders kept from another version, and restoring a replaced game file no longer copies one version's binaries into another version's install.
- Fixed: A finished download is no longer reported as incomplete and refused, and the size shown for a version now counts its game files wherever they live.
- Fixed: Making a version the resting one takes that version's settings, history and backups with it, and says what did not move.
- Fixed: Auto-Sort keeps an official DLC such as War Sails with the game's own modules instead of behind a large overhaul that other mods depend on.
- Added: A Nexus link opened from your browser names the version it will install into and asks before downloading.
- Added: An optional "Install 7-Zip for me when a mod needs it" setting, off by default and printing the exact command before it ever runs, and the message shown when a .7z or .rar cannot be opened now points at Settings, Toolkit, which is visible in Basic mode, explains what the 7ZIP_EXE variable is for and says BEM has to be restarted after setting it.
- Fixed: A mod downloaded with Mod Manager Download that Nexus serves as 7z or RAR arrived named ".zip" and failed to install with "End of Central Directory record could not be found"; an archive is now read as the format its own bytes say it is.
- Fixed: Installing a mod updates the Play tab straight away instead of after a restart, and an install that fails partway no longer leaves the empty module folder it created sitting in the game.
- Fixed: On Library, the list of downloaded archives is no longer squeezed to a row and a half by the panels underneath it.
- Changed: One type face and size ladder, square corners, solid grays, a gold focus outline and a one-sentence tooltip on every control, with long names shortened rather than wrapped.

## v2.5.0 - 2026-08-27

- Added: the Play tab's load order can be grouped into named sections, inserted from any module's right-click menu and renamed, collapsed or removed from their own.
- Added: collapsing a section folds the modules under it out of the list and says how many are hidden, without changing your load order or what the game boots.
- Added: sections in a shared load order file are recognized when you import it, instead of being listed as mods you do not have, and your own are written back out when you export one.
- Added: a Settings option, "Keep sections with their modules when Auto-Sorting". It is off by default, so Auto-Sort moves every section to the bottom of the list; turned on, each section follows the module it was sitting above.
- Added: a saved load order profile records the sections the list had and puts them back when you restore it; a profile saved before this release records none and leaves your sections alone.
- Changed: dragging is blocked while any section is collapsed, because the modules folded away are not on screen to be reordered around. Expand it to drag again.
- Changed: Undo still puts back the module order and enabled state an import or a restored profile changed; it does not remove the sections either one brought in. Remove those from their own right-click menu.
- Fixed: Install Available Updates could leave a module reading as unknown status on the next check, even though the update itself installed correctly. BEM now keeps a full record of which file it fetched, the same way a manual Nexus download already did.

## v2.4.0 - 2026-08-23

- Added: BEM can check for a newer version of itself. Settings, under Network features, asks BEM's own Nexus page with your key and says plainly whether a newer version is published, with a button to its page. Nothing installs itself; a newer BEM downloads from the page like any release.
- Added: Epic Games installs are found automatically. The Epic launcher's install manifests are read and every location is tested against the game's own folder shape, so a renamed listing cannot be missed and no other game can match.

## v2.3.1 - 2026-08-23

- Fixed: "Launch into save" failed with "Failed to find save" for any save whose name contains a space. The game's own starter rejoins the launch arguments with plain spaces and no quoting, so a conventionally quoted name reached BLSE as two words and only the first was looked up. The name now travels with escaped quotes that survive the round trip.
- Fixed: the release package shipped without BEM's companion module, so watch sessions and boot checks reported the companion as not installed on every machine but the developer's. The Companion folder now travels beside the exe, and the setup notes say to keep it there.
- Fixed: on a fresh machine, the game install BEM auto-detected on Play never reached Library or Settings, which showed blank folder boxes for a game BEM had already found. The detected path is now saved the moment it is found, and GOG's default install folders are searched as well as Steam and Game Pass.
- Fixed: a first Nexus "Mod Manager Download" with nothing configured failed twice before it could succeed - first for the key, then again for the folder. Everything missing is now named in one answer, and the "add a key" button opens the exact Settings section the key goes in instead of the top of the page.
- Fixed: several messages still directed you to "the Install page", which no longer exists - the folder pickers are in Settings and the page is Library. Every message now points where the control actually is, including the ones that named panels visible only in Advanced mode without saying so.
- Fixed: a crash on a background thread bypassed both the error dialog and the log, and the crash dialog itself stayed silent if another dialog was already open. Both now always reach the log, and a blocked dialog falls back to a plain system message box.
- Added: the app version is shown in the title bar and on the Settings page, so a bug report can say which build it is about.
- Added: publisher metadata in the exe (Alkeari Labs LLC, copyright), a LICENSE, and third-party notices covering every shipped library; both documents ship in the archive beside the exe.
- Note for users of the original 1.0.0 release: its full-folder backup zips are not managed by this version and can be deleted by hand from %LOCALAPPDATA%\Bannerlord Environment Manager\backups.

## v2.3.0 - 2026-08-23

- Added: Advanced mode now splits BEM into an everyday surface and everything else. Basic keeps the whole core loop - install, order, launch, play, and see what broke - while the investigative and power-user surfaces show only in Advanced: the Forensics destination, the sort-key and pin machinery on Play, the mod-id repair tools, the Library tools band (update coverage, quarantines, replaced-file restore panels), and the launch tuning in Settings (extra arguments, BLSE crash handling, the machine tools, the load-order override).
- Added: in Basic mode the Health page carries a Crash Reports tile, so a crashed game is still one click from its answers while Forensics is out of the navigation.
- Added: arming "Watch launches" on Play brings the Forensics destination into the navigation in either mode, so the page its results land on never vanishes on whoever just started using it.
- Changed: nothing is disabled by Basic and nothing stops running - scans still scan, quarantines still fill, pins still hold, backups still happen. Anything a check finds is shown in both modes; Basic changes what is offered, never what BEM does or what it found.

## v2.2.1 - 2026-08-23

- Fixed: the archive folder watcher could lose events with nothing anywhere saying so - its buffer overflows when several downloads land at once, and the watch dies if the folder moves or its drive disconnects. Both are now handled: logged, the watch restarted, and the folder re-read to reconcile whatever was missed.
- Changed: the Updates flyout on Play reads as its two actual workflows. Fetch Fresh sits with the update check it modifies, Rebuild Every Mod ID under its own Mod IDs heading with Query Nexus and Undo Rebuild, and a hairline separates the groups.

## v2.2.0 - 2026-08-23

- Added: a "Check Pre-Flight" button on Play that runs and shows the Preflight Checks on demand, instead of Preflight only ever appearing after Launch was clicked. The old "Check This Load Order Boots" button moves to Diagnostics as a Boot Checks action.
- Added: Accept the Risk on Preflight warnings, Install Check warnings, Suspicious-grade Mod Safety alarms, and Boot Check findings. Accepted findings stop counting against Health, and Health lists every accepted finding in one place with a way to forget it. Critical Preflight findings and Known-Bad safety verdicts are never offered it.
- Fixed: "Launch into save" on the Saves tab produced "No modules were provided as an argument!" from BLSE and never started. The launch now carries the save's own recorded module list on the command line, so a save opens with exactly the load order it was written with.
- Fixed: the first page after starting BEM could render blank until you left and came back. The shell now waits for the content area to have a real size before the first navigation.
- Fixed: an archive published as RAR but downloaded through Nexus's opaque link landed under a ".zip" name and failed to open. The real format is now read from the file's own bytes and overrides whatever the name claims.
- Fixed: a dropped connection during a download or an update check was reported as a failure on the first blip; both now retry with backoff before giving up.
- Fixed: an update's staged download was never deleted after installing, so the staging folder accumulated every update ever installed. It is cleaned up after each install.
- Fixed: a failed safety scan on a downloaded archive, a failed move to the Recycle Bin, and a failed file unblock were all silent. Each is now recorded and reported instead of looking like success.
- Added: watched play sessions write a heartbeat once a minute, so the timeline can say "the game was still running as of here" and tell a hang or forced close apart from a session that simply has not ended yet.
- Changed: module fingerprints are cached against a cheap file-metadata signature, so scans skip re-hashing folders that have not changed on disk.
- Changed: timeline rows, diagnostic file rows and log rows each render as one selectable text block, so a whole entry can be selected and copied together.

## v2.1.2 - 2026-08-19

- Fixed: the app no longer crashes when you return to Play after visiting another screen. The selection-visual template that caused a layout failure has been removed.
- Fixed: Play no longer freezes while it loads. The module scan (including the signature check on every mod) now runs on a background thread, so the window stays responsive.
- Changed: multi-select on Play no longer draws a second checkbox per row. Hold Ctrl and click modules to highlight the ones you want; right-click acts on all highlighted. The only checkbox left on a row is the one that enables it in the load order.

## v2.1.1 - 2026-08-19

- Fixed: "Install Available Updates" no longer offers more modules than the update check reports out of date. It now plans only modules the check actually flagged, so the count agrees with the out-of-date tally.
- Fixed: the Play page and right-click menus no longer re-read the whole Nexus ledger on every use; the update plan is computed once and reused, which removes a noticeable lag on load and right-click.
- Changed: selecting modules on Play now highlights the selected rows with a faint fill and gold outline instead of showing a second checkbox beside each module's enable checkbox.
- Changed: before the first update check the Install Updates count is read without touching the version ledger at all, so Play loads faster on a fresh start.

## v2.1.0 - 2026-08-19

- Added: autonomous update install. Play now offers "Install Available Updates" and a module's menu has "Update Mod"; BEM plans which newer file Nexus published, downloads it, and installs it through the same safe pipeline as a manual install. Needs a Nexus key; an update that touches the game's own files is confirmed before it happens.
- Added: multi-select on Play. Select several modules and Open mod pages applies to all of them.
- Fixed: clicking a destination such as Health or Forensics again now returns to its landing page from a drill-in page, instead of sitting on the subpage.
- Changed: the tooltip on every Why? button now states what that page or section is for, on every page.

## v2.0.0 - 2026-08-19

BEM rebuilt. Version 1 was a tabbed utility; everything below was built since, on a new core
engine with its own test suite (2,800+ tests), and ships as one piece.

### The interface

- Changed: the whole interface is six destinations - Play, Library, Health, Forensics, Saves, and Settings - organized by what you came to do rather than by which subsystem produced the screen. Health and Forensics answer their question on a landing page and drill into the full evidence.
- Added: one shared page frame, one styled control set, one accent budget, selectable text everywhere, right-click menus on modules, archives, logs, crash reports and saves, and a configurable module row density.

### The load order

- Added: every sort is a topological pass over the constraints mods actually declare, so no sort can produce an order the mods themselves forbid. Auto-Sort applies the default plan; up to five sort keys stack in the order you chose them.
- Added: a drag that would break a declared load-before, load-after or dependency is refused with the constraint named. A settings switch permits manual override, off by default.
- Added: validation for missing dependencies, version mismatches, wrong sequence and dependency cycles, graded by whether the ordering was stated or merely implied.
- Added: import and export as a .bmlist, a Novus preset or plain text; ten dated load order backups with named profiles; pinning; and an undo for any bulk change.
- Changed: the load order writes to LauncherData.xml as you edit - there is no Save button to forget. A change is held only while a launcher is running and goes out when it closes. Comments and unknown elements in the file survive every write.

### Launching

- Added: launch targets for BLSE Standalone, BLSE Launcher, BLSE LauncherEx, the game exe, the TaleWorlds launcher and Steam, resolved from what is actually installed, with BLSE's crash handler flags passed from BEM's own settings.
- Added: Preflight Checks between pressing Launch and the game starting - missing BLSE, missing libraries, patch targets that do not exist on this install, stylesheets that empty shared game datasets, the command line character budget - each ending in a fix or in a plain statement that there is none.

### Installing and updating

- Added: archive install from a watched folder - zip, 7z and RAR, module layouts and bin payloads alike.
- Added: nxm:// links from Nexus download straight into BEM; a second click reaches the BEM already running instead of opening another.
- Added: Nexus sign-in in the browser instead of pasting a key by hand.
- Added: update checking against Nexus with the version ledger, saying what changed and never touching your mods because of it.
- Added: mod page identification by manifest, filename grammar, archive hash and BUTR's index, every learned id put in front of you to confirm, and a wrong page can be ruled out and remembered.
- Added: nothing BEM removes is destroyed. Replaced module folders, shader folders, archived settings, cleared logs and crash reports all move to a quarantine or the Recycle Bin with a restore panel beside them.

### Health

- Added: Install Checks - rig conflict patchers, duplicate and shadowed assemblies, Harmony patches against methods that do not exist, mods shipping copies of the game's own assemblies, and Workshop copies hiding in the Modules folder.
- Added: Mod Overlaps - which mod wins each contested XML attribute, the values the engine silently drops, and the game's whole XSLT pipeline actually run so "BEM cannot tell" became an answer.
- Added: Mod Safety - a scan of installed modules and downloaded archives for the trojanized-mod fingerprint, at startup and before anything is installed.
- Added: Boot Checks - a dry run that launches the load order with a companion module aboard and says whether it boots, and what threw if it did not.

### Forensics

- Added: crash reading - the reports the game writes, the dumps Windows kept, the event log, and BEM's own captured copies taken before the game deletes them - joined into ranked suspects with the evidence stated, and "I do not know" said rather than a mod named on thin evidence.
- Added: watched play sessions - the companion records who patches what as they patch, and reads the launch back as a timeline.
- Added: bisection - the layer that earns the word "caused" - which searches inside a set you already narrowed, resumes where it left off, and never proposes a run outside your scope.
- Added: a drafted bug report built from the launches that actually proved the problem, and crash evidence contributed back to BUTR.

### Saves

- Added: a Saves page that reads each save's own recorded module list and says what has changed since it was made - and whether it will still load the way it did.
- Added: Launch into Save - straight into a campaign through BLSE with the save's own load order, not the currently enabled one.

### Platform

- Added: Game Pass installs are accepted, the platform is read from the bin folder, the companion is built for whichever runtime the install runs, and only the binaries this install can load are touched.
- Added: a Toolkit page for the external tools BEM can install but cannot ship.

## v1.0.0 - 2026-05-03

- The original Nexus release: a tabbed desktop utility with a SubModule validator, a mod folder checker, and basic module scanning.
