# Unity UITK / USS / C# Constraints

## C# 9 (Unity 2022.3) — NO EXCEPTIONS

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

### ✅ USE THESE
- flex-direction, flex-wrap, flex-grow, flex-shrink, flex-basis
- align-items, justify-content, align-self
- width, height, min-width, min-height, max-width, max-height
- margin, padding (individual properties)
- border-width, border-color, border-radius (INDIVIDUAL only)
- background-color, color
- font-size, font-weight
- opacity, visibility
- position: absolute | relative
- display: flex | none
- overflow: hidden | visible
- white-space, -unity-text-align
- :hover, :focus pseudo-classes
- var(--custom-property)

### ❌ NEVER USE THESE
- **cursor** — no cursor control in USS
- **transition** — ALL forms (transition, transition-property, transition-duration)
- **text-overflow: ellipsis** — not supported
- **gap** — no flex gap; use margin on children
- **border shorthand** — NO `border: 1px solid red`. Use border-width + border-color
- **border-top/bottom/left/right shorthands** — individual properties only
- **background shorthand** — use background-color
- **line-height** — not supported
- **overflow-x / overflow-y** — only overflow (both axes)
- **pointer-events: none** — use visibility: hidden
- **@media, @keyframes, ::before/::after** — none exist in USS
- **!important, calc(), hsl()** — not supported

## UITK Layout Rules

1. Every child in a flex container participates in that flex direction
2. Vertical elements must be SIBLINGS in a column parent, NOT children of a row container
3. Unity may silently ignore entire USS rule blocks if ONE property is invalid
4. display: may not fully remove from layout — create dynamically in C# instead of UXML

### Adding New UI Elements
- PREFERRED: create in C# dynamically, don't modify UXML
- If modifying UXML: add as sibling, don't restructure existing containers
- Hidden elements: create with `style.display = DisplayStyle.None` at runtime

```csharp
var el = new VisualElement();
el.name = "my-el";
el.AddToClassList("my-class");
el.style.display = DisplayStyle.None;
int idx = parent.IndexOf(target);
parent.Insert(idx, el);
```
