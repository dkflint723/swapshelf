# Code signing

Swapshelf's releases are unsigned. Every release body says so, and Windows SmartScreen says so
again before the installer runs. This page is what it would take to change that, what is already in
place, and what stands in for a signature until then.

## What is already in place

`.github/workflows/build-for-distribute.yml` carries the signing steps DLSS Swapper uses upstream:
the built `Swapshelf.exe` and `Swapshelf.dll` (installer and portable) and the finished installer
are uploaded as artifacts, submitted to [SignPath](https://about.signpath.io/) through
`signpath/github-action-submit-signing-request`, and the signed files are moved back into place
before packaging. The steps run only when `SHOULD_SIGN` is true, which needs all of:

| Setting | Kind | Meaning |
| --- | --- | --- |
| `SIGNPATH_API_TOKEN` | repository **secret** | The CI user's API token for the SignPath organization. |
| `SIGNPATH_ORGANIZATION_ID` | repository **variable** | The organization the project lives in. |
| `SIGNPATH_PROJECT_SLUG` | repository **variable** | The SignPath project for this repository. |

None of the three is set on this repository, so a tagged build takes the unsigned path that has
always existed for untagged builds. The organization and project used to be written into the
workflow and were **upstream's** (`dlss-swapper` in organization `9b913506-…`). They are
variables now, so that setting a token here can never submit this fork's binaries to somebody
else's signing project. The signing policy slug (`release-signing`) and the two artifact
configuration slugs (`installer-and-portable-raw-files`, `installer`) are still in the workflow;
they are names inside the project and are set up when the project is.

## Getting a signature

SignPath Foundation signs open-source projects at no cost. The application is at
<https://signpath.org/apply>, and asks for:

- the repository, its licence, and a short description;
- evidence the project is real and maintained (releases, activity);
- who the maintainers are, and that builds happen in CI from the repository (they do, in the
  workflow above);
- the artifact types to sign: for this project, Windows executables and the NSIS installer.

Once accepted, SignPath provides the organization id, a project, and a CI user whose token goes in
the secret. Set the three settings above under the repository's **Settings → Secrets and variables
→ Actions**, tag a release, and the workflow signs it. Nothing in the app changes: it verifies the
release digest it downloads (below) whether or not the file is signed.

The alternative is a certificate of one's own from a commercial CA and `signtool` in
`package/build_all.cmd`. That costs money every year, ties the identity to a person, and puts a
private key on the machine that builds releases; it is not the recommended route for a hobby fork.

## Until then: digests

A signature says who built a file. A digest says the file is the one that was published. The
second is what the app already relies on:

- **GitHub records a SHA-256 for every release asset**, exposed as `digest` in the releases API.
  The in-app updater (`GitHubUpdater`) reads it and refuses to run an installer whose bytes do not
  match - checked once after download and again immediately before it starts.
- **Release notes should carry the same digests**, so a person downloading by hand can check too.
  From the `package/Output` folder after `build_all.cmd`:

  ```powershell
  Get-ChildItem package\Output\*.exe, package\Output\*.zip | Get-FileHash -Algorithm SHA256 |
      ForEach-Object { "{0}  {1}" -f $_.Hash, (Split-Path $_.Path -Leaf) }
  ```

  Paste the output into the release body under a `## SHA-256` heading. The digests must match
  what the releases API reports for the uploaded assets; if they do not, the upload is not the file
  that was built.

- The SBOM the workflow produces (`Swapshelf-<version>.cdx.json`) describes what went into a
  build. It is uploaded as a workflow artifact, not attached to the release, and its absence never
  stops a release.

## What a signature would not do

Signing the app does not make swapped dlls trusted by a game or its anti-cheat, and it does not
change what the app checks about the dlls it swaps: those are verified against NVIDIA's, AMD's and
Intel's own signatures (`WinTrust.VerifyForVendor`) and the manifest's hashes, independently of
who signed Swapshelf.
