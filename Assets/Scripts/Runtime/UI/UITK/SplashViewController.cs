using System;
using System.Collections;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UIElements;

namespace NeonCompanion.Runtime.UI.UITK
{
    /// <summary>
    /// Cyberpunk boot-screen controller.
    /// Attach to a GameObject in the Loading scene together with a UIDocument.
    /// Assign <see cref="panelSettings"/> and <see cref="splashAsset"/> in the Inspector.
    /// </summary>
    public sealed class SplashViewController : MonoBehaviour
    {
        // ── Inspector ────────────────────────────────────────────────────────
        [SerializeField] private PanelSettings   panelSettings;
        [SerializeField] private VisualTreeAsset splashAsset;
        [SerializeField] private string          mainSceneName = "Main";

        // ── UI refs ──────────────────────────────────────────────────────────
        private VisualElement _logList;
        private VisualElement _progressFill;
        private Label         _versionLabel;
        private Label         _progressPct;

        // Fade-in elements (CSS transition opacity 0→1 triggered from C#)
        private VisualElement _logoImg;
        private VisualElement _titleArea;

        // Neon glow rings (pulsed from C# schedule)
        private VisualElement _ring1;
        private VisualElement _ring2;
        private VisualElement _ring3;
        private float         _glowTime;

        // ── Completion ───────────────────────────────────────────────────────
        public bool IsComplete { get; private set; }

        // ── Boot log entries ─────────────────────────────────────────────────
        private static readonly (string status, string text, bool ok)[] LogEntries =
        {
            ("OK", "Neural core engine initialized",           true),
            ("OK", "Voice synthesis modules loaded",           true),
            ("OK", "AI provider API · connection established", true),
            ("OK", "Avatar profile library synced",            true),
            ("..", "Calibrating response pipeline",             false),
            ("..", "Starting companion runtime...",             false),
        };

        // ── Lifecycle ─────────────────────────────────────────────────────────

        private void Awake()
        {
            var doc = GetComponent<UIDocument>() ?? gameObject.AddComponent<UIDocument>();
            if (panelSettings != null) doc.panelSettings    = panelSettings;
            if (splashAsset   != null) doc.visualTreeAsset  = splashAsset;
        }

        private void Start()
        {
            var doc  = GetComponent<UIDocument>();
            var root = doc?.rootVisualElement;
            if (root == null) return;

            _logList      = root.Q("log-list");
            _progressFill = root.Q("progress-fill");
            _versionLabel = root.Q<Label>("splash-version");
            _progressPct  = root.Q<Label>("progress-pct");
            _logoImg      = root.Q(className: "splash__logo-img");
            _titleArea    = root.Q(className: "splash__title-area");
            _ring1        = root.Q(className: "splash__glow-ring--1");
            _ring2        = root.Q(className: "splash__glow-ring--2");
            _ring3        = root.Q(className: "splash__glow-ring--3");

            // Build label: "V0.2 | BUILD 20260527"
            if (_versionLabel != null)
                _versionLabel.text = $"V{Application.version} | BUILD {DateTime.Now:yyyyMMdd}";

            // Trigger CSS opacity transitions — one frame delay so opacity:0
            // is fully committed before we change the value.
            root.schedule.Execute(() =>
            {
                if (_logoImg   != null) _logoImg.style.opacity   = 1f;
            }).StartingIn(16);

            root.schedule.Execute(() =>
            {
                if (_titleArea != null) _titleArea.style.opacity = 1f;
            }).StartingIn(200); // 16 ms base + ~180 ms stagger

            // Pulse neon glow rings every frame
            root.schedule.Execute(TickGlow).Every(0);

            StartCoroutine(RunBootSequence());
        }

        // ── Boot sequence ─────────────────────────────────────────────────────

        private IEnumerator RunBootSequence()
        {
            yield return new WaitForSeconds(0.5f);

            int   total            = LogEntries.Length;
            float progressPerEntry = 85f / total;
            float progress         = 0f;

            for (int i = 0; i < total; i++)
            {
                var (status, text, ok) = LogEntries[i];
                AddLogRow(status, text, ok);

                progress += progressPerEntry;
                SetProgress(progress);

                float delay = ok
                    ? UnityEngine.Random.Range(0.16f, 0.32f)
                    : UnityEngine.Random.Range(0.38f, 0.65f);

                yield return new WaitForSeconds(delay);
            }

            yield return new WaitForSeconds(0.3f);

            SetProgress(100f);
            yield return new WaitForSeconds(0.6f);

            IsComplete = true;
            SceneManager.LoadScene(mainSceneName, LoadSceneMode.Single);
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        /// <summary>
        /// Sinusoidal opacity pulse on the neon glow rings.
        /// ring1 (bright) pulses between 0.65–1.0
        /// ring2 (mid)    pulses between 0.25–0.55, offset phase
        /// ring3 (purple) pulses between 0.20–0.50, different speed
        /// </summary>
        private void TickGlow()
        {
            _glowTime += Time.deltaTime;

            if (_ring1 != null)
                _ring1.style.opacity = Mathf.Lerp(0.65f, 1.00f,
                    (Mathf.Sin(_glowTime * 2.1f) + 1f) * 0.5f);

            if (_ring2 != null)
                _ring2.style.opacity = Mathf.Lerp(0.25f, 0.55f,
                    (Mathf.Sin(_glowTime * 2.1f + 0.8f) + 1f) * 0.5f);

            if (_ring3 != null)
                _ring3.style.opacity = Mathf.Lerp(0.20f, 0.50f,
                    (Mathf.Sin(_glowTime * 1.5f + 1.4f) + 1f) * 0.5f);
        }

        private void AddLogRow(string status, string text, bool ok)
        {
            if (_logList == null) return;

            var row = new VisualElement();
            row.AddToClassList("splash__log-row");

            var statusLabel = new Label(status);
            statusLabel.AddToClassList(ok
                ? "splash__log-status--ok"
                : "splash__log-status--pending");

            var textLabel = new Label(text);
            textLabel.AddToClassList("splash__log-text");

            row.Add(statusLabel);
            row.Add(textLabel);
            _logList.Add(row);

            // Trigger CSS opacity transition on the next frame
            row.schedule.Execute(() => row.style.opacity = 1f).StartingIn(16);
        }

        private void SetProgress(float percent)
        {
            if (_progressFill == null) return;

            float clamped = Mathf.Clamp(percent, 0f, 100f);
            _progressFill.style.width =
                new StyleLength(new Length(clamped, LengthUnit.Percent));

            if (_progressPct != null)
                _progressPct.text = $"{Mathf.RoundToInt(clamped)}%";
        }
    }
}
