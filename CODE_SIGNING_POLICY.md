# Code signing policy

Free code signing provided by [SignPath.io](https://about.signpath.io/), certificate by [SignPath Foundation](https://signpath.org/).

## What is signed

Only binaries built by this repository's GitHub Actions workflow ([.github/workflows/build.yml](.github/workflows/build.yml)) from the public source code in this repository are signed:

- `DLSS5Master.exe` and `DLSS5Master.dll`
- the installer `DLSS5Master-Setup-<version>.exe`

Third-party files are never signed with this project's certificate. That includes the Microsoft .NET / Windows App SDK runtime files in the installer, and every component the app downloads or copies into games: ReShade, RenoDX, the bundled MFGAdaUnlock add-on, OptiScaler, DLSS5-Feeder and NVIDIA runtimes.

## Team roles

| Role | Members |
|---|---|
| Committers and reviewers | [@liongooder](https://github.com/liongooder) |
| Approvers | [@liongooder](https://github.com/liongooder) |

Every signing request is approved manually by an approver. Changes from outside contributors are reviewed by a committer before merging.

## Privacy

This program will not transfer any information to other networked systems unless specifically requested by the user or the person installing or operating it.

When you install a route or OptiScaler build, DLSS 5 Master downloads that component from its author's GitHub release page. These requests contain no personal data; GitHub sees your IP address, as with any download. To show game artwork, the app looks up game names or Steam IDs on Steam's public store search and downloads posters from Steam's image servers; nothing else is sent. The "Buy me a coffee" button opens buymeacoffee.com in your browser only when you click it. The app has no telemetry, analytics, accounts or update checks. Settings and logs stay on your PC in `%LOCALAPPDATA%\DLSS5Master`.
