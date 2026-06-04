# neon-companion

Source-available Unity client for connecting to personal AI agents with avatar visualization.

## About the Project

**neon-companion** is a cross-platform desktop/mobile application that allows you to chat with your own AI agents through an OpenAI-compatible API. The app supports custom backends, multiple providers, chat history, 2D/3D avatar visualization, and a native markdown rendering engine.

This project is intended as a personal companion shell for self-hosted agents, with room for community forks and extensions.

## Current Status

- ✅ Text chat with custom OpenAI-compatible providers
- ✅ Multiple providers support + switching
- ✅ Model auto-discovery from provider endpoints
- ✅ Model switcher in chat topbar (per-session model selection)
- ✅ Chat sessions and history
- ✅ Connection to self-hosted agents (tested with Hermes + Grok)
- ✅ Native markdown rendering (SelectableMarkdownElement) with syntax highlighting
- ✅ 2D avatar rendering with sprite-sheet motion packs
- ✅ Fixed MVP 2D action set: `idle`, `thinking`, `talking`, `listening`, `smile`, `confused`
- ✅ Custom NeonDropdown UI component
- ✅ Chat attachments (images, drag-and-drop, clipboard paste)
- ✅ Hermes session routing + inventory integration
- ✅ Agent approval system (WebSocket RPC)
- ✅ Window chrome service (borderless desktop window)
- ✅ Chat commands (/help, /clear, /new, /system, /temp, /tokens)
- ✅ Stop button (cancel generation)
- ✅ Export chat as markdown
- ✅ Token counter + response time in bubbles
- ✅ Reasoning/thinking block display
- ✅ Tool call visualization with emoji + expandable diffs
- ✅ Plugin/extension system
- ✅ Contributor docs + donate system
- ✅ Cyberpunk splash screen
- 🚧 Cross-platform builds (Desktop + Mobile)
- 🚧 Voice input/output pipeline
- 🚧 3D avatar architecture (Avatar3DLoader, Avatar3DRenderer)
- 📋 VR support planned (M4+)

## Features

- Connect any OpenAI-compatible API (including self-hosted)
- Switch between providers directly in the app
- Auto-discover available models from provider endpoints
- Per-session model switching from chat topbar
- Native markdown rendering with syntax highlighting for 15+ languages
- Diff-fenced code blocks with +/-/@@ coloring
- Agent approval flow for tool calls
- Drag-and-drop files into chat
- Clipboard image paste
- Chat export to markdown
- Stop/cancel in-progress generation
- Chat commands (/help, /clear, /new, /system, /temp, /tokens)
- Persistent chat sessions with folders
- Modern dark UI built with Unity UI Toolkit
- Design token system (CSS variables throughout)
- 2D sprite-sheet baseline with continuous states + one-shot reactions
- Plugin system for extensions (DLL-based)
- Customizable avatars with persona/instructions
- Themes

## Getting Started

### Requirements

- Unity 6 (6000.4 or newer)
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

Current version: `0.3.0`.

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
- [UI Flows](docs/05_UI_Flows.md)
- [Data Models](docs/06_Data_Model.md)
- [Cross-Platform](docs/07_CrossPlatform.md)
- [Build & Deploy](docs/08_Build_and_Deploy.md)
- [Roadmap](docs/09_Roadmap.md)
- [Changelog](docs/11_Changelog.md)
- [Feature Tracker](docs/12_Feature_Tracker.md)
- [Avatar Motion Research](docs/13_Avatar_Motion_Research.md)
- [Provider Adapter](docs/14_Provider_Adapter.md)
- [Platform Architecture](docs/16_Platform_Architecture.md)
- [iOS Platform Architecture](docs/17_iOS_Platform_Architecture.md)

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

## Android Support

Android build is in active development (M3).

- Full voice support (TTS + speech recognition via system intent)
- Native file picker
- Runtime permissions
- Safe area + mobile UI adaptations
- See `AGENTS.md` → Android Build section for build instructions
- See `docs/16_Platform_Architecture.md` for architecture

Current status: Code complete. Awaiting device testing and runtime fixes.
