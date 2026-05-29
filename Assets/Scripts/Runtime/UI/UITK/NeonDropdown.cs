using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;

namespace NeonCompanion.Runtime.UI.UITK
{
    [UxmlElement]
    public sealed partial class NeonDropdown : VisualElement, INotifyValueChanged<string>
    {
        public event Action TriggerClicked;

        // ── UXML attribute: choices-csv="A,B,C" ─────────────────
        [UxmlAttribute]
        public string choicesCsv
        {
            get => string.Join(",", _choices);
            set
            {
                _choices.Clear();
                if (!string.IsNullOrEmpty(value))
                    foreach (var part in value.Split(','))
                    {
                        var t = part.Trim();
                        if (t.Length > 0)
                            _choices.Add(t);
                    }

                if (string.IsNullOrEmpty(_value) && _choices.Count > 0)
                    _value = _choices[0];

                UpdateLabel();
                RebuildItems();
            }
        }

        // ── INotifyValueChanged<string> ──────────────────────────
        public string value
        {
            get => _value;
            set
            {
                if (string.Equals(_value, value, StringComparison.Ordinal))
                    return;

                var prev = _value;
                _value = value ?? string.Empty;
                UpdateLabel();

                if (panel != null)
                {
                    using var evt = ChangeEvent<string>.GetPooled(prev, _value);
                    evt.target = this;
                    SendEvent(evt);
                }
            }
        }

        public void SetValueWithoutNotify(string newValue)
        {
            _value = newValue ?? string.Empty;
            UpdateLabel();
        }

        // ── Programmatic API ─────────────────────────────────────
        public List<string> choices
        {
            get => _choices;
            set
            {
                _choices = value ?? new List<string>();
                RebuildItems();
            }
        }

        // ── Private state ────────────────────────────────────────
        private string _value = string.Empty;
        private List<string> _choices = new List<string>();
        private bool _isOpen;

        private VisualElement _popup;
        private VisualElement _popupList;
        private VisualElement _panelRoot;
        private readonly Label _valueLabel;
        private EventCallback<PointerDownEvent> _outsideClickHandler;

        // ── Constructor ──────────────────────────────────────────
        public NeonDropdown()
        {
            AddToClassList("neon-dropdown");

            var trigger = new VisualElement();
            trigger.AddToClassList("neon-dropdown__trigger");
            trigger.RegisterCallback<ClickEvent>(OnTriggerClicked);

            _valueLabel = new Label();
            _valueLabel.AddToClassList("neon-dropdown__value");
            _valueLabel.pickingMode = PickingMode.Ignore;

            var arrow = new Label("▾");
            arrow.AddToClassList("neon-dropdown__arrow");
            arrow.pickingMode = PickingMode.Ignore;

            trigger.Add(_valueLabel);
            trigger.Add(arrow);
            Add(trigger);

            RegisterCallback<DetachFromPanelEvent>(_ => ClosePopup());
        }

        // ── Trigger ──────────────────────────────────────────────
        private void OnTriggerClicked(ClickEvent evt)
        {
            evt.StopPropagation();
            if (TriggerClicked != null)
            {
                TriggerClicked.Invoke();
                return;
            }
            if (_isOpen) ClosePopup();
            else OpenPopup();
        }

        // ── Popup lifecycle ──────────────────────────────────────
        private void OpenPopup()
        {
            if (_isOpen || panel == null) return;

            // Add popup to the UIDocument root (direct child of panel.visualTree)
            // so that UXML stylesheets cascade into the popup correctly.
            // As the last child it also renders on top of all other elements.
            _panelRoot = GetDocumentRoot();
            if (_panelRoot == null) return;

            EnsurePopup();
            RebuildItems();
            PositionPopup();

            _panelRoot.Add(_popup);
            _isOpen = true;
            AddToClassList("neon-dropdown--open");

            _outsideClickHandler = OnOutsidePointerDown;
            _panelRoot.RegisterCallback(_outsideClickHandler, TrickleDown.TrickleDown);
        }

        private VisualElement GetDocumentRoot()
        {
            // Walk up to the element whose parent is panel.visualTree — that's
            // UIDocument.rootVisualElement, which owns the UXML stylesheets.
            var el = (VisualElement)this;
            var panelRoot = panel?.visualTree;
            while (el.parent != null && el.parent != panelRoot)
                el = el.parent;
            return el;
        }

        private void ClosePopup()
        {
            _isOpen = false;
            RemoveFromClassList("neon-dropdown--open");
            _popup?.RemoveFromHierarchy();

            if (_panelRoot != null && _outsideClickHandler != null)
                _panelRoot.UnregisterCallback(_outsideClickHandler, TrickleDown.TrickleDown);

            _panelRoot = null;
            _outsideClickHandler = null;
        }

        private void OnOutsidePointerDown(PointerDownEvent evt)
        {
            if (_popup != null && _popup.worldBound.Contains(evt.position)) return;
            if (worldBound.Contains(evt.position)) return;
            ClosePopup();
        }

        private void EnsurePopup()
        {
            if (_popup != null) return;

            _popup = new VisualElement();
            _popup.AddToClassList("neon-dropdown__popup");
            _popup.style.position = Position.Absolute;

            var scroll = new ScrollView(ScrollViewMode.Vertical);
            scroll.AddToClassList("neon-dropdown__scroll");
            scroll.horizontalScrollerVisibility = ScrollerVisibility.Hidden;
            scroll.verticalScrollerVisibility = ScrollerVisibility.Auto;

            _popupList = new VisualElement();
            _popupList.AddToClassList("neon-dropdown__list");
            scroll.Add(_popupList);
            _popup.Add(scroll);
        }

        private void PositionPopup()
        {
            if (_popup == null) return;
            var b = worldBound;
            _popup.style.left = b.xMin;
            _popup.style.top = b.yMax + 4f;
            _popup.style.minWidth = Mathf.Max(b.width, 180f);
        }

        // ── Items ────────────────────────────────────────────────
        private void RebuildItems()
        {
            if (_popupList == null) return;
            _popupList.Clear();

            foreach (var choice in _choices)
            {
                var item = new Label(choice);
                item.AddToClassList("neon-dropdown__item");
                item.EnableInClassList("neon-dropdown__item--selected",
                    string.Equals(choice, _value, StringComparison.Ordinal));

                var captured = choice;
                item.RegisterCallback<ClickEvent>(evt =>
                {
                    evt.StopPropagation();
                    ClosePopup();
                    value = captured;
                });
                item.RegisterCallback<PointerEnterEvent>(_ =>
                    item.AddToClassList("neon-dropdown__item--hover"));
                item.RegisterCallback<PointerLeaveEvent>(_ =>
                    item.RemoveFromClassList("neon-dropdown__item--hover"));

                _popupList.Add(item);
            }
        }

        private void UpdateLabel()
        {
            if (_valueLabel != null)
                _valueLabel.text = _value;

            if (_popupList == null) return;
            foreach (var child in _popupList.Children())
            {
                if (child is Label lbl)
                    lbl.EnableInClassList("neon-dropdown__item--selected",
                        string.Equals(lbl.text, _value, StringComparison.Ordinal));
            }
        }
    }
}
