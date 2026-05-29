# 13_Avatar_Motion_Research.md

## Avatar motion research notes

Этот документ фиксирует текущую гипотезу по движению аватаров, чтобы агенты не переоткрывали тот же вопрос с нуля.

## Текущая архитектурная позиция

- **2D остаётся базовым режимом** для слабых ПК, Android/iOS и дешёвых сборок.
- **Realtime avatar — в первую очередь 3D desktop layer**, не обязательный baseline для всех платформ.
- 2D runtime должен быть лёгким и предсказуемым: Unity проигрывает заранее подготовленные sprite-sheet клипы/атласы.
- Генеративные инструменты не должны становиться runtime-зависимостью для слабых устройств.

## Зафиксированный MVP action set

Минимальный набор действий для 2D baseline:
- `idle`
- `thinking`
- `talking`
- `listening`
- `smile`
- `confused`

Смысл набора:
- `idle`, `thinking`, `talking`, `listening` — continuous states
- `smile`, `confused` — one-shot reactions
- Формат хранит клипы, но не решает, когда улыбаться или путаться
- `smile` и `confused` должны приходить из policy/app logic layer

Требования к ассетам:
- loop-friendly движение;
- минимальный drift позы, лица и силуэта;
- стабильный фон или прозрачность;
- одинаковая композиция/scale между клипами одного аватара;
- разумный размер атласов для mobile/low-end desktop.

## Ограничения asset pipeline

Для внешней генерации или подготовки motion-ассетов остаются общие ограничения:
- высокая latency для сложных пайплайнов неприемлема как runtime-path;
- результат не гарантирует loop-friendly анимацию;
- full-body и руки чаще дают drift/артефакты, чем talking-head/upper-body;
- результат всё равно требует отбора, чистки и упаковки в motion-pack формат.

## Full-body vs talking-head

Практический риск растёт так:
1. talking head / upper body — наиболее вероятно стабильный результат;
2. half-body gestures — возможно пригодно для sprite sheets после отбора;
3. full-body action loops — обязательно тестировать отдельно на drift, руки, ноги, loopability.

Для sprite-sheet pipeline сначала тестировать простые действия из MVP-набора:
- `idle`;
- `talking`;
- `thinking`;
- `listening`;
- `smile`;
- `confused`.

## Рекомендуемый следующий эксперимент

Цель: проверить, можно ли надёжно получать production-пригодные 2D motion assets через внешний asset pipeline.

Вход:
- reference image: `Assets/UI/Avatars/neon.png`
- короткий TTS voice clip: 4–5 секунд
- prompt/style brief для каноничного образа

Сгенерировать или собрать 3–5 клипов:
- `talking`
- `thinking`
- `listening`
- `idle`
- `smile` или `confused`

Проверить:
- сохранение образа Неон;
- стабильность лица/силуэта;
- stylized/anime quality;
- lip-sync, если он вообще участвует в пайплайне;
- loopability;
- артефакты рук/тела;
- размер sprite-sheet после нарезки;
- пригодность для Unity 2D animation import.

## Integration guidance for agents

Если агент продолжает эту тему:
1. Не предлагать внешнюю генерацию как обязательный runtime для mobile/low-end.
2. Не смешивать 2D sprite-sheet baseline и 3D realtime desktop path.
3. Сначала делать asset-pipeline proof-of-concept, а не клиентскую интеграцию.
4. Любую генерацию считать внешним tooling/backend step, не Unity runtime dependency.
5. После теста обновить `04_Avatar_System.md`, `07_CrossPlatform.md`, `09_Roadmap.md` и этот файл с фактическими результатами.
