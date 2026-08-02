# 04_Avatar_System.md

## Система аватаров

### MVP (2D baseline)
- Статичные 2D изображения аватаров
- Возможность переключения между несколькими аватарами
- Загрузка своей картинки (из файла или URL)
- Анимация через sprite-sheet motion packs
- 2D остаётся базовым режимом для слабых ПК и mobile: runtime должен проигрывать заранее подготовленные клипы без зависимости от GPU/generative backend

### Фиксированный MVP action set
- Continuous states: `idle`, `thinking`, `talking`, `listening`
- One-shot reactions: `smile`, `confused`

Это жёсткий MVP-набор. Новые action names не добавляются без отдельного пересмотра контракта.

### Как это используется в runtime
- Формат хранит доступные клипы, но не принимает решения за приложение
- App/state mapper выбирает continuous state: `idle`, `thinking`, `talking`, `listening`
- Reaction policy триггерит `smile` или `confused` как one-shot события
- После one-shot реакции проигрыватель возвращается в текущий базовый continuous state

Примеры:
- ничего не происходит → `idle`
- модель генерирует ответ → `thinking`
- идёт TTS / воспроизведение ответа → `talking`
- пользователь печатает / говорит → `listening`
- тёплое успешное завершение → `smile`
- ошибка / сбой / неуспешный шаг → `confused`

### Motion pack v1 (MVP)

Каноничный runtime-формат — один `motion_pack.json` на один аватар. Спрайтшиты хранятся напрямую в корне аватара (без подкаталога `motion/`):

```text
<Resources/Avatars/neon>/
  motion_pack.json
  idle.png
  thinking.png
  talking.png
  listening.png
  smile.png
  confused.png
```

Встроенные motion packs сейчас находятся в `Assets/Resources/Avatars/`:
- `neon` — основной набор Neon
- `yorha-2b` — пиксельный набор YoRHa 2B, конвертированный из шести GIF-состояний

### Структура `motion_pack.json`

```json
{
  "formatVersion": 1,
  "format": "spritesheet-pack",
  "clips": [
    {
      "action": "idle",
      "spriteSheetPath": "idle.png",
      "columns": 4,
      "rows": 4,
      "frameCount": 16,
      "frameRate": 12,
      "loop": true,
      "pingPong": false
    }
  ]
}
```

Поля клипов:
- `action` — имя действия (`idle`, `thinking`, `talking`, `listening`, `smile`, `confused`)
- `spriteSheetPath` — путь к PNG спрайтшиту (относительно корня аватара)
- `columns`, `rows` — размерность сетки спрайтшита
- `frameCount` — необязательное число заполненных ячеек; рекомендуется для built-in pack, чтобы не читать пиксели импортированной текстуры
- `frameRate` — частота кадров
- `loop` — зацикленность
- `pingPong` — обратное проигрывание (idle/thinking)

### Инварианты MVP
- `version = 1`
- `format = "spritesheet-pack"`
- `idle` обязателен
- actions уникальны
- `frameCount <= columns * rows`
- continuous states обычно loop, `smile` и `confused` — one-shot
- fallback при отсутствии action: `idle`

### Будущие версии
- 3D модели как desktop-first realtime слой
- Lipsync при голосовом режиме
- Кастомизация аватара
- Расширение action vocabulary только после MVP
- Внешние генеративные инструменты для производства motion-ассетов не должны становиться baseline runtime dependency

### Режимы отображения аватара
`AvatarViewMode` enum:
- `Static` — статичное изображение
- `Animated` —.sprite-sheet анимация через motion pack
- `Volume3D` — 3D модель (desktop-first)

Тогглы переключения доступны в UI. Галерея аватаров разбита по табам: static / animated / 3D.

## Research notes
- Подробности по motion-pack ограничениям, full-body/talking-head рискам и asset-pipeline экспериментам: [13_Avatar_Motion_Research.md](13_Avatar_Motion_Research.md)

## Требования к изображениям
- Рекомендуемый размер: 512x512 или 1024x1024
- Поддержка прозрачности (PNG)
- Grid spritesheet с одинаковым размером кадров внутри файла
- Порядок кадров: слева направо, сверху вниз
- Автоматическое масштабирование и обрезка

## User-owned avatar backends (Phase A + VRM Phase B)

Экран аватаров сохраняет прежние built-in/static/sprite профили и добавляет
явный импорт четырех backend-типов:

