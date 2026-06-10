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
