# Translator's Glossary

This is for whoever writes or corrects a translation of Bannerlord Environment Manager (BEM), including
someone who has never seen this codebase. Read it once before starting.

## 1. Terms that stay English in every language

| Term | Why |
|---|---|
| Bannerlord Environment Manager, BEM | The app's own name. Names are not translated. |
| Bannerlord, Mount and Blade II | The game's name, as TaleWorlds ships it. |
| Nexus Mods, Nexus | The mod site's brand name. |
| Vortex | Nexus's mod manager, a brand name. |
| Steam, Xbox, GOG, Epic | Platform and storefront brand names. |
| BLSE | Bannerlord Loader Studio (Standalone) Extension, the loader BEM launches through. A proper name, not a translatable phrase. |
| Harmony, `0Harmony.dll` | The patching library Bannerlord mods run on, and its file name. |
| BUTR | The Bannerlord modding org (BUTR Manager, BUTR's dependency index) BEM reads and defers to. |
| War Sails | A Bannerlord DLC name. |
| `SubModule.xml`, `Modules`, `LauncherData.xml`, `Configs`, `ModuleData` | Exact file and folder names Bannerlord itself uses on disk. Renaming them in a translation would point a user at a path that does not exist. |
| `nxm://` | The URL scheme Nexus uses for one-click downloads. Protocol schemes are not translated. |
| `instance.json`, `capture.json`, `retention-log.txt` | BEM's own on-disk file names, referenced in guidance text. |
| DepotDownloader | The third-party tool BEM uses to fetch Steam depots, a proper name. |
| GitHub, PowerShell, Recycle Bin, Windows | Proper names of things BEM talks about (repository host, shell, the OS trash, the OS itself). |
| Play, Library, Health, Forensics, Saves, Settings | BEM's six navigation destinations. |

The six destinations get their own reasoning: they are also the words printed on the screen. A user
who reads a forum post, a guide, or another player's bug report that says "check Forensics" needs that
word to be the same word on their own screen, in their own language, or the instruction stops working.
BEM does not localize these six words anywhere in the app (there is no catalog key for the label text
itself, only for their tooltips), and a translation should not invent one.

### Also left in English

`LauncherData.xml`, `Configs`, `GOG`, `Epic`, DepotDownloader, GitHub, PowerShell, and TaleWorlds (the
game's developer). `War Sails`, `nxm://` and the three BEM-internal file names do not appear as literal
substrings in the catalog, since they show up as argument data or in code rather than in translated
sentences, but they follow the same rule and are listed for completeness.

## 2. Rules for translating a value

**Placeholders keep their number, not their position.** `{0}`, `{1}`, and so on are positional
arguments BEM fills in at runtime (a module name, a count, a path). Move them wherever the sentence
reads naturally in the target language; do not renumber, remove, or duplicate them. A placeholder that
goes missing or gains a number that was not in the English source will fail the catalog's integrity
check.

**Placeholders are bounded per form, not copied per form.** The arm you are writing must carry at
least the placeholders English's matching arm carries, and at most the placeholders English uses
anywhere in that key. The floor keeps a fact English states in that arm from going missing; the
ceiling keeps you from referencing an argument BEM never passes. A form English does not have
(Russian's and Polish's `few` and `many`) takes its floor from English's `other`.

**A plural arm must read correctly at every count its own category covers, and that set of counts is
not the same in every language.** English reaches `one` only at 1, which is why English writes
"1 file" with no count in it. French and Brazilian Portuguese reach `one` at 0 and 1, and Russian
reaches it at 1, 21, 31, 41, 101 and so on, so a hardcoded "1" in those languages is a false
sentence at every other count the arm serves. Write the count placeholder into the `one` arm there
and inflect the noun for what your language does at 0 or at 21. The count is available to you
whenever English uses it in any arm of that key, which is what the ceiling above allows. Which
counts your category covers is decided by `PluralRules.Select` in
`Bannerlord Environment Manager.Core/Localization/PluralRules.cs`; read it before writing a `one` arm.

**Plurals follow the target language's own rules, not English's.** A key with `one` and `other` forms
in English is not a template for what your language needs; some languages need more forms (Russian and
Polish distinguish `one`, `few`, and `many`), and some need fewer (Chinese, Japanese, and Korean use
only `other`). Do not guess or copy another language's shape. The catalog's own plural rules decide
what is required per language: see `PluralRules.Categories` in
`Bannerlord Environment Manager.Core/Localization/PluralRules.cs`. Supply exactly the forms it lists
for your language code, no more and no fewer.

**No sentence gains or loses a fact.** If English says a file was moved to the Recycle Bin, the
translation says it was moved to the Recycle Bin, not that it was deleted. If English names a specific
count, condition, or consequence, the translation keeps that same count, condition, and consequence.
BEM's wording is often deliberately precise about what it did and did not do (see the `Diagnostics` and
`Safety` catalog sections); a looser paraphrase in translation can turn an accurate warning into a
misleading one.

**Length matters where the layout is tight.** German and Russian commonly run 20 to 35 percent longer
than the equivalent English. Most body text and tooltips have room for that. Button labels and column
headers do not: a label that doubles in length will clip. Where a key's context is a button, a header,
or a pill, favor the shorter accurate phrasing over the most literal one.

**Quoted data stays as data.** Module names, versions, paths, and quoted evidence that BEM inserts at a
`{0}` are runtime values, not English words to translate. Translate the sentence around them; leave the
placeholder's eventual content untouched, because you will never see what fills it while translating
the template.

## 3. Contributing a translation

1. Copy `en.json` from `Bannerlord Environment Manager.Core/Localization/Catalogs/`.
2. Translate every value. Keep every key exactly as it is; a key is never renamed, even if the English
   wording it once matched has since changed.
3. Set `$language` at the top of your file to your language's code, native name, and English name.
4. Put the finished file in a `Languages` folder beside the BEM executable. BEM reads it as an overlay
   the next time the app starts (changing language always restarts the app).
5. Before sending it back, check it against what
   `Bannerlord Environment Manager.Core.Tests/Localization/CatalogIntegrityTests.cs` enforces: every
   English key present and no extra ones, every value non-empty, every placeholder set inside the bound the
   English forms set (at least its matching arm, at most the whole key), plural forms matching what `PluralRules.Categories` requires for your language code, and
   any key that is a plural key in English staying a plural key in your file.
