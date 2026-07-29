#!/usr/bin/env python3
"""Focused static checks for the Windows-isolated Companion display contract."""

import json
import pathlib
import re
import xml.etree.ElementTree as ET


ROOT = pathlib.Path(__file__).resolve().parents[1]


def read(path):
    return (ROOT / path).read_text(encoding="utf-8")


files = (
    "Assets/Scripts/Runtime/Platform/ICompanionWindowService.cs",
    "Assets/Scripts/Runtime/Platform/WindowsCompanionWindowService.cs",
    "Assets/Scripts/Runtime/Platform/CompanionPlayerRuntime.cs",
    "Assets/Scripts/Runtime/UI/UITK/CompanionWindowController.cs",
)
for path in files:
    assert (ROOT / path).is_file(), "missing " + path

protocol = read(files[0])
parent = read(files[1])
player = read(files[2])
controller = read(files[3])
voice = read("Assets/Scripts/Runtime/UI/UITK/VoiceController.cs")
main = read("Assets/Scripts/Runtime/UI/UITK/MainViewController.cs")
bootstrap = read("Assets/Scripts/Runtime/Core/AppBootstrap.cs")
factory = read("Assets/Scripts/Runtime/Platform/PlatformServiceFactory.cs")
settings = read("Assets/Scripts/Runtime/Data/Models/AppSettings.cs")
gallery = read("Assets/Scripts/Runtime/UI/UITK/AvatarGalleryController.cs")
chat = read("Assets/Scripts/Runtime/UI/UITK/ChatController.cs")
uxml_path = ROOT / "Assets/UI/Avatars/AvatarsView.uxml"
ET.parse(uxml_path)
uxml = read("Assets/UI/Avatars/AvatarsView.uxml")

snapshot_body = protocol.split("class CompanionDisplaySnapshot", 1)[1].split(
    "class CompanionWindowPreferences", 1
)[0]
for forbidden in ("ProviderConfig", "ChatSession", "apiKey", "secret", "SystemPrompt"):
    assert forbidden not in snapshot_body, "secret/session field leaked into snapshot: " + forbidden

for state in ("Idle", "Listening", "Thinking", "Speaking", "Stop"):
    assert "public const string " + state in protocol
assert "NamedPipeServerStream" in parent
assert 'Guid.NewGuid().ToString("N")' in parent
assert "--companion-player" in parent and "--companion-parent-pid" in parent
assert "Process.GetCurrentProcess().MainModule.FileName" in parent
assert "process.Kill()" in parent
assert 'type = "voice_start", text = _voiceText' in parent
assert 'type = "voice_clear"' in parent
assert "StartVoicePlayback(string text)" in protocol
assert "ClearVoicePlayback()" in protocol
assert 'case "voice_start":' in player
assert 'case "voice_clear":' in player
assert "LipsyncController.GetVisemeAt(_voiceText, charIndex)" in player
assert "LipsyncController.TextCharsPerSecond" in player
assert "_avatar3DService.Capabilities.hasLipsync" in player
assert "_avatar3DService.SetMouthShape(viseme.ToString())" in player
assert "private void ClearVoicePlayback()" in player
assert "_avatar3DService.ClearMouth()" in player
assert "public Action<string> OnVoicePlaybackStarted;" in voice
assert "_d.OnVoicePlaybackStarted?.Invoke(text);" in voice
assert "_companionWindowController.StartVoicePlayback(text);" in main
assert "_companionWindowController.ClearVoicePlayback();" in main
assert "_service.ClearVoicePlayback();" in controller

guard = bootstrap.index("if (CompanionProcessMode.IsPlayerProcess)")
assert guard < bootstrap.index("new DeviceSecretStore")
assert guard < bootstrap.index("new ChatSessionRepository")
assert guard < bootstrap.index("new ProviderConfigRepository")
assert "services.Register<ICompanionWindowService>" in bootstrap

assert "#if UNITY_STANDALONE_WIN && !UNITY_EDITOR" in factory
assert "return new StubCompanionWindowService();" in factory
assert "WsExTransparent" in player and "WsExNoActivate" in player
assert "SetTopmost" in player and "MoveToMonitor" in player and "BeginDrag" in player
assert "0x7B" in player and "0x11" in player and "0x10" in player
assert "clickThrough;" in protocol
assert "public bool clickThrough" in protocol
assert "public bool companionWindowClickThrough = false;" in settings
assert "Application.Quit()" in player and "_parent.HasExited" in player
assert "OpenAvatarSettings" in controller and "ReturnToColumn" in controller
assert "StopAvatarDisplay" in chat
assert "AvatarMotionStateChanged" in gallery

for field in (
    "companionModeEnabled",
    "companionWindowVisible",
    "companionWindowPinned",
    "companionWindowClickThrough",
    "companionWindowMonitor",
    "companionWindowScale",
    "companionWindowPositionX",
    "companionWindowPositionY",
):
    assert field in settings and field in controller, "missing persisted control: " + field

for element in (
    "companion-mode-toggle",
    "companion-visible-toggle",
    "companion-pinned-toggle",
    "companion-click-through-toggle",
    "companion-monitor-button",
    "companion-scale-slider",
    "companion-return-button",
):
    assert element in uxml and element in controller, "missing UI wiring: " + element

english = json.loads(read("Assets/Resources/Localization/en.json"))
russian = json.loads(read("Assets/Resources/Localization/ru.json"))
for key in (
    "companion.window.title",
    "companion.window.mode",
    "companion.window.click_through",
    "companion.window.emergency",
    "companion.window.return",
):
    assert key in english and key in russian, "missing localization: " + key

new_source = "\n".join(read(path) for path in files)
for pattern, name in (
    (r"\bis\s+not\s+", "is not"),
    (r"\bis\s+null\b", "is null"),
    (r"=\s*new\s*\(", "target-typed new"),
):
    assert not re.search(pattern, new_source), "unsupported C# syntax: " + name

print("avatar Phase C static contract: ok")
