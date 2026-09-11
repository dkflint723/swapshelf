# Changelog

Changes made in [dkflint723/swapshelf](https://github.com/dkflint723/swapshelf), a personal
fork of [beeradmoore/dlss-swapper](https://github.com/beeradmoore/dlss-swapper). Entries describe
what changed for someone using the app, and why — the reasoning in full is in the commit messages.

Nothing here has been offered upstream. Builds from this fork are **unsigned**; the original
project is the signed, official one.

**Versions from 2.0.0.0 onward are this fork's own count** and have nothing to do with upstream's
number. Before that the scheme was upstream's version with this fork's count in the fourth part —
1.2.5.1 being the first build on top of upstream's 1.2.5 — which stopped working the day upstream
released a 1.2.6.0 of its own: two different builds wearing one number, and this fork's reading as
*older* than an upstream release it already contained all of. Starting a separate line at 2 keeps
them apart without anyone having to remember a rule. It stays four plain numbers because the
updater packs them into 16 bits each, so a suffix like `-fork.3` would silently stop update checks
working.

## v3.0.6.0 — every step of a swap, checked

This release comes out of a review of everything Swapshelf does between downloading a dll and
putting a game back the way it was. Most of it only shows when something has already gone wrong,
which is exactly when it has to work. Some of it you will see the first time you swap.

### What you will see

**The anti-cheat note is asked per game.** It used to appear once per installation, before the
first swap of any game, and never again — so by the time it applied to a multiplayer game it had
been dismissed weeks earlier over a single-player one. It now lists the games a swap is about,
names the anti-cheat Swapshelf found in any of them, such as Easy Anti-Cheat or BattlEye, and makes
Cancel the default button. Cancelling cancels the swap. A game that gains an anti-cheat after you
said yes is asked about again.

**How much is known about a version, before you pick it.** The version picker and the update preview
label each choice Known good, Likely fine, Experimental or No evidence yet, with a one-line reason.
Each label has a shape of its own, so no colour has to carry it. The file the game shipped with is
Known good. A developer build, a file not signed by the company that makes it, or an older release
line than the game shipped with is Experimental. With nothing known either way, it says so rather
than guessing.

**Games that use NVIDIA Streamline say so** on their page. In those games, frame generation and ray
reconstruction swaps across release lines are marked Experimental unless you have swapped that
version into the game before: Streamline loads those two as a matched set, which is where
cross-line swaps are known to go wrong.

**Restore asks before erasing a change.** If a dll changed after Swapshelf swapped it — a game
update, a mod, a fix applied by hand — restore stops and says so, with Restore anyway to go ahead.
It used to write over it without a word.

**An interrupted swap is put back.** Each swap is written down before the game folder is touched. If
Swapshelf is closed or killed partway through one, the next launch returns that game to how it was
before the swap began, and the games page says which game.

**Diagnostics you can paste anywhere.** The copied text now includes what each manifest holds and
the last 200 lines of the log, with folder paths replaced by placeholders such as
`<Game: Alan Wake 2>` and `<UserProfile>`. Tick *Include real folder paths* to see them as they are.

### Checks that now happen

- A game that is running is refused, with a message to close it first, before anything is touched.
  That goes for swaps and restores alike.
- A dll built for a different processor than the one it replaces, 32-bit over 64-bit or the
  reverse, is refused. The game could not have loaded it.
- A signature has to come from the company that makes that dll: NVIDIA for DLSS, AMD for FSR, Intel
  for XeSS. Any valid signature used to be enough. A dll whose signature was stripped after signing
  is now named as such, instead of "not trusted".
- A saved original is checked against the hash recorded when it was saved, before it is put back.
  One that no longer matches is refused rather than restored as though it were the original.
  Backups made by older builds have no recorded hash and are restored as before.
- SHA-256 is recorded beside MD5 for every dll Swapshelf scans, imports or downloads, and a download
  now checks the dll it extracted, not only the zip it came in.
- The in-app updater checks the installer it downloads against the SHA-256 GitHub publishes for it,
  before running it. That applies from this version on, to the updates that follow it.

### A second copy of every original

The copy of what a game shipped with lives beside its dll, as `nvngx_dlss.dll.dlsss`, inside the
game folder — where Steam's verify, a launcher repair, or a patcher tidying unknown files can delete
it. Swapshelf now keeps a second copy of every saved original in its own data folder, under
`originals`, and puts a missing `.dlsss` back from it.

The originals you already have are copied in the background once the first scan after this update
finishes. That costs disk once, between 20 and 170 MB for each saved dll, and no copy is made that
would leave less than 1 GB free. There is no switch for it on the Settings page yet: to turn it off,
set `KeepOriginalCopiesInLibrary` to `false` in `settings.json`, in the `json` folder of
`%LOCALAPPDATA%\Swapshelf` (or of `StoredData` beside the portable build).

### Fixed

- The number of games using each dll in your library stays current as games change, imported dlls
  included.
- A download whose archive is wrong says what is wrong with it.
- Paths longer than 260 characters work.
- The bundled manifest includes DLSS 310.8 and 310.9, so they are there before the first manifest
  download.

### Command line

`restore` leaves a dll that changed since it was swapped alone, and says so, unless it is given
`--force`. [Hotswap](https://github.com/dkflint723/hotswap) does not pass it, so a restore from
Steam after a game update now asks you to open the game in Swapshelf and choose Restore there. Swap
and restore results carry a `failure` field naming the reason. The contract version is still 1,
since nothing was removed or repurposed.

### Under it

The 2,600-line game class is split into files by concern — scanning, saved originals, covers and
swapping — and the build treats warnings as errors, after the fourteen it carried were fixed at
their source. CI adds Dependabot, CodeQL, a check for vulnerable packages, and a bill of materials
for each release build, and a version tag now runs the distribute workflow as well.

## v3.0.5.0 — the installer stops instead of warning

The installer has always checked whether Swapshelf was running, said "please close it before
continuing", and then installed over it anyway. A warning nobody has to act on is not a warning; it
is a note about what is being overwritten.

It was not theoretical. Both 3.0.3.0 and 3.0.4.0 were installed over the running app, and both left
Add or remove programs showing 3.0.2.0 while the files on disk were current. The same install wrote
every other value in that entry correctly — the name, the publisher, the paths — so the failure was
partial and silent, visible only in the one value that changes between releases. Installing again
with the app closed wrote it correctly from an identical installer, which is what pinned it on the
running process rather than on the installer.

Now it stops: an error, and nothing written. The uninstaller has always refused for the same
reason, so this is the installer catching up with it.

The command line beside the app is checked too, because the same install overwrites it. That one
gets looked at twice with a pause between, because it is a different kind of process — Steam starts
it, it answers, and it exits, so a single look would refuse an install for something that had
already finished.

If you install this over a running copy, it will now tell you to close it and stop. That is the
point, though it does mean this particular update has to be installed the way it is asking you to.

## v3.0.4.0 — the covers come back

Renaming the data folder in 3.0.0.0 moved the cover art with it, and left every note of where that
art lives pointing at the folder that had just stopped existing. Cover paths are stored as full
paths, and nothing rewrote them.

Nothing failed loudly, which is the reason this took two releases to notice. A cover that cannot be
found reads as a game that has no cover, so the app went and fetched the store's own art instead —
240x280 for two of the games it turned up on, stretched into a card three times that size, with the
600x900 image it should have used sitting unread in the same folder. The library did not look
broken. It looked like the art had gone soft.

A game now checks, as it loads, whether its stored cover path is inside the image cache actually in
use, and works the path out again if it is not. A cover you chose yourself wins over a downloaded
one, which is the order the app already used everywhere else, so nothing quietly swaps your art for
the store's. Only a row whose path really moved gets written.

This corrects itself on the first launch after installing. If you already fixed your own library by
re-fetching covers, nothing visible will change — the difference is that it now stays fixed rather
than depending on having asked at the right moment.

## v3.0.3.0 — a new icon

The icon was still the one this was forked from: a purple disc with a magenta arrow and a green one
curving past each other. Two things wrong with keeping it. It advertised a project this is no longer
a version of, which was the whole point of the rename. And it told its two halves apart by colour
alone — magenta against green, the pair red/green colour blindness collapses — so to some people it
was two grey arrows, and at 16px, which is the size it appears at in the taskbar, it was unreadable
to everybody.

The new one is two identical hooks in 180 degree point symmetry, reaching past each other on a deep
navy tile. The gap they leave between them is a single constant-width channel: two pieces milled to
swap places, a shelf board each, and no arrows anywhere.

Its two pieces are told apart by lightness as well as colour, so the mark still works if you cannot
separate the hues — white against amber measures 3.37:1 in greyscale, 3.07:1 under simulated
deuteranopia, where the palette it replaced sat at 1.78:1 and relied on you already knowing which
piece was orange. The tile keeps an edge against a near-black taskbar with a one-pixel rim rather
than by going lighter, because every step lighter the tile takes is a step of contrast off the two
marks on top of it.

Everywhere the icon appears is regenerated from one source: the taskbar and Explorer icon, the
installer, the title bar, and the favicons on the docs site. That source is a script rather than a
drawing, kept in `design/icon`, so the next change to it is an edit rather than an archaeology
project.

The docs site's web app manifest also had an empty name, so installing it from a browser gave you a
blank label under the icon, and its two colours were white behind a navy mark. Both fixed.

## v3.0.2.0 — bug reports come here

Four links sent people to the original project's issue tracker to report things that only happen in
this build: the window shown when the app fails to launch, the network tester's report button, the
dialog after a manual add fails, and the version in Settings.

That last one is the one worth naming. Clicking the version opens the release notes for the tag this
build was stamped with — against a repository that has no release under it, so it was a 404 by
construction from the moment a tag existed, and somebody else's releases page for anyone building
without one. Its sibling a few lines away had already been pointed here, so it was an oversight
rather than a decision.

Links that belong to the original still go there: the wiki pages this app sends you to for help, the
manifest builder where newly discovered dlls are reported — that manifest is theirs and this app
reads it — and the attribution in About.

## v3.0.1.0 — the name in the places you actually read it

3.0.0.0 renamed the app, the installer, the folders and the executables, and left the name it shows
you alone. The title bar still said DLSS Swapper. So did the About panel's link to this build, the
feedback link, the log file's name, and a few hundred strings across every translation.

All of it now says Swapshelf: the window title, every message that names the app, the export and
translation files it saves, and the log — `swapshelf_<date>.log` rather than `dlss_swapper_<date>.log`.

**References to the original are untouched, on purpose.** "A fork of the original DLSS Swapper by
beeradmoore", the links to that project and its community, the troubleshooting wiki, and the names of
its download servers in the network tester all still say DLSS Swapper, because that is what they are
about. Renaming those would have been the opposite of the point.

Two things that had quietly stopped working since the rename are fixed with them. The app updates the
install size shown in Apps & features by writing to its own uninstall key, and it was still opening
the key under the old name, which no longer exists — so it silently did nothing. It also measured the
data folder by its old path rather than asking where the data actually is, which after the migration
was the wrong folder and, in a portable build, always had been.

## v3.0.0.0 — Swapshelf

The app has a name of its own. It was DLSS Swapper (dkflint723 fork), which was accurate when this
was a fork with a few repairs in it and stopped being accurate a long time ago: it has its own
release line, a command line, a Steam plugin, and behaviour that deliberately differs from upstream
in places. Carrying the original's name meant trading on a project this is no longer a version of.

**Swapshelf** is what it does — a shelf of dll versions, downloaded, verified, with the copy of what
each game shipped with kept beside them, and any of it swappable into a game. It also drops "DLSS",
which was the wrong word anyway: of the ten dll types this handles only some are upscalers, and the
rest are frame generation, ray reconstruction, neural rendering and latency.

None of this changes what the app does. Nothing was added and nothing was taken away.

**Your library moves itself, and the move is the careful part.** Everything lived under
`%LOCALAPPDATA%\DLSS Swapper`: the database with its pins, notes and history, the image cache, every
dll ever downloaded or imported, and the copies of what each game shipped with. Pointing at a new
folder and leaving that behind would have read as an empty library and, to anybody who then pressed
restore, as losing the one file nothing can recreate. So the first time Swapshelf runs it moves the
folder — a rename on the same volume rather than 861 MB of copying, so it either happens or it does
not, with no half-migrated state in between. If it cannot be done, because something holds a file
open or a permission refuses, the old folder goes on being used exactly as it was and the next
launch tries again. There is no case where the app starts up looking at nothing while your library
sits on disk.

**The installer removes the old copy.** A new name means a new uninstall entry and a new install
folder, so without this an upgrade would leave two of everything: two entries in Apps & features,
two Start Menu shortcuts, two folders, one of which nothing would ever update again. It runs the
previous version's own uninstaller, which removes exactly what that version installed, and leaves
`%LOCALAPPDATA%` alone for the migration above to deal with.

The executables are renamed with it: `Swapshelf.exe` and `swapshelf-cli.exe`. Anything driving the
command line by path needs updating — the Steam plugin, [Hotswap](https://github.com/dkflint723/hotswap),
does this from 1.3.0.

And one thing that was quietly broken is fixed on the way past. The installer refuses to install
into a folder that is not one of ours, because uninstalling deletes what it installed out of
wherever it was pointed, and somebody choosing a folder that already holds their own files would get
those caught up in it. The check looked for "dlss" in the path, case sensitively, against a folder
called "DLSS Swapper" — so it never matched, and it went unnoticed because a silent install never
runs it.

## v2.2.3.0 — it can go and look for itself

The library used to be something only the app could add to. Anything driving it could read what the
app had last seen and act on it, but a game installed five minutes ago was invisible until somebody
opened the app and let it scan. This closes that.

**The command line can scan Steam**, and writes what it finds into the same library the app loads,
so a game found that way is a game the app has. Steam and nothing else: every other library scan
reaches a different launcher's files, and one of them installs copies whose dlls must never be
swapped at all, so a command that quietly walked all of them would be doing considerably more than
its name says.

**Games added to Steam by hand are found now**, by the app as well as by the command line. Steam
never writes an install manifest for one of those, so the scan that reads those manifests had no
way of seeing them at all - they are read from Steam's own shortcuts file instead. They arrive
carrying the app id Steam's library page uses, which is what lets anything looking at a game page
match it to the right game. Their cover art comes from the art Steam is already showing, since a
shortcut has no store page to fetch one from.

There is a rule about which folders may be scanned, and it is doing real work. A shortcut's folder
is whatever somebody typed, and the fallback when there is none is the folder of its executable -
which for a shortcut that launches a script is the Windows system directory, because the executable
is cmd. Detecting a game's dlls walks its folder and everything below it, so accepting one of those
would set the app walking the whole of Windows, and finding upscaler dlls in there that belong to no
game and must not be swapped. Drive roots, the Windows directory, and the folders that hold many
programs rather than being one are refused outright; anything inside them is fine, because plenty of
games live under Program Files.

**And the command line reports its own version now.** It had none, so it was built as 1.0.0.0 and
2.2.2.0 shipped it that way - a 1.0.0.0 executable sitting in the install folder of a 2.2.2.0 app,
telling anybody who read its file properties something that was never true of any release. The
consistency tests cover it now, along with the four other places the version is written by hand.

## v2.2.2.0 — a way in for other things

Nothing moves in the app itself. It gains a second executable, installed beside it, so that things
which are not the app can perform a swap without reimplementing what a swap is allowed to do.

**`dlss-swapper-cli.exe` now ships with the app**, in the install folder and in the portable zip.
It lists what is installed and what each dll could move to, swaps, and restores, answering in JSON
so a script can read it. It performs no swap of its own: it loads what the app loads and calls the
same methods the buttons call, so the rules hold without being restated — the files a type owns, the
transactional write and its rollback, the saved original that is never overwritten, pins, and the
version ranking FSR breaks if you rank it by file version. A second implementation of those in
another language would drift, and the way drift shows up is wrong files written into a game folder.

**It is built to be driven rather than watched.** stdout is always one JSON object, failures
included, so a caller reads one thing and checks `ok` instead of parsing prose out of an exit code;
diagnostics and stack traces go to stderr and never mix into it. Every reply carries a
`contractVersion`, and a caller is meant to refuse a version it does not know rather than guess at a
field that may have moved — the two ship separately and will disagree eventually, and misreading a
reply ends in a swap nobody asked for.

**And it asks Windows for no console.** A console program started by something that has no console
of its own has one made for it, which is a terminal appearing on screen for as long as the command
runs — nothing about the command being run changes that. This is a windows subsystem program
instead, and attaches to the calling terminal only when run by hand with its output not already
redirected. That is what makes it usable from inside another application, a Steam client plugin
being the reason it exists.

The portable zip carries its own copy, built the way the portable app is, so it reads the library
sitting beside it rather than the one an installed app keeps in AppData.

## v2.2.1.0 — it keeps what it cannot replace

A new dll type, and two places the app was choosing something other than the truth. The middle one
is the reason to take this release.

**The app no longer deletes your saved original.** When a dll changed outside the app — a game
update, or a tool that writes dlls into game folders — the scan deleted the copy it had kept of the
version the game shipped with. Upstream's reason is real, that a game which updates its own dll past
the version you swapped to should not read as a downgrade, but the price was the one file *restore*
exists to put back, and nothing brings it back: not a rescan, not a reinstall, not verifying the
game's files. A single scan of one library took 29 saved originals across 13 games. The copy stays
now, and the confusion it was avoiding is answered with words instead — every surface that offers a
restore already names both versions first, and the upscaler row now names the saved original
whenever it differs from what is installed: *"v2.0.2.68 installed, which is the newest — the saved
original is v2.0.1.41"*. Nobody restores without being told what they get, so nothing has to be
destroyed to keep them from being surprised.

**DLSS Neural Rendering is recognised**, alongside DLSS, Frame Generation and Ray Reconstruction.
It has leaked rather than shipped, so there is no list to download and no upstream manifest key:
versions appear once you import the dll yourself, and the page says exactly that instead of
offering a Refresh button that could never produce anything.

**And it no longer invents an original for a dll that has none.** The automatic backup reads the
file it finds in a game folder as the version the developer shipped, which is true of every
released upscaler and false of one that is only there because somebody installed it. On first sight
of Neural Rendering it duly saved six "originals" that were copies of the injected file, claimed
protection it did not have, and spent 0.93 GB doing it. A dll no game ships is now left alone, and
is not reported as missing a copy it could never have.

## v2.2.0.0 — the app closes the loop

A feature release, and the first with features of this fork's own rather than repairs and
refinements of what was there. One thesis runs through all five: the app's job used to end at the
file write, while the user's job ends when the game runs well — these close the gap.

**Restore originals, for the whole library.** The sidebar has always said "Originals kept for 24 of
24 games"; acting on that promise meant opening every game one at a time. One toolbar button now
puts everything back — scoped to the games shown like every bulk action, skipping games marked
leave alone like every bulk write, and confirming with every dll named: what each is now, what it
goes back to, in a list that scrolls. For the moments that call for a clean slate: a driver
rollback, a support thread, handing the machine on.

**The app says so when a game throws a swap away.** A game update overwrites the dll you swapped
in, and the scan that notices also deletes the stranded backup — so the swap and the way back both
vanished silently, recorded only in a history dialog nobody opens. A warning bar on the games page
now names each game and dll it happened to. Closing the bar acknowledges exactly what it showed;
a swap undone next month reopens it.

**A dll can be pinned where it is, with the reason written down.** You roll a game back because the
newest build ghosts, and nothing remembers — so next month's update-all offers the bad version
again. "Never update this game" existed but is all-or-nothing. A pin holds one dll: no batch moves
it — not an update run, not a restore run — while the picker on the game's own page always can.
The row says it in words: *v310.1 installed and pinned there — v310.7.129 exists — newer builds
ghost in this game*, the last clause being whatever you wrote when you pinned it.

**The version wall gained a curated lane.** A hundred near-identical numbers, and the knowledge of
which two matter lived on forums. A Recommended group now leads the list on the Upscalers page and
in the picker, each entry carrying its why — the 310.x transformer line, and 3.8.10 as the last
CNN build and the fallback when the transformer misbehaves. Every claim names an exact build, and
a test fails the build if an entry points at a version the shipped manifest does not carry.

**Launch, then restore originals.** The swap you do not want to still be there next week: play the
session swapped, and the shipped versions return the moment the game closes. The confirmation
names every dll before anything is agreed to; a strip narrates the watch in words; and every
uncertain path — the app closing, the game never starting, the watch stopped — leaves the files
exactly as they are, which is where every game is without this feature.

Also: the revert summary now has a sentence for every count. "Restored 4 dlls across 1 games" is
gone, and the one-dll case — which resolved a resource key nothing defined and showed an error in
the exact case singled out for better wording — reads properly, with tests holding every
concatenated key to the resources for good.

## v2.1.0.0 — the app meets you halfway

A user-experience release: two adversarial review passes over the whole app, every finding either
implemented and verified on screen, or declined with the reason recorded in the commit.

**Flows finish what the press asked for.** Swapping a not-yet-downloaded version downloads and then
swaps, in one motion. The dll picker gets the search box and release-line groupings the Upscalers
page already had, and a selection the filter hides disarms Swap. Reset all lists every dll it will
touch — what each is now and what it goes back to — before asking for a yes. Version rows on the
Upscalers page offer Download in the dialog the row opens, instead of hiding it in an overflow menu.
Exports end with "Show in folder".

**First launch is honest.** The page says "Looking for your games…" while the first scan runs instead
of offering to start it; empty filter tabs state their truth instead of a blank canvas; the anti-cheat
note appears before your first swap rather than over the loading screen; adding a game by hand shows
one note, not two stacked ones.

**Settings speak one language.** The SteamGridDB key is validated before it is saved, with the API's
own answer under the box — a mistyped key used to be saved silently and fail every later search. The
DLSS developer options got sentences; ignored paths say what ignoring means, with Remove visible; the
dll-list toggles sit together; titles share one style.

**It is faster where you feel it.** The game list appears before cover art finishes loading; grid
covers decode at the size they are drawn; unchanged covers are a cache hit across the session instead
of a fresh disk read on every scroll; Refresh walks your dlls without re-downloading every cover.

**And it stops guessing.** A failed update check says it could not reach GitHub instead of "no new
updates"; one malformed Xbox config no longer removes every other Xbox game from the list; a failed
scan no longer hides the game it failed on.

## v2.0.0.0 — a version number of its own

A small release. Its reason for existing is the version number, and one thing that number was
getting wrong.

**The installer was naming the wrong release.** The version is written in four files by hand and
only two of them were ever bumped, so the 1.2.6.0 build stamped **1.2.5.1** into its file
properties and into the entry it writes to *Add or remove programs* — the place you look to see
what you have installed. Fixed, and a test now reads all four files and fails when they disagree,
so it cannot happen quietly again.

**This fork counts its own versions now**, starting here. It used to carry upstream's version with
its own count in the fourth part, which collided the day upstream released a 1.2.6.0 of its own —
two different builds wearing one number, and this fork's reading as *older* than an upstream
release it already contained all of. Nothing about the app changes; the number just stops being
ambiguous.

Also in this build:

- **DLSS 310.7.129**, in both the served manifest and the copy bundled with the app. Upstream
  updated only the first of those in their own release, so a fresh install here starts with the
  newer list rather than waiting to fetch it.
- **Upstream's Japanese retranslation**, minus the sixteen strings it carries for UI this fork
  removed. Better wording throughout, and one real correction: the "NVIDIA recommended" preset had
  been labelled "always use the latest version".
- **A zip that tries to write outside the import folder is refused on any operating system**, not
  only on Windows. The check asked the running platform what counted as a path separator, which
  meant the rule was true where the app ships and quietly false everywhere it was tested.

## v1.2.6.0 — keeping your original dlls

Forked at upstream v1.2.5. Takes upstream's DLSS 310.7.128 entries and its DLSS D Preset F.

Most of this release is one theme: several ways the app could destroy the copy of a dll your game
shipped with — the file that lets you put a game back the way it came. None of them announced
themselves, and most needed nothing unusual to happen.

### Your saved originals

- **Ignoring a folder no longer deletes what is inside it.** Every launcher's scan skips a game in an
  ignored path, and every launcher then deletes the games its scan did not return, assuming they were
  uninstalled — which deletes their saved originals. So adding an ignored path destroyed the original
  dll of every game underneath it on the very next refresh. The game still leaves the app; the files
  stay, and un-ignoring the path finds them again.
- **A scan no longer invents an original.** When an installed dll stopped matching the version on
  record, the scan deleted the saved original and then immediately wrote a new one from whatever was
  installed at that moment — which could be a dll the app itself had swapped in and failed to record.
  Reverting would then have restored the swapped dll and reported success. The row now says plainly
  that there is no saved original, and "Save a copy" takes a fresh one when you ask.
- **"Save a copy" covers every location of a dll.** A game shipping the same dll in two folders had
  the first copied, the second skipped, and success reported — and the row then read as protected
  while that second location had nothing saved anywhere. The same wrong question was being asked in
  four places, so the list, the row and the sidebar all agreed.
- **Deleting a saved original is written down before the file goes**, so an interrupted scan can no
  longer destroy the copy and the note that it did.
- **A failed swap removes a dll it created.** When a target file was missing, the swap wrote one and
  could not undo that, so a swap that failed part way left a dll in your game folder while reporting
  that nothing had changed.

### Your settings and your imported dlls

- **An interrupted write no longer costs you either.** Settings, the imported dll list and the cached
  update check were each written by emptying the real file first, so a crash, a power cut or a full
  disk left nothing behind. Settings were then silently replaced with defaults — api key, ignored
  paths, library order, language, theme. The imported dll list disabled importing permanently, with
  the dlls still on disk and nothing left saying what they were. All three are now written beside the
  real file and moved over it, so it is either entirely the old one or entirely the new one.
- **A settings file that cannot be read is left alone.** A file held open for a moment by an
  antivirus used to read as "no settings yet" and get overwritten. Being unreadable and being absent
  are now different answers.
- **Imported dlls survive upgrading.** A dll that existed only in your imported list was skipped by
  the upgrade to the current layout, then read as missing and removed — it disappeared from the
  library on the first launch after updating, silently. Only genuinely custom dlls could hit this,
  which are the ones that cannot be downloaded again.
- **A zip that writes outside the import folder is refused.** Entry names inside a zip are chosen by
  whoever made it, and are not always a plain file name.

### Things that were not true

- **A game with no upscalers says so** instead of "Up to date", which was a claim about DLSS being
  current in a game that has no DLSS.
- **The update count clears when the batch finishes.** "Review 13 updates" stayed on screen over rows
  that all read up to date, and then did nothing when pressed.
- **Searching survives clicking a filter tab.** The box kept your text over a list that had stopped
  honouring it.
- **Update prompts come back.** Closing one update dialog suppressed every future release
  permanently — the check asked whether it had prompted before and answered the question backwards.
- **Two games from different launchers that share an id are two games.** Owning a Steam game and a
  Ubisoft game with the same number made one silently overwrite the other's name and install path.

### Cover art

- **Apply cannot run twice.** Pressing it again re-ran games already done, overwriting the backup of
  your own cover with the one the scan had just written — and undo then deleted your cover outright.
- **Stopping part way keeps undo**, and says how many were applied. It used to say nothing at all and
  hide the button, then delete the backups when the dialog closed.
- **Undo says how many it actually put back** rather than always claiming success.
- **A stalled search gives up** instead of sitting silent, and a scan that is stopped keeps what it
  found rather than throwing it away.

### Interface

- **A game opens over the list rather than instead of it.** Closing it leaves the list exactly where
  it was — same scroll position, same search, same tab — so looking at several games in a row is no
  longer a round trip through a list that resets each time. Escape, the back button and clicking away
  all close it.

### Under it

- The library is no longer rewritten to the database on every launch — measured at 25 writes per
  start before, none now unless something actually changed.
- Setting a game's title during a scan could throw and take the rest of that launcher's scan with it,
  so a game renamed or moved to another drive stopped the whole library being scanned.
- The installed build walked its own dll cache on every launch to refresh a number in Windows' Apps
  list. Once a week now.

## v1.2.5.1 — interface redesign

Forked at upstream v1.2.5.

### The app says things in words

- **Vendor-coloured badges are gone entirely.** A game's state was carried by colour, which is
  unreadable to anyone who cannot separate red from green. Every state is now a glyph plus a
  sentence, and no meaning anywhere in the app depends on colour alone.
- **A dll version row says where the file is** — on disk, imported, not downloaded, or downloading —
  instead of leaving it to be worked out from which buttons the row happened to show.
- **Every setting says what it does.** Four rows had a title and a control and nothing in between;
  four more had a description that explained the subject without ever stating what the setting
  changed. "Allow Untrusted" described how signature checking works and recommended a value without
  ever saying what turning it on permits.
- **A game's page has one row per upscaler**, each naming the installed version, whether anything
  newer is available, whether the original was kept, and whether the dll was found in more than one
  folder. It was nine dropdowns with that information split between a label and nowhere.
- **A preset that cannot be set says which of the three reasons that is** — no NVIDIA driver, no
  driver profile for this game, or the driver refusing access. All three were collapsed into the
  single word "Not supported".

### Nothing is written without showing you what it will write

- **The update preview sheet** lists every file a run will replace, with its current and new version,
  and lets you deselect any of them.
- **The done strip keeps what it wrote**, so undo puts that batch back and nothing else.
- **"See what changed"** names what each replaced file was and what it became. The version a file was
  before is only knowable before it is overwritten, so it is recorded as the run happens.
- A game's own **Update all dlls** now goes through the same sheet and strip rather than a modal
  confirmation offering only a count.

### Finding things

- **The upscalers page is searchable** across 200-plus versions in nine engines, by version, label or
  file hash. The engine counts follow the search, so the column says where the matches are.
- **Versions are grouped by release line**, newest three lines separately and the rest rolled up,
  rather than a flat wall of near-identical numbers.
- **Each version says how many games use it**, and the count is a button: click it to see which
  games. That is the question worth answering before deleting a file, and the page could not answer
  it at all.
- **Launcher sections fold**, and unfolding one keeps its heading where it was rather than putting
  its games off the top of the screen.

### Cover art

- **A game's page can find its own cover.** It could always take a custom image you already had —
  a button and a drag target — but not help you find one. It now searches SteamGridDB: pick which
  game it is from the matches, then pick a cover from that game's art. Nothing is written until you
  choose one, so a search that finds nothing leaves the cover you had alone and says so, and
  choosing a file from your own disk stays available throughout.
- **The whole library at once.** "Find covers" scans every game shown and proposes one only where
  the name matches beyond doubt. Everything less certain is **listed by name with a reason** rather
  than counted, and can be picked from without leaving the dialog — clicking one opens the same
  picker a game's page uses and returns to the list once a cover is set, so the list can be worked
  down. A batch can be put back in one press.
- **Only the shape that fits.** The app draws one 400x600 portrait, so only portrait art is ever
  fetched. Art flagged as adult, and art flagged for epilepsy, is never offered.
- **A key is your own.** SteamGridDB issues keys to people rather than to applications, so the
  setting says where to get one and links to the page that makes it. Without a key nothing else in
  the app changes.

### Counts that were wrong

Each of these was a count disagreeing with the list it described.

- **"All games" counted hidden games** and then did not show them. Steam and Xbox mark their own
  non-game entries hidden on sight, so the gap was widest on the libraries most people have.
- **The engine counts included debug files the list was hiding** — DLSS read 107 over a list of 88 —
  and the sidebar's total read 213 over a column summing to 186.
- **"Review N updates" counted one set of games and opened onto another** once a dll filter was on.

### Fixed

- **The per-game window leaked.** It added itself to the main window and was only ever hidden, so
  every game opened left one behind, still holding its model, its game and its cover bitmap.
- **Menus opened outside the application window** — the View menu rendered on the desktop above the
  app — and, once constrained, were transparent enough to read the toolbar through. Both fixed.
- **"1 upscalers not in this game"** had no singular form.
- A duplicate resource key and 30 dead ones removed, 300-odd entries across 24 translation files.

Three more were found by running a real swap on a real game, and all three were introduced by the
work above rather than inherited — they are listed because the first is exactly the kind of thing
this redesign exists to prevent, and it survived until something actually wrote to a disk.

- **A game's rows went stale the moment a swap succeeded.** The file on disk changed and the row
  kept describing the version it had before, until the page was reopened.
- **"Update all dlls" on a game's page opened nothing.** It navigated back and then raised the
  preview sheet, but the sheet lives on the page it had just left.
- **"See what changed" and the sheet that offered the change disagreed on format** — `310.6.0.0 →
  310.7.0.0` against `310.6 → 310.7`. One fact in two shapes reads as two facts.

### Shape

- **Three sections in a sidebar**: Games, Upscalers, Settings, each with a count.
- **The games page is one filtering surface.** A separate "Filter" dialog held a worse copy of the
  Hidden tab and a view option; both are gone, grouping moved into the View menu, and the toolbar
  dropped from six buttons to four, none of which decides which games are shown.
- **A game is a page rather than a dialog**, with the game's name as its header. As a dialog it had
  no title at all, and it existed inside a workaround for the fact that a dialog cannot open another
  dialog — which it did six times.
- **Grid cards put their caption below the art** with an accent rule, instead of a gradient darkening
  the cover to keep white text legible over whatever the publisher shipped. Cards gained a title.
- **Settings is two columns**: what the app does and how it looks on the left, what it found and what
  it is on the right.
- **A theme and accent picker**, with the accent applied live.

### Under it

- The swap engine, version ranking and dll registry moved into a **pure `net10.0` core project** with
  no WinUI dependency.
- **A new kind of dll cannot arrive unnoticed.** The list of swappable dlls is generated by a
  separate repository and was copied into this one by hand. A key the app has no entry for is kept
  as-is rather than dropped, so that an imported manifest is never corrupted when it is saved —
  which also meant a newly supported upscaler could be carried along silently and offered to nobody.
  A test now asserts that every manifest key with anything under it is one the app handles, and a
  daily job refreshes the bundled copy and raises a pull request saying whether what arrived is new
  versions of something already supported or something new that needs the app taught about it.
- **555 tests** across the two suites, run in CI on every push. The rules that decide what a page
  shows are tested rather than the pages themselves — counts and the lists they describe come from
  one function, and a test asserts they cannot diverge.
- **The write path has been run end to end on a real game**, not only against forced state: swap
  down to an older version, watch the row and the games list both notice, update back through the
  preview sheet, and confirm the file on disk at each step. That run is what found the three bugs
  above. Tests over forced state had all passed.
- Contrast is **measured** against the lightest surface each text level can land on, including the
  accents used as text. Several tokens are well above the values the design specified because those
  values did not pass.

### Not carried over

- **The updater points at this fork**, so it offers this fork's releases rather than the original's
  — which would otherwise replace this build with one that does not have any of these changes.
- **About names both repositories** — this fork for the code and for interface reports, the original
  for everything else and for credit.
