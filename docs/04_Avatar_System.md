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

## User-owned avatar backends (Phase A)

Экран аватаров сохраняет прежние built-in/static/sprite профили и добавляет
явный импорт четырех backend-типов:

- `static-2d` — PNG/JPG; после проверки и crop сохраняется локальная PNG-копия;
- `sprite-sheet` — `motion_pack.json` v1 и перечисленные в нем локальные PNG;
- `generic-3d` — существующий runtime GLB/glTF через glTFast;
- `vrm` — отдельный тип контракта и каталог. Runtime-рендеринг, expressions,
  анимация и lipsync VRM относятся к Phase B.

Импорт сначала проверяет исходный файл и показывает capability card. Профиль и
копия ассета создаются только после успешной проверки. Ошибка, отмена crop или
ошибка сохранения не меняют активный аватар. Для VRM кнопка применения отключена:
карточка прямо сообщает, что профиль пока catalog-only.

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
  `isVerified`, render/animation/lipsync, число клипов, узлов, renderers и
  triangles, evidence code. Для legacy-профиля до повторного импорта UI
  показывает неизвестные возможности, а не выдумывает подтверждение;
- `stateClipMapping` — persisted mapping `idle`, `thinking`, `talking`,
  `listening`, `smile`, `confused` для generic 3D;
- `diagnostic` — стабильный код ограничения runtime (сейчас
  `vrm_runtime_phase_b`).

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

Лимиты Phase A:

- static image: 20 MB, максимум `8192 x 8192`;
- sprite pack: до 24 клипов, 100 MB на bundle, 64 megapixels decoded суммарно,
  каждый sheet до `8192 x 8192`, grid должен делить размеры без остатка;
- GLB/glTF/VRM: 100 MB на локальный bundle;
- generic 3D: минимум один renderer; максимум 512 scene nodes, 128 renderers,
  500 000 triangles и 128 animation clips;
- VRM catalog inspection: максимум 512 nodes и 128 meshes.

Для `.gltf` копируются только локальные `buffers[].uri` и `images[].uri`.
`com.unity.cloud.gltfast` зафиксирован как прямая runtime dependency, а не
случайная транзитивная зависимость Unity AI package. `Avatar3D/link.xml`
сохраняет reflection-loaded assembly в IL2CPP-сборках.