- `static-2d` — PNG/JPG; после проверки и crop сохраняется локальная PNG-копия;
- `sprite-sheet` — `motion_pack.json` v1 и перечисленные в нем локальные PNG;
- `generic-3d` — существующий runtime GLB/glTF через glTFast;
- `vrm` — отдельный runtime backend через закреплённый UniVRM 0.131.2. Импортер
  вызывается только для расширения `.vrm`; произвольный GLB не считается VRM.

Импорт сначала проверяет исходный файл и показывает capability card. Профиль и
копия ассета создаются только после успешной проверки. Ошибка, отмена crop или
ошибка сохранения не меняют активный аватар. VRM до выбора повторно проверяется
UniVRM, поэтому ошибочная модель не заменяет текущий preview.

### VRM runtime (Phase B)

После успешного импорта путь пользователя: Avatars → VRM → выбрать VRM-файл →
проверить preview и capability card → Save → выбрать созданную gallery tile →
увидеть модель в основном preview. Capability card показывает фактически найденные
humanoid bones, blink, gaze, expressions, lipsync и упакованные VRMA state clips.
Отсутствующие необязательные возможности дают `vrm_restricted_features`: модель
остается доступной как restricted 3D, но отсутствующая функция не вызывается.

Состояния `idle`, `thinking`, `talking`, `listening`, `smile`, `confused`
ретаргетятся только для humanoid VRM и только когда соответствующий VRMA ресурс
существует. Voice playback управляет только найденными mouth expressions.
Recording, stop, cancel, interrupt и barge-in немедленно очищают speaking/mouth
state. Маршруты Hermes и Generic OpenAI TTS/STT не изменены.

Авторская вторичная физика VRM берётся напрямую из `VRMC_springBone`; приложение
не генерирует пружины или коллайдеры заново. Горизонтальный mouse-drag в основном 3D
preview вращает корень модели, поэтому волосы, одежда и body springs получают
ускорение и обсчитываются UniVRM. После быстрого отпускания остаётся короткая
инерция; вертикальный drag по-прежнему меняет наклон камеры.

Встроенная Neon VRM сохраняет авторские spring chains для bust, coat skirt,
hood/strings, sleeves и hair. Плащ использует отдельный cloth-like профиль:
18 цепей и 42 рабочих joint'а с пониженной stiffness, damping и направленной
вниз gravity. `Tools/tune_neon_vrm_springbones.py` детерминированно применяет и
проверяет этот профиль после повторного экспорта модели.

Bust использует две симметричные цепи и четыре рабочих joint'а. Для них задан
отдельный soft-body профиль с меньшей stiffness, умеренным damping и небольшой
направленной вниз gravity: это увеличивает амплитуду, но гасит механическое
пружинное дребезжание после движения.

### Windows Companion window (Phase C)

На Windows Player пользователь выбирает аватар в одной из четырёх независимых
категорий: static 2D, sprite-sheet, generic 3D или VRM. Правая колонка всегда
показывает только выбранный backend. При откреплении она остаётся на месте и
переключается на терминал; пользователь может скрыть колонку штатной кнопкой
макета. Тот же executable запускается вторым процессом с `--companion-player`.
Дочерний процесс
получает по случайно названному локальному named pipe только display snapshot:
идентификатор/имя/тип профиля, локальные display asset paths или встроенный preview,
state clip mapping и display transform. Provider config, API key, system prompt,
chat/session/history и transport в snapshot отсутствуют.

`AppBootstrap` распознаёт display-процесс до создания storage, secret store,
provider/session repositories, `ChatService`, plugins и voice. Поэтому Companion
Player не создаёт второй AI-сеанс и не конкурирует за JSON истории. Parent
передаёт состояния `idle`, `listening`, `thinking`, `speaking`, `stop`; Phase-B
motion-pack/3D state mapping используется непосредственно display runtime.

Окно интерактивно по умолчанию: его можно перетаскивать за видимую модель,
масштабировать колесом мыши либо кнопками hover-toolbar, показать/скрыть,
закрепить поверх окон, выбрать монитор, открыть настройки аватара, вернуться к
колонке или закрыть отдельно. Click-through действует только по прозрачным
пикселям; `Ctrl+Shift+F12` аварийно возвращает интерактивность. Позиция, масштаб
и controls сохраняются в `AppSettings`. Закрытие/crash player не завершает chat parent, а
закрытие parent сначала посылает `stop`/shutdown и затем принудительно убирает
зависший дочерний процесс. Android/iOS и Editor получают stub и никогда не spawn.

