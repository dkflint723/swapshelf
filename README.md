<p align="center">
 <img width="150px" src="docs/logo_250.png" align="center" alt="Swapshelf" />
 <h2 align="center">Swapshelf
</h2>
 <p align="center">Swapshelf downloads, verifies and keeps a library of upscaler dlls — <strong>DLSS</strong>, <strong>FSR</strong>, <strong>XeSS</strong> and <strong>XeLL</strong> — and swaps them into your games, so a game can move to a newer version, or back to the one it shipped with, without the game needing an update.</p>
</p>

> [!IMPORTANT]
> **Swapshelf was called DLSS Swapper (dkflint723 fork) until 3.0.0.0.** It began as a personal fork
> of [beeradmoore/dlss-swapper](https://github.com/beeradmoore/dlss-swapper) and has diverged a long
> way since — a command line, a Steam plugin, its own release line, and behaviour that deliberately
> differs from upstream in places. Carrying that project's name no longer described what this is, so
> it does not.
>
> **Swapshelf is not affiliated with NVIDIA, or with the original DLSS Swapper.** If you want the
> original, it is [here](https://github.com/beeradmoore/dlss-swapper/releases) — that one is
> official and signed. Builds here are **unsigned**, so Windows will warn before running one. Issues
> with Swapshelf belong [on this repository](https://github.com/dkflint723/swapshelf/issues);
> anything about the original belongs upstream.

> [!WARNING]
> Be aware of malicious sites claiming to be DLSS Swapper. See the original project's
> [official links](#official-links) for what is affiliated with it. **Swapshelf is not one of them**
> and does not claim to be — it publishes unsigned builds from this repository only.

> [!NOTE]
> **Upgrading from DLSS Swapper (dkflint723 fork)?** The installer removes the old copy for you, and
> your library moves itself: everything under `%LOCALAPPDATA%\DLSS Swapper` — the database with its
> pins and history, the downloaded dlls, and the copies of what each game shipped with — is moved to
> `%LOCALAPPDATA%\Swapshelf` the first time Swapshelf runs. Nothing is copied and nothing is
> deleted; if the move cannot be done the old folder keeps being used, and it tries again next
> launch.

<p align="center">
    <a href="https://github.com/dkflint723/swapshelf/issues"><img alt="Issues on this fork" src="https://img.shields.io/github/issues/dkflint723/swapshelf?color=0088ff&label=fork%20issues" /></a>
    <a href="https://github.com/dkflint723/swapshelf/commits/main"><img alt="Last commit to this fork" src="https://img.shields.io/github/last-commit/dkflint723/swapshelf?label=fork%20updated" /></a>
    <a href="https://github.com/beeradmoore/dlss-swapper/releases"><img alt="Latest release of the original" src="https://img.shields.io/github/v/release/beeradmoore/dlss-swapper?label=original%20release" /></a>
</p>

<p align="center">
    <a href="https://github.com/beeradmoore/dlss-swapper/releases">Download the original</a>
    ·
    <a href="https://github.com/dkflint723/swapshelf/issues/new">Report something about this interface</a>
    ·
    <a href="https://github.com/beeradmoore/dlss-swapper/issues/new/choose">Everything else, upstream</a>
</p>

<p align="center">
    <a href="./readmes/readme_ca.md">Català</a>
    ·
    English
    ·
    <a href="./readmes/readme_es.md">Español</a>
    ·
    <a href="./readmes/readme_ja-JP.md">日本語</a>    
    ·
    <a href="./readmes/readme_pt-BR.md">Português BR</a>
    ·
    <a href="./readmes/readme_tr-TR.md">Türkçe</a>
    ·
    <a href="./readmes/readme_zh-Hans.md">简体中文</a>
    ·
    <a href="./readmes/readme_zh-TW.md">繁體中文</a>
</p>

<p align="center">
    <img src="docs/images/fork/games-page.png" alt="The games page in this fork: a sidebar with counts, filter tabs, and cards that each name their state and which upscalers the game has" />
</p>

## What is different in this fork

The app does the same job; it explains itself while doing it. Everything below replaced something
that worked but could only be read by someone who already knew what it meant. [CHANGELOG.md](CHANGELOG.md)
has the full list.

- **State is words, never colour.** Vendor-coloured badges are gone. Every game and every dll says
  what it is in a sentence, with a glyph beside it — the app is usable by someone who cannot tell
  red from green, which the badges assumed you could.
- **Nothing writes to a game without showing you what it will write.** Updating opens a sheet
  listing each file, and the strip that follows keeps what it wrote so it can put that batch back.
- **A game has its own page**, one row per upscaler, each saying which version is installed, whether
  anything newer exists, whether the original was kept, and whether the dll was found twice. It was
  nine dropdowns and eight unlabelled icon buttons.
- **The upscalers list is searchable**, grouped by release line, and says how many games use each
  file — the one question worth answering before deleting one, and you can click the number to see
  which games.
- **Counts agree with the lists they describe.** Several did not: "All games" counted hidden games it
  would not show, and the engine counts included debug files the list was hiding.
- **Every setting says what it does**, contrast is measured against the surface it lands on rather
  than eyeballed, and menus stay inside the window.

### It checks before it writes, and keeps what it cannot replace

Since 3.0.6.0. Most of it only shows when something has already gone wrong, which is when it has to
work.

- **A swap that cannot work is refused, with the reason.** The game is still running, the dll was
  built for a different processor than the one it would replace, or it is not signed by the company
  that makes it — NVIDIA, AMD or Intel. Any valid signature used to be enough.
- **Two copies of what each game shipped with.** One beside the dll, as before, and one in
  Swapshelf's own folder, where a game's verify or repair cannot delete it. That costs disk — 20 to
  170 MB for each saved dll — and a copy is never made that would leave less than 1 GB free.
- **Restore puts back the original, and only the original.** It checks the saved copy still matches
  what was recorded when it was saved, and it asks before writing over a dll that changed after it
  was swapped, whether by a game update, a mod or a fix by hand.
- **A swap cut off part way is put back.** Each swap is written down before the game folder is
  touched, so the next launch can undo one interrupted by the app closing, a crash or a power cut.
- **Each version says how much is known about it** in the game you are about to put it in: Known
  good, Likely fine, Experimental or No evidence yet, with the reason, as a shape and words.
- **The anti-cheat note is asked per game.** It names the anti-cheat it found, and Cancel is the
  default.
- **Diagnostics are safe to paste into a public issue.** Folder paths become placeholders; versions
  and hashes stay as they are.

## What game libraries are supported?

- [Steam](https://store.steampowered.com/)
- [GOG](https://www.gog.com/en/)
- [Epic Games](https://store.epicgames.com/)
- [Ubisoft Connect](https://www.ubisoft.com/)
- [Xbox App](https://www.xbox.com/)
- [Battle.net](https://shop.battle.net/)
- Manually added via the `Add Game` button.

## Why would you want to change the DLSS dlls in your game?

See [this](https://youtube.com/clip/UgzYyeox3s7jFJZAvYF4AaABCQ) clip, or better yet just watch the entire video ([Lego Builder's Journey Ray Tracing Showcase + DLSS 2.2 Upgrades Analysis](https://www.youtube.com/watch?v=dtbqJXb1UDw)) from Digital Foundry. DLSS 2.2 discussions start at 11:40.

## Please note

This tool does **NOT** allow you to add DLSS to games that don't support it.

This tool does **NOT** guarantee that swapping DLSS dlls will:

- Improve DLSS performance.
- Reduce DLSS artifacts.
- Give a crash free experience.

In many cases you may fix some issues, in other cases you may prevent a game from launching (until you restore your original dll, provided in the tool).

Happy experimenting. As my university professor once said,

> The good thing about computer [science] is we will never die wondering 'What if...?'

Please, come and share your DLSS experience over in [r/DLSS_Swapper](https://www.reddit.com/r/DLSS_Swapper/).

## How do I get it?

**This fork publishes unsigned builds** on its [releases page](https://github.com/dkflint723/swapshelf/releases)
— an installer and a portable zip. They carry no certificate, so SmartScreen will warn on first run
and you will have to click through it. That is the cost of a fork without code signing, and it is a
good reason to prefer the original unless you specifically want this interface.

Each release lists the SHA-256 of both files — the same digests GitHub shows beside them — so a
download can be checked by hand, and the in-app updater checks them before it runs anything it
downloaded.

Or build it yourself with the .NET 10 SDK:

> dotnet build "src\DLSS Swapper.csproj" -c Release -p:Platform=x64

**The original** has proper releases, signed, on its [GitHub releases](https://github.com/beeradmoore/dlss-swapper/releases) page, or:

> winget install --id=beeradmoore.dlss-swapper -e

That winget package is the original project, not this fork. Those are the only official places to get
DLSS Swapper.

## It would be cool if DLSS Swapper could...

Create a [feature request](https://github.com/beeradmoore/dlss-swapper/issues/new?template=feature_request.yml).

## How can I contribute?

More info on this soon.

## Minimum System Requirements

| Requirement | Description                           |
| ----------- | ------------------------------------- |
| OS          | Windows 10 64-bit (20H1, build 19041) |
| GPU         | Any                                   |

## Official links

Of the original project, which is where everything except this fork's interface work belongs:

- GitHub: https://github.com/beeradmoore/dlss-swapper/
- Twitter: https://twitter.com/dlss_swapper
- Reddit: https://www.reddit.com/r/DLSS_Swapper/

If you have found an other accounts or sites claiming to be DLSS Swapper, please ignore them (or better yet, [file an issue](https://github.com/beeradmoore/dlss-swapper/issues/new?template=other_issue.yml) and let us know)


## Sponsors

<table>
    <tr>
        <td style="width:50px">
            <img src="docs/images/sponsors/signpath.png" width="50" height="50" alt="SignPath">
        </td>
        <td>
            <strong>Of the original project.</strong> Free code signing on Windows provided by <a href="https://signpath.io/">SignPath.io</a>, certificate by <a href="https://www.signpath.com/solutions/for-open-source-community-foundation">SignPath Foundation</a>. Builds from this fork are <strong>not</strong> signed and carry no certificate.
        </td>
    </tr>
</table>