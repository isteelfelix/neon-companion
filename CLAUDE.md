# Unity UITK / USS / C# Constraints (from official Unity 6.4 docs)

## C# 9 (Unity 6) — NO EXCEPTIONS

```
❌ switch expressions        → use switch statement
❌ is not null / is not x    → use == null / !(x is string)
❌ tuple deconstruction      → use separate variables
❌ target-typed new()        → use new TypeName()
❌ pattern matching props    → use if/else chains
```

### Common Mistakes
- `[Serializable]` — ALWAYS `using System;` + `[Serializable]`, NEVER `[UnityEngine.Serializable]`
- `async` without `await` — remove `async`, return `Task.CompletedTask`
- Closure capture — `int index = i;` must be OUTSIDE any inner if/for block
- No UniTask — use `System.Threading.Tasks`
- No HttpClient — all HTTP via `UnityWebRequest`

## Unity USS — What Works and What Doesn't

### ✅ SUPPORTED (use these freely)
- **Flex**: flex-direction, flex-wrap, flex-grow, flex-shrink, flex-basis, flex, align-items, align-content, align-self, justify-content
- **Sizing**: width, height, min-width, min-height, max-width, max-height
- **Spacing**: margin (shorthand + individual), padding (shorthand + individual)
- **Border**: border-width, border-color, border-radius (ALL shorthands work), border-top/bottom/left/right-width, border-top/bottom/left/right-color
- **Visual**: background-color, background-image, background-size, background-position, color, opacity, visibility, display, position, overflow, filter
- **Text**: font-size, font-style, letter-spacing, word-spacing, white-space, text-overflow, text-shadow, -unity-text-align, -unity-text-overflow-position
- **Transform**: rotate, scale, translate, transform-origin
- **Animation**: transition, transition-property, transition-duration, transition-delay, transition-timing-function
- **Cursor**: cursor
- **Positioning**: top, right, bottom, left
- **Selectors**: :hover, :focus, :active, :disabled, :enabled
- **Variables**: var(--custom-property)

### ❌ NOT SUPPORTED
- **z-index** — no z-index property at all
- **gap** — no flex gap; use margin on children
- **line-height** — not supported
- **pointer-events** — not supported; use visibility: hidden
- **box-shadow** — not supported (silently ignored)
- **@media** queries — not supported
- **@keyframes** — not supported
- **::before / ::after** — no pseudo-elements
- **!important** — not supported
- **calc()** — not supported
- **grid** — no grid layout (flex only)
- **float / clear** — not supported

### ⚠️ Gotchas
- display: may not fully remove from layout — create dynamically in C# instead of UXML
- Elements inside flex-row containers all participate in the row. Vertical elements must be SIBLINGS in a column parent, NOT children of the row.
- Unity silently ignores entire USS rule blocks if ONE property is invalid

## Adding New UI Elements
- PREFERRED: create in C# dynamically, don't modify UXML
- Hidden elements: create with `style.display = DisplayStyle.None` at runtime

```csharp
var el = new VisualElement();
el.name = "my-el";
el.AddToClassList("my-class");
el.style.display = DisplayStyle.None;
int idx = parent.IndexOf(target);
parent.Insert(idx, el);
```