### Persisted profile contract v1

`AvatarProfile.contractVersion = 1` дополняет, но не удаляет legacy-поля
`imagePath`, `modelPath`, `is3D`, `animationClips` и
`motionPackManifestPath`. Репозиторий нормализует старые JSON-профили при чтении:
тип выводится из существующих полей, а capability evidence помечается как legacy.
Неизвестные будущие backend-типы и `contractVersion > 1` сохраняются в каталоге,
но не подменяются другим runtime renderer: UI показывает явную диагностику и
сохраняет текущий активный аватар.

Новые поля:

- `avatarType` — одна из строк выше;
- `source` — `local-user-owned-copy`, исходное имя, расширение, размер и путь
  относительно `Application.persistentDataPath`;
- `capabilities` — только факты, полученные при декодировании/валидации:
  `isVerified`, render/animation/humanoid/blink/gaze/expressions/lipsync,
  restricted status, число expressions, клипов, узлов, renderers и triangles,
  evidence code. Для legacy-профиля до повторного импорта UI
  показывает неизвестные возможности, а не выдумывает подтверждение;
- `stateClipMapping` — persisted mapping `idle`, `thinking`, `talking`,
  `listening`, `smile`, `confused` для generic 3D и VRM;
- `diagnostic` — стабильный код ограничения runtime
  (`vrm_restricted_features`).

### Local storage and limits

User-owned ассеты копируются вне packaged `Resources` в:

```text
Application.persistentDataPath/
  Avatars/
    custom_<guid>/
      avatar.png | motion_pack.json | model.glb | model.gltf | model.vrm
      <только явно перечисленные локальные sidecar-файлы>
```

Каждый импорт получает отдельный каталог, поэтому одинаковые исходные имена не
перезаписывают существующие аватары. Абсолютные/remote URI, symlink-файлы и пути,
выходящие из исходного каталога, отклоняются. При удалении нового профиля удаляется только его
проверенный каталог под `Avatars/custom_<guid>`.

Перед копированием Phase D повторно сверяет размер и время изменения каждого
проверенного файла и отклоняет collision путей назначения. Изменившийся после
preview источник требует повторной проверки и не оставляет частичный каталог.

Лимиты Phase A:

- static image: 20 MB, максимум `8192 x 8192`;
- sprite pack: до 24 клипов, 100 MB на bundle, 64 megapixels decoded суммарно,
  каждый sheet до `8192 x 8192`, grid должен делить размеры без остатка;
- GLB/glTF/VRM: 100 MB на локальный bundle;
- generic 3D: минимум один renderer; максимум 512 scene nodes, 128 renderers,
  500 000 triangles и 128 animation clips;
- VRM runtime inspection: минимум один renderer; максимум 512 scene nodes,
  128 renderers и 500 000 triangles.

Для `.gltf` копируются только локальные `buffers[].uri` и `images[].uri`.
`com.unity.cloud.gltfast` зафиксирован как прямая runtime dependency, а не
случайная транзитивная зависимость Unity AI package. `Avatar3D/link.xml`
сохраняет reflection-loaded assembly в IL2CPP-сборках.
UniVRM 0.131.2 хранится как embedded `com.vrmc.gltf` + `com.vrmc.vrm`;
runtime asmdef ссылается на VRM10 напрямую, а linker сохраняет VRM10 для IL2CPP.
Built-in Standard, URP Lit и UniUnlit shaders входят в
`m_AlwaysIncludedShaders`, поскольку raw runtime import не оставляет
сериализованной material-ссылки, которая автоматически защитила бы shaders
от build stripping. Runtime import выбирает glTF material generator активного
render pipeline: bundled UniVRM MToon в URP standalone создавал корректную
геометрию и rig, но не записывал ни одного пикселя в RenderTexture.
Catalog limits проверяются до Unity runtime import; после декодирования проверяются
повторно по фактической сцене. Generic loader хранит не более одного неактивного
template в cache.

Полная Windows-проверка требует Unity Editor, Windows TTS и лицензированный VRM.
Встроенный Neon хранится как raw `TextAsset`
`Assets/Resources/Avatars/neon/Neon.vrm.bytes`; шесть состояний аналогично
хранятся как `.vrma.bytes`. В runtime они импортируются UniVRM из байтов,
чтобы model и animation control rig не терялись при сериализации prefab.
Phase D evidence, performance observations и точные ограничения описаны в
`docs/24_Avatar_Phase_D_Hardening.md`.
