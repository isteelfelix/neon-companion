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
Версия хранится в файле `VERSION` в корне проекта (формат `MAJOR.MINOR.PATCH`).

### Публикация
Релизы публикуются вручную через GitHub Releases: https://github.com/isteelfelix/neon-companion/releases
