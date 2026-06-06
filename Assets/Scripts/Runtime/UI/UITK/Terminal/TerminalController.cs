using System;
using System.Threading.Tasks;
using NeonCompanion.Runtime.Core;
using NeonCompanion.Runtime.Localization;
using UnityEngine;
using UnityEngine.UIElements;

namespace NeonCompanion.Runtime.UI.UITK.Terminal
{
    public sealed class TerminalController : MonoBehaviour
    {
        private VisualElement _root;
        private Label _statusLabel;
        private Label _outputLabel;
        private ScrollView _outputScroll;
        private TextField _inputField;
        private Button _submitButton;

        private ProcessExecutionService _processService;
        private bool _isExecuting;

        private string _emptyText;

        public void Initialize(VisualElement terminalRoot)
        {
            _root = terminalRoot;
            if (_root == null)
                return;

            // Try to find elements (from UXML clone if hosted). If not present, build fallback UI.
            _statusLabel = _root.Q<Label>("terminal-status");
            _outputLabel = _root.Q<Label>("terminal-output");
            _outputScroll = _root.Q<ScrollView>("terminal-output-scroll");
            _inputField = _root.Q<TextField>("terminal-input");
            _submitButton = _root.Q<Button>("terminal-submit");

            if (_outputLabel == null || _inputField == null || _submitButton == null)
            {
                BuildFallbackUI(_root);
            }

            // Re-query after possible build
            _statusLabel = _root.Q<Label>("terminal-status");
            _outputLabel = _root.Q<Label>("terminal-output");
            _outputScroll = _root.Q<ScrollView>("terminal-output-scroll");
            _inputField = _root.Q<TextField>("terminal-input");
            _submitButton = _root.Q<Button>("terminal-submit");

            _emptyText = LocalizationExtensions.Get("terminal.output.empty", "No output yet. Type a command above.");

            if (_outputLabel != null)
            {
                _outputLabel.text = _emptyText;
            }

            SetStatus("idle");

            if (_submitButton != null)
            {
                _submitButton.clicked += OnSubmitClicked;
            }

            if (_inputField != null)
            {
                _inputField.RegisterCallback<KeyDownEvent>(OnInputKeyDown);
            }

            // Resolve service (AppBootstrap is DontDestroy, Find works after bootstrap)
            ResolveService();
        }

