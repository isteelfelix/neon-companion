# 04_Avatar_System.md

## Система аватаров

### MVP (2D)
- Статичные 2D изображения аватаров
- Возможность переключения между несколькими аватарами
- Загрузка своей картинки (из файла или URL)
- Анимация через спрайтшиты (idle, talking, reactions)
- 2D остаётся базовым режимом для слабых ПК и mobile: runtime должен проигрывать заранее подготовленные sprite-sheet action clips без зависимости от GPU/generative backend

### 2D action sets
- Базовые клипы: `idle`, `talk-neutral`, `talk-happy`, `listen`, `thinking`, `typing/coding`
- Эмоциональные варианты: `smile/smirk`, `focused`, `surprised`, `annoyed`, `tired`, `error/confused`
- Personality variants опциональны: `soft`, `teasing`, `dominant`
- Ассеты должны быть loop-friendly: без drift позы/лица/силуэта, с согласованным scale и композицией между клипами

### Будущие версии
- 2D анимированные аватары (**спрайтшиты** — приоритет; Spine — опционально)
- 3D модели как desktop-first realtime слой
- Lipsync при голосовом режиме
- Кастомизация аватара
- Генеративные модели вроде LongCat-Video-Avatar-1.5 рассматривать как tooling для производства ассетов или async premium snippets, не как baseline runtime dependency

## Research notes
- Подробности по LongCat, full-body/talking-head рискам и asset-pipeline эксперименту: [13_Avatar_Motion_Research.md](13_Avatar_Motion_Research.md)

## Требования к изображениям
- Рекомендуемый размер: 512x512 или 1024x1024
- Поддержка прозрачности (PNG)
- Автоматическое масштабирование и обрезка