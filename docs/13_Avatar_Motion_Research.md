# 13_Avatar_Motion_Research.md

## Avatar motion research notes

Этот документ фиксирует текущую гипотезу по движению аватаров, чтобы агенты не переоткрывали тот же вопрос с нуля.

## Текущая архитектурная позиция

- **2D остаётся базовым режимом** для слабых ПК, Android/iOS и дешёвых сборок.
- **Realtime avatar — в первую очередь 3D desktop layer**, не обязательный baseline для всех платформ.
- 2D runtime должен быть лёгким и предсказуемым: Unity проигрывает заранее подготовленные sprite-sheet клипы/атласы.
- Генеративные модели не должны становиться runtime-зависимостью для слабых устройств.

## 2D sprite-sheet action set

Минимальный набор действий для 2D baseline:

- `idle`
- `talk-neutral`
- `talk-happy`
- `listen`
- `thinking`
- `typing` / `coding`
- `smile` / `smirk`
- `focused`
- `surprised`
- `annoyed`
- `tired`
- `error` / `confused`
- optional personality variants: `soft`, `teasing`, `dominant`

Требования к ассетам:

- loop-friendly движение;
- минимальный drift позы, лица и силуэта;
- стабильный фон или прозрачность;
- одинаковая композиция/scale между клипами одного аватара;
- разумный размер атласов для mobile/low-end desktop.

## LongCat-Video-Avatar-1.5 research

Исследованный кандидат:

- Model: `meituan-longcat/LongCat-Video-Avatar-1.5`
- Demo Space: `victor/LongCat-Video-Avatar-1.5`
- License: MIT для model weights
- Tasks: Audio-Text-to-Video, Audio-Image-to-Video, Video Continuation
- Заявленные сценарии: talking avatars, broadcasting, acting, singing, e-commerce, multi-person conversation, animation, animal characters
- Заявлено: full-body temporal stability, stylized/anime generalization, identity consistency

Практические параметры демо Space Victor:

- Генерация около 5 секунд (`125` frames at `25 fps`).
- 480p/720p output.
- INT8 DiT + DMD2 8-step LoRA.
- Whisper-Large-v3 audio encoder для lip-sync.
- Запуск на HF ZeroGPU `zero-a10g`; CPU/VPS без GPU не подходит.
- Минимальный runtime-набор весов большой: грубо 40+ GB disk, CUDA обязателен.

## Вывод по LongCat для проекта

LongCat **не считаем realtime runtime avatar layer**.

Правильная роль для `neon-companion`:

1. **Asset production tool** — генерировать короткие motion clips из каноничного образа.
2. **Premium async renderer** — опционально генерировать короткие video snippets для специальных реплик/моментов.
3. **Research branch only** до проверки качества на реальном образе Неон.

Нельзя закладываться на LongCat как на обязательную зависимость клиента:

- высокая latency;
- нужна CUDA/GPU-инфраструктура;
- генерация не гарантирует loop-friendly результат;
- full-body и руки могут давать drift/артефакты;
- результат нужно отбирать/чистить перед попаданием в ассеты.

## Full-body vs talking-head

LongCat, судя по model card, не ограничен только лицом:

- может работать с portrait/upper-body/full-body reference image;
- заявляет full-body temporal stability;
- заявляет anime/stylized и animal cases.

Но практический риск растёт так:

1. talking head / upper body — наиболее вероятно стабильный результат;
2. half-body gestures — возможно пригодно для sprite sheets после отбора;
3. full-body action loops — обязательно тестировать отдельно на drift, руки, ноги, loopability.

Для sprite-sheet pipeline сначала тестировать простые full/half-body действия:

- idle full-body;
- talking gesture;
- thinking pose;
- focused typing/coding;
- smile/wave.

## Рекомендуемый следующий эксперимент

Цель: понять, можно ли LongCat использовать для production генерации 2D motion assets.

Вход:

- reference image: `Assets/UI/Avatars/neon.png`
- короткий TTS voice clip: 4–5 секунд
- prompt style: `A stylized AI companion woman speaking naturally to the camera, subtle expression, clean futuristic interface background.`

Сгенерировать 3–5 клипов:

- `talk-neutral`
- `talk-happy`
- `thinking`
- `focused-coding`
- `idle` или `smile/wave`

Проверить:

- сохранение образа Неон;
- стабильность лица/силуэта;
- anime/stylized quality;
- lip-sync;
- loopability;
- артефакты рук/тела;
- размер sprite-sheet после нарезки;
- пригодность для Unity 2D animation import.

## Integration guidance for agents

Если агент продолжает эту тему:

1. Не предлагать LongCat как обязательный runtime для mobile/low-end.
2. Не смешивать 2D sprite-sheet baseline и 3D realtime desktop path.
3. Сначала делать asset-pipeline proof-of-concept, а не клиентскую интеграцию.
4. Любую генерацию считать внешним tooling/backend step, не Unity runtime dependency.
5. После теста обновить `04_Avatar_System.md`, `07_CrossPlatform.md`, `09_Roadmap.md` и этот файл с фактическими результатами.