        private void BuildFallbackUI(VisualElement container)
        {
            container.Clear();

            var root = new VisualElement();
            root.name = "terminal-root";
            root.AddToClassList("terminal-root");
            root.style.flexGrow = 1;
            root.style.flexDirection = FlexDirection.Column;

            var header = new VisualElement();
            header.AddToClassList("terminal-header");
            header.style.flexDirection = FlexDirection.Row;
            header.style.alignItems = Align.Center;
            header.style.justifyContent = Justify.SpaceBetween;
            header.style.paddingTop = 6;
            header.style.paddingBottom = 6;
            header.style.paddingLeft = 8;
            header.style.paddingRight = 8;
            header.style.borderBottomWidth = 1;
            header.style.borderBottomColor = new StyleColor(new Color(0.14f, 0.15f, 0.2f)); // approx --line-1

            var title = new Label(LocalizationExtensions.Get("terminal.title", "Terminal"));
            title.AddToClassList("terminal-title");
            title.style.color = new StyleColor(new Color(0.95f, 0.96f, 0.98f));
            title.style.fontSize = 12;
            title.style.unityFontStyleAndWeight = FontStyle.Bold;

            _statusLabel = new Label(LocalizationExtensions.Get("terminal.status.idle", "Ready"));
            _statusLabel.name = "terminal-status";
            _statusLabel.AddToClassList("terminal-status");
            _statusLabel.style.color = new StyleColor(new Color(0.6f, 0.64f, 0.7f));
            _statusLabel.style.fontSize = 10;
            _statusLabel.style.unityFontDefinition = StyleKeyword.Null; // will inherit or default mono-ish

            header.Add(title);
            header.Add(_statusLabel);
            root.Add(header);

            _outputScroll = new ScrollView();
            _outputScroll.name = "terminal-output-scroll";
            _outputScroll.AddToClassList("terminal-output-scroll");
            _outputScroll.style.flexGrow = 1;
            _outputScroll.style.minHeight = 80;
            _outputScroll.style.backgroundColor = new StyleColor(new Color(0.1f, 0.11f, 0.14f));
            _outputScroll.style.borderBottomWidth = 1;
            _outputScroll.style.borderBottomColor = new StyleColor(new Color(0.14f, 0.15f, 0.2f));
            _outputScroll.style.paddingTop = 4;
            _outputScroll.style.paddingBottom = 4;
            _outputScroll.style.paddingLeft = 6;
            _outputScroll.style.paddingRight = 6;

            _outputLabel = new Label();
            _outputLabel.name = "terminal-output";
            _outputLabel.AddToClassList("terminal-output");
            _outputLabel.style.color = new StyleColor(new Color(0.85f, 0.88f, 0.92f));
            _outputLabel.style.fontSize = 11;
            _outputLabel.style.whiteSpace = WhiteSpace.Normal;
            _outputLabel.style.unityTextAlign = TextAnchor.UpperLeft;
            _outputScroll.Add(_outputLabel);
            root.Add(_outputScroll);

            var inputRow = new VisualElement();
            inputRow.AddToClassList("terminal-input-row");
            inputRow.style.flexDirection = FlexDirection.Row;
            inputRow.style.alignItems = Align.Center;
            inputRow.style.paddingTop = 4;
            inputRow.style.paddingBottom = 4;
            inputRow.style.paddingLeft = 6;
            inputRow.style.paddingRight = 6;

            _inputField = new TextField();
            _inputField.name = "terminal-input";
            _inputField.AddToClassList("terminal-input");
            _inputField.style.flexGrow = 1;
            _inputField.style.marginRight = 6;
            _inputField.style.backgroundColor = new StyleColor(new Color(0.07f, 0.08f, 0.1f));
            _inputField.style.color = new StyleColor(new Color(0.95f, 0.96f, 0.98f));
            _inputField.style.borderWidth = 1;
            _inputField.style.borderColor = new StyleColor(new Color(0.18f, 0.19f, 0.23f));
            _inputField.style.borderTopLeftRadius = 4;
            _inputField.style.borderTopRightRadius = 4;
            _inputField.style.borderBottomLeftRadius = 4;
            _inputField.style.borderBottomRightRadius = 4;
            _inputField.style.paddingTop = 3;
            _inputField.style.paddingBottom = 3;
            _inputField.style.paddingLeft = 5;
            _inputField.style.paddingRight = 5;
            _inputField.style.fontSize = 11;

            _submitButton = new Button();
            _submitButton.name = "terminal-submit";
            _submitButton.AddToClassList("btn");
            _submitButton.AddToClassList("btn--primary");
            _submitButton.AddToClassList("terminal-submit-btn");
            _submitButton.text = "Run";
            _submitButton.style.flexShrink = 0;
            _submitButton.style.paddingLeft = 10;
            _submitButton.style.paddingRight = 10;
            _submitButton.style.paddingTop = 3;
            _submitButton.style.paddingBottom = 3;
            _submitButton.style.fontSize = 11;

            inputRow.Add(_inputField);
            inputRow.Add(_submitButton);
            root.Add(inputRow);

            container.Add(root);
        }

        private void ResolveService()
        {
            if (_processService != null)
                return;

            var bootstrap = FindObjectOfType<AppBootstrap>();
            if (bootstrap != null && bootstrap.App != null && bootstrap.App.Services != null)
            {
                try
                {
                    _processService = bootstrap.App.Services.GetRequired<ProcessExecutionService>();
                }
                catch (Exception)
                {
                    _processService = null;
                }
            }
        }

        private void OnInputKeyDown(KeyDownEvent evt)
        {
            if (evt.keyCode == KeyCode.Return || evt.keyCode == KeyCode.KeypadEnter)
            {
                if (!_isExecuting)
                {
                    evt.StopPropagation();
                    SubmitCurrentCommand();
                }
            }
        }

