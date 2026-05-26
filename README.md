# neon-companion

Source-available Unity client for connecting to personal AI agents with avatar visualization.

## About the Project

**neon-companion** is a cross-platform desktop/mobile application that allows you to chat with your own AI agents through an OpenAI-compatible API. The app supports custom backends, multiple providers, chat history, and 2D avatar visualization.

This is currently the **first working prototype**. Core chat functionality and custom provider connection are already implemented and usable.

This project is intended as a personal companion shell for self-hosted agents, with room for community forks and extensions.

## Current Status

- ✅ Text chat with custom OpenAI-compatible providers
- ✅ Multiple providers support + switching
- ✅ Chat sessions and history
- ✅ Connection to self-hosted agents (tested with Hermes + Grok)
- 🚧 2D avatar rendering with sprite-sheet action sets for low-end/mobile baseline
- 📋 Desktop-first 3D realtime avatar layer is planned separately
- 🚧 Cross-platform builds (Desktop + Mobile)

## Features (MVP)

- Connect any OpenAI-compatible API (including self-hosted)
- Switch between providers directly in the app
- Persistent chat sessions
- Modern dark UI built with Unity UI Toolkit
- Designed around a lightweight 2D sprite-sheet baseline, with optional desktop 3D realtime avatars later

## Getting Started

### Requirements

- Unity 2022.3 or newer
- .NET Standard 2.1 compatible environment

### Installation

1. Clone the repository:
   ```bash
   git clone https://github.com/isteelfelix/neon-companion.git
   cd neon-companion
   ```

2. Open the project in Unity.

3. Go to `Assets/Scenes` and open the main scene.

4. Build the project for your target platform (Desktop / Android / iOS).

## Versioning

Project version is tracked in the root [`VERSION`](VERSION) file and follows semantic versioning (`MAJOR.MINOR.PATCH`).

Current version: `0.2.0` (post-M2, pre-M4).

## Build From CLI

The project includes a Unity batch build pipeline:

- Unity entrypoint: `BuildScript.Build`
- Script wrapper: `scripts/build.sh`
- Output directory: `Builds/`

### Requirements

- Unity 2022.3+ installed
- Unity CLI binary available as `UNITY_PATH` env var or passed via `--unity`

### Build Commands

Build Windows x64:

```bash
scripts/build.sh --target windows --version 0.2.0 --unity "/path/to/Unity"
```

Build Linux x64:

```bash
scripts/build.sh --target linux --version 0.2.0 --unity "/path/to/Unity"
```

Build Android APK:

```bash
scripts/build.sh --target android --version 0.2.0 --unity "/path/to/Unity"
```

The script prints the built artifact path on success.

Build name format includes version and commit hash:

`<product>-v<version>-<commit>-<target>`

## Release Process

Use `scripts/release.sh` to produce all platform builds and publish a GitHub Release using `gh`.

```bash
scripts/release.sh 0.2.0 --unity "/path/to/Unity"
```

What it does:

1. Builds `windows`, `linux`, and `android` artifacts.
2. Creates annotated tag `v<version>`.
3. Generates release notes from commits since previous tag (or a default note for first release).
4. Creates GitHub release and uploads artifacts.

Optional custom release notes:

```bash
scripts/release.sh 0.2.0 --unity "/path/to/Unity" --notes /path/to/notes.md
```

GitHub Releases:

- https://github.com/isteelfelix/neon-companion/releases

## Connecting Your Own Agent

The app can connect to any OpenAI-compatible endpoint.

**Example configuration** (for Hermes agent):

- **Base URL**: `http://your-server-ip:8642/v1`
- **API Key**: Your agent API key (Bearer token)
- **Model**: `grok-4.3` (or any model your backend supports)

After adding the provider, select it in the Providers tab and start chatting.

## Documentation

More detailed information is available in the `docs/` folder:

- [Architecture](docs/01_Architecture.md)
- [MVP Features](docs/02_Features_MVP.md)
- [API Integration](docs/03_API_Integration.md)
- [Avatar System](docs/04_Avatar_System.md)
- [Data Models](docs/06_Data_Model.md)
- [Cross-Platform](docs/07_CrossPlatform.md)
- [Build & Deploy](docs/08_Build_and_Deploy.md)
- [Roadmap](docs/09_Roadmap.md)
- [Feature Tracker](docs/12_Feature_Tracker.md)
- [Avatar Motion Research](docs/13_Avatar_Motion_Research.md)

## Contributing

Contributions are welcome. Please check [Contribution Guide](docs/10_Contribution.md) for details.

## License

- **Code**: see [LICENSE](LICENSE)
- **Assets / UI art / models / branding**: see [ASSET_LICENSE.md](ASSET_LICENSE.md)

This repository is source-available: you may use it, modify it, fork it, and self-host it, but you may not sell it, repackage it as a paid product, or offer it as a commercial hosted service without permission.

## Author

Maintained by iSteelFelix with contributions from the community.

## Credits

- **iSteelFelix** — creator, owner, product direction, engineering
- **Neon** — AI companion, in-repo contributor, co-builder of `neon-companion`
