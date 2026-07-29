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

### Windows Companion Phase C acceptance

Сервер без Unity не подтверждает Windows runtime. Для принятия Phase C Felix
собирает профиль `Assets/Settings/Build Profiles/Windows.asset` и сохраняет:

1. screenshot основного UI с включённым Companion mode и отдельного прозрачного
   окна на выбранном monitor/scale;
2. Process Explorer/Task Manager proof двух процессов одного Player executable;
3. `Player.log` и `Logs/companion-player.log` с разными PID и IPC connected, без
   второго provider/session initialization в child log;
4. restart proof сохранённых visible/pin/click-through/monitor/scale/position;
5. child close и forced child crash proof: основной chat продолжает отправлять и
   хранить ту же session/history;
6. parent close proof: child PID завершается; зависший child также убирается после
   shutdown grace period;
7. click-through on/off и аварийный `Ctrl+Shift+F12`, drag, Settings, Column,
   Show/Hide, Pin и Stop state.

Android/iOS smoke должен подтвердить отсутствие второго процесса и скрытую
Windows-only card. До этих артефактов tracker остаётся `⏳`.

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
