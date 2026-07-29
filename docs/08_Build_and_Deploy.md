# 08_Build_and_Deploy.md

## Сборка проекта

### Требования
- Unity 6 (6000.4 или новее)
- Android SDK (для мобильных сборок)
- Git

### Ручная сборка
1. Открыть проект в Unity
2. Перейти в `File → Build Settings`
3. Выбрать целевую платформу
4. Собрать

### Windows Companion Phase C/D acceptance

Сервер без Unity не подтверждает Windows runtime. Машиночитаемый source of truth —
`Tools/companion_windows_acceptance.json`: ровно десять критериев, executable
coverage, текущий environment blocker и требуемый артефакт Felix.

После Windows Development build Felix включает Companion mode, задаёт
non-default monitor/scale/position/pin/click-through, закрывает Player и запускает:

```powershell
powershell -ExecutionPolicy Bypass -File Tools\Test-CompanionWindowsAcceptance.ps1 `
  -PlayerPath C:\path\to\neon-companion.exe
```

Harness не меняет settings и сохраняет логи, PID/command line, JSON до/после,
protected-data hashes и `result.json`. Оставшийся smoke checklist:

1. отдельное прозрачное Companion окно на выбранных monitor/scale;
2. ровно parent + один `--companion-player`, IPC connected;
3. child log без provider/secret/session/chat/plugin/voice bootstrap;
4. legacy static/sprite, generated GLB/glTF, committed VRM 1.0 и licensed VRM 0.x;
5. idle/listening/thinking/speaking/reaction/stop parity в обоих preview;
6. TTS lipsync и мгновенный mouth/busy/queue reset на stop/cancel/barge-in;
7. visible/pin/click-through/monitor/scale/position после restart;
8. drag, Settings, Column, Show/Hide, Pin, click-through и `Ctrl+Shift+F12`;
9. child close/crash: parent отправляет сообщение в той же session/history;
10. parent close удаляет child; Android/iOS не spawn и не показывают Windows card.

До Unity EditMode, Windows/TTS, licensed VRM 0.x, profiler и этих десяти артефактов
tracker остаётся `⏳`. Подробности: `docs/24_Avatar_Phase_D_Hardening.md`.

### Headless-сборка (Android)
Безголовая сборка через `AndroidHeadlessBuild.cs`:
```bash
Unity -batchmode -nographics -projectPath . \
  -executeMethod AndroidHeadlessBuild.Build -quit
```
Скрипт автоматически:
- Включает IL2CPP + ARM64
- Принудительно ставит `applicationEntry = Activity` (через reflection)
- Устанавливает иконку из `Assets/UI/Branding/app-icon-1024.png`
- Диагностика: `-executeMethod AndroidHeadlessBuild.DiagEntry` (печатает enum `applicationEntry`)

### Версионирование
Версия задаётся в Player Settings активного Unity Build Profile (`Version` / `bundleVersion`, формат `MAJOR.MINOR.PATCH`). Интерфейс приложения получает её через `Application.version`.

### Публикация
Релизы публикуются вручную через GitHub Releases: https://github.com/isteelfelix/neon-companion/releases
