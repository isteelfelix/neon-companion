using System;
using System.Collections;
using System.Text;
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

        // Text labels with dynamic effects
        private Label _titleLabel;  // gradient text
        private Label _tagline;     // flicker effect

        // Neon glow rings (pulsed from C# schedule)
        private VisualElement _ring1;
        private VisualElement _ring2;
        private VisualElement _ring3;
        private float         _glowTime;

        // Orbit ring (continuously rotated)
        private VisualElement _orbit;
        private float         _orbitAngle;

        // Scan line (top→bottom sweep)
        private VisualElement _root;
        private VisualElement _scanLine;
        private float         _scanTime;

        // Floating particles (translate-y + fade per particle)
        private struct ParticleAnim
        {
            public VisualElement el;
            public float phase, dur, dx, dy;
        }
        private ParticleAnim[] _particles;
        private float          _animTime;

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
            _titleLabel   = root.Q<Label>(className: "splash__title");
            _tagline      = root.Q<Label>(className: "splash__tagline");
            _ring1        = root.Q(className: "splash__glow-ring--1");
            _ring2        = root.Q(className: "splash__glow-ring--2");
            _ring3        = root.Q(className: "splash__glow-ring--3");
            _orbit        = root.Q("orbit-dashed");
            _scanLine     = root.Q("scan-line");
            _root         = root;

            // Tricolor gradient progress fill — generated at runtime, no PNG asset.
            if (_progressFill != null)
            {
                var tex = BuildHorizontalGradient(512, 4,
                    new Color(0.30f, 0.78f, 0.92f, 1f),
                    new Color(0.49f, 0.48f, 0.93f, 1f),
                    new Color(0.88f, 0.45f, 0.78f, 1f));
                _progressFill.style.backgroundImage = new StyleBackground(tex);
                _progressFill.style.backgroundColor = new StyleColor(new Color(0,0,0,0));
            }

            // Particles — capture all, assign per-particle phase / duration / drift.
            var pels = root.Query(className: "splash__particle").ToList();
            _particles = new ParticleAnim[pels.Count];
            for (int i = 0; i < pels.Count; i++)
            {
                _particles[i] = new ParticleAnim
                {
                    el    = pels[i],
                    phase = UnityEngine.Random.Range(0f, 8f),
                    dur   = UnityEngine.Random.Range(4f, 11f),
                    dx    = UnityEngine.Random.Range(-30f, 30f),
                    dy    = UnityEngine.Random.Range(-130f, -60f),
                };
            }

            // Build label: "V0.2 | BUILD 20260527"
            if (_versionLabel != null)
                _versionLabel.text = $"V{Application.version} · BUILD {DateTime.Now:yyyyMMdd}";

            // Tricolor gradient title: cyan → indigo → magenta per character
            if (_titleLabel != null)
            {
                _titleLabel.enableRichText = true;
                _titleLabel.text = BuildTriGradientText("NEON COMPANION",
                    new Color(0.30f, 0.78f, 0.92f),   // cyan-teal
                    new Color(0.49f, 0.48f, 0.93f),   // indigo (#7C7AED)
                    new Color(0.88f, 0.45f, 0.78f));  // magenta-pink
            }

            // Trigger CSS opacity transitions — one frame delay so opacity:0
            // is fully committed before we change the value.
            root.schedule.Execute(() =>
            {
                if (_logoImg   != null) _logoImg.style.opacity   = 1f;
            }).StartingIn(16);

            root.schedule.Execute(() =>
            {
                if (_titleArea != null) _titleArea.style.opacity = 1f;
            }).StartingIn(200);

            // Pulse rings, rotate orbit, sweep scan line, drift particles — every frame.
            root.schedule.Execute(TickAnims).Every(0);

            StartCoroutine(RunBootSequence());
            StartCoroutine(FlickerTagline());
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

        /// <summary>
        /// Three rapid opacity flickers on the tagline, starting at 2.2 s.
        /// Implemented in C# because UITK animation-* properties are not supported.
        /// </summary>
        private IEnumerator FlickerTagline()
        {
            yield return new WaitForSeconds(2.2f);

            for (int i = 0; i < 3; i++)
            {
                if (_tagline == null) yield break;

                _tagline.style.opacity = 0.25f;
                yield return new WaitForSeconds(0.072f);

                _tagline.style.opacity = 0.85f;
                yield return new WaitForSeconds(0.054f);

                _tagline.style.opacity = 1f;
                yield return new WaitForSeconds(0.054f);
            }

            if (_tagline != null) _tagline.style.opacity = 1f;
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        /// <summary>
        /// Builds rich-text string with per-character colour interpolated
        /// linearly between <paramref name="from"/> and <paramref name="to"/>.
        /// Spaces are emitted without a colour tag.
        /// </summary>
        private static string BuildGradientText(string text, Color from, Color to)
        {
            int nonSpaceCount = 0;
            foreach (char c in text)
                if (c != ' ') nonSpaceCount++;

            var sb  = new StringBuilder(text.Length * 28);
            int idx = 0;

            foreach (char c in text)
            {
                if (c == ' ') { sb.Append(' '); continue; }

                float t = nonSpaceCount > 1 ? (float)idx / (nonSpaceCount - 1) : 0f;
                Color col = Color.Lerp(from, to, t);
                sb.Append($"<color=#{ColorUtility.ToHtmlStringRGB(col)}>{c}</color>");
                idx++;
            }

            return sb.ToString();
        }

        /// <summary>
        /// Three-stop gradient: per-character colour goes from <paramref name="a"/>
        /// through <paramref name="b"/> at the midpoint to <paramref name="c"/>.
        /// </summary>
        private static string BuildTriGradientText(string text, Color a, Color b, Color c)
        {
            int nonSpaceCount = 0;
            foreach (char ch in text)
                if (ch != ' ') nonSpaceCount++;

            var sb  = new StringBuilder(text.Length * 28);
            int idx = 0;

            foreach (char ch in text)
            {
                if (ch == ' ') { sb.Append(' '); continue; }

                float t = nonSpaceCount > 1 ? (float)idx / (nonSpaceCount - 1) : 0f;
                Color col = t < 0.5f
                    ? Color.Lerp(a, b, t * 2f)
                    : Color.Lerp(b, c, (t - 0.5f) * 2f);
                sb.Append($"<color=#{ColorUtility.ToHtmlStringRGB(col)}>{ch}</color>");
                idx++;
            }

            return sb.ToString();
        }

        /// <summary>
        /// Per-frame: ring opacity/scale pulse, orbit rotation, scan-line sweep,
        /// particle drift. One driver to keep timing coherent.
        /// </summary>
        private void TickAnims()
        {
            float dt = Time.deltaTime;
            _glowTime  += dt;
            _animTime  += dt;
            _scanTime  += dt;

            // Ring opacity pulses (unchanged behaviour)
            if (_ring1 != null)
                _ring1.style.opacity = Mathf.Lerp(0.65f, 1.00f,
                    (Mathf.Sin(_glowTime * 2.1f) + 1f) * 0.5f);

            if (_ring2 != null)
            {
                float opa = Mathf.Lerp(0.25f, 0.55f,
                    (Mathf.Sin(_glowTime * 2.1f + 0.8f) + 1f) * 0.5f);
                _ring2.style.opacity = opa;
                // ring2 is the "glass" ring — also pulse its scale (1.0 ↔ 1.045)
                float s = Mathf.Lerp(1.00f, 1.045f,
                    (Mathf.Sin(_glowTime * 1.5f) + 1f) * 0.5f);
                _ring2.style.scale = new StyleScale(new Scale(new Vector3(s, s, 1f)));
            }

            if (_ring3 != null)
                _ring3.style.opacity = Mathf.Lerp(0.20f, 0.50f,
                    (Mathf.Sin(_glowTime * 1.5f + 1.4f) + 1f) * 0.5f);

            // Orbit slow reverse rotation (full turn in 22s)
            if (_orbit != null)
            {
                _orbitAngle = (_orbitAngle - dt * (360f / 22f));
                if (_orbitAngle <= -360f) _orbitAngle += 360f;
                _orbit.style.rotate = new StyleRotate(
                    new Rotate(new Angle(_orbitAngle, AngleUnit.Degree)));
            }

            // Scan line — sweep top→bottom over 5.5s, looping
            if (_scanLine != null && _root != null)
            {
                const float scanDur = 5.5f;
                float t = (_scanTime / scanDur) % 1f;
                float h = _root.resolvedStyle.height;
                if (h > 0f)
                    _scanLine.style.translate = new StyleTranslate(
                        new Translate(0f, t * h, 0f));

                // Opacity envelope: fade in fast, fade out near end
                float opa;
                if      (t < 0.04f) opa = Mathf.SmoothStep(0f, 1f, t / 0.04f);
                else if (t < 0.96f) opa = Mathf.Lerp(1f, 0.55f, (t - 0.04f) / 0.92f);
                else                opa = Mathf.Lerp(0.55f, 0f, (t - 0.96f) / 0.04f);
                _scanLine.style.opacity = opa;
            }

            // Particles — translate-y + opacity loop, per-particle phase
            if (_particles != null)
            {
                for (int i = 0; i < _particles.Length; i++)
                {
                    var p     = _particles[i];
                    float u   = ((_animTime + p.phase) / p.dur);
                    float f   = u - Mathf.Floor(u);
                    p.el.style.translate = new StyleTranslate(
                        new Translate(f * p.dx, f * p.dy, 0f));

                    float opa;
                    if      (f < 0.08f) opa = Mathf.SmoothStep(0f, 0.9f, f / 0.08f);
                    else if (f < 0.90f) opa = Mathf.Lerp(0.9f, 0.3f, (f - 0.08f) / 0.82f);
                    else                opa = Mathf.Lerp(0.3f, 0f, (f - 0.9f) / 0.1f);
                    p.el.style.opacity = opa;
                }
            }
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

        /// <summary>
        /// Builds a 3-stop horizontal gradient texture used as the progress-fill
        /// background. Tiled tiny height keeps memory minimal.
        /// </summary>
        private static Texture2D BuildHorizontalGradient(int width, int height,
            Color a, Color b, Color c)
        {
            var tex = new Texture2D(width, height, TextureFormat.RGBA32, false)
            {
                wrapMode   = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear,
                hideFlags  = HideFlags.HideAndDontSave,
            };
            var pixels = new Color[width * height];
            for (int x = 0; x < width; x++)
            {
                float t = width > 1 ? (float)x / (width - 1) : 0f;
                Color col = t < 0.5f
                    ? Color.Lerp(a, b, t * 2f)
                    : Color.Lerp(b, c, (t - 0.5f) * 2f);
                for (int y = 0; y < height; y++) pixels[y * width + x] = col;
            }
            tex.SetPixels(pixels);
            tex.Apply(false, true);
            return tex;
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