        private void OnSubmitClicked()
        {
            if (!_isExecuting)
                SubmitCurrentCommand();
        }

        private void SubmitCurrentCommand()
        {
            if (_inputField == null)
                return;

            string cmd = _inputField.value != null ? _inputField.value.Trim() : string.Empty;
            if (string.IsNullOrEmpty(cmd))
                return;

            _inputField.value = string.Empty;
            _ = ExecuteCommandAsync(cmd);
        }

        private async Task ExecuteCommandAsync(string command)
        {
            if (_isExecuting)
                return;

            _isExecuting = true;
            SetStatus("executing");
            SetInputEnabled(false);

            AppendOutput("> " + command);

            ResolveService();

            if (_processService == null)
            {
                AppendOutput("Error: ProcessExecutionService not available.");
                SetStatus("error");
                _isExecuting = false;
                SetInputEnabled(true);
                return;
            }

            try
            {
                var res = await _processService.ExecuteAsync(command, 30000);

                if (res.timedOut)
                {
                    if (!string.IsNullOrEmpty(res.stdout))
                        AppendOutput(res.stdout);
                    if (!string.IsNullOrEmpty(res.stderr))
                        AppendOutput(res.stderr);
                    SetStatus("timeout");
                }
                else
                {
                    if (!string.IsNullOrEmpty(res.stdout))
                        AppendOutput(res.stdout);
                    if (!string.IsNullOrEmpty(res.stderr))
                        AppendOutput(res.stderr);

                    if (res.exitCode != 0)
                        AppendOutput("[exit " + res.exitCode + "]");
                    SetStatus("idle");
                }
            }
            catch (Exception ex)
            {
                AppendOutput("Error: " + ex.Message);
                SetStatus("error");
            }
            finally
            {
                _isExecuting = false;
                SetInputEnabled(true);
                if (_statusLabel != null && _statusLabel.text != null &&
                    _statusLabel.text.IndexOf("execut", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    SetStatus("idle");
                }
            }
        }

        private void AppendOutput(string text)
        {
            if (_outputLabel == null)
                return;

            if (_outputLabel.text == _emptyText)
                _outputLabel.text = string.Empty;

            string current = _outputLabel.text ?? string.Empty;
            if (!string.IsNullOrEmpty(current))
                current += "\n";

            current += text;
            _outputLabel.text = current;

            // Scroll to bottom after layout
            if (_outputScroll != null)
            {
                _outputScroll.schedule.Execute(() =>
                {
                    if (_outputScroll != null)
                    {
                        _outputScroll.scrollOffset = new Vector2(0, float.MaxValue);
                    }
                }).StartingIn(10);
            }
        }

        private void SetStatus(string state)
        {
            if (_statusLabel == null)
                return;

            string key;
            string fallback;
            switch (state)
            {
                case "executing":
                    key = "terminal.status.executing";
                    fallback = "Running...";
                    break;
                case "error":
                    key = "terminal.status.error";
                    fallback = "Error";
                    break;
                case "timeout":
                    key = "terminal.status.timeout";
                    fallback = "Timed out";
                    break;
                case "idle":
                default:
                    key = "terminal.status.idle";
                    fallback = "Ready";
                    break;
            }

            _statusLabel.text = LocalizationExtensions.Get(key, fallback);
        }

        private void SetInputEnabled(bool enabled)
        {
            if (_inputField != null)
                _inputField.SetEnabled(enabled);
            if (_submitButton != null)
                _submitButton.SetEnabled(enabled);
        }

        public void SetVisible(bool visible)
        {
            if (_root != null)
                _root.style.display = visible ? DisplayStyle.Flex : DisplayStyle.None;
        }

        private void OnDestroy()
        {
            if (_submitButton != null)
                _submitButton.clicked -= OnSubmitClicked;
            if (_inputField != null)
                _inputField.UnregisterCallback<KeyDownEvent>(OnInputKeyDown);
        }
    }
}
