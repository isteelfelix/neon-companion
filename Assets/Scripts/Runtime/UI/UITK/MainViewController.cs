using UnityEngine;
using UnityEngine.UIElements;

namespace NeonCompanion.Runtime.UI.UITK
{
    [RequireComponent(typeof(UIDocument))]
    public sealed class MainViewController : MonoBehaviour
    {
        private static readonly string[] NavItems = { "nav-chat", "nav-avatar", "nav-providers", "nav-history", "nav-themes", "nav-settings" };

        private VisualElement _root;
        private string _activeNav = "nav-chat";

        private void OnEnable()
        {
            var document = GetComponent<UIDocument>();
            if (document == null || document.rootVisualElement == null) return;
            _root = document.rootVisualElement;

            foreach (var id in NavItems)
            {
                var item = _root.Q<VisualElement>(id);
                if (item == null) continue;
                var captured = id;
                item.RegisterCallback<ClickEvent>(_ => SetActiveNav(captured));
            }

            var historyList = _root.Q<ScrollView>(className: "history__list");
            if (historyList != null)
            {
                foreach (var item in historyList.Query<VisualElement>(className: "history__item").ToList())
                {
                    var captured = item;
                    item.RegisterCallback<ClickEvent>(_ => SetActiveHistory(historyList, captured));
                }
            }

            SetActiveNav(_activeNav);
        }

        private void SetActiveNav(string id)
        {
            _activeNav = id;
            foreach (var n in NavItems)
            {
                var el = _root.Q<VisualElement>(n);
                if (el == null) continue;
                el.EnableInClassList("nav__item--active", n == id);
            }
        }

        private static void SetActiveHistory(ScrollView list, VisualElement selected)
        {
            foreach (var item in list.Query<VisualElement>(className: "history__item").ToList())
            {
                item.EnableInClassList("history__item--active", item == selected);
            }
        }
    }
}
