using System;
using System.Text;

namespace NeonCompanion.Runtime.Terminal.Emulator
{
    /// <summary>
    /// Receives the parsed events from <see cref="VtParser"/>. Implemented by
    /// <see cref="TerminalEmulator"/>.
    /// </summary>
    public interface IVtParserHandler
    {
        /// <summary>A printable character should be placed at the cursor.</summary>
        void Print(char c);

        /// <summary>A C0 control byte (&lt; 0x20) such as CR, LF, BS, HT, BEL.</summary>
        void Execute(char control);

        /// <summary>
        /// A complete CSI sequence: ESC [ &lt;prefix&gt; params &lt;intermediate&gt; finalByte.
        /// <paramref name="prefix"/> is a private marker ('?', '&lt;', '=', '&gt;') or '\0'.
        /// </summary>
        void CsiDispatch(char finalByte, int[] parameters, int paramCount, char prefix, char intermediate);

        /// <summary>A complete ESC sequence: ESC &lt;intermediate&gt; finalByte.</summary>
        void EscDispatch(char finalByte, char intermediate);

        /// <summary>A complete OSC sequence: ESC ] command ; data (BEL|ST).</summary>
        void OscDispatch(int command, string data);
    }

    /// <summary>
    /// A pragmatic VT100/xterm escape-sequence parser modeled on the classic VT500 state
    /// machine (ground / escape / CSI / OSC). It is intentionally a subset — enough for
    /// real shells, colored CLI tools, and full-screen apps — and never throws on malformed
    /// input: unknown sequences are dropped and parsing resyncs at the next final byte.
    ///
    /// Input is UTF-8-decoded chars (control bytes pass through as ASCII). Feed one char at
    /// a time via <see cref="Process"/>.
    /// </summary>
    public sealed class VtParser
    {
        private enum State
        {
            Ground = 0,
            Escape = 1,
            EscapeIntermediate = 2,
            CsiEntry = 3,
            CsiParam = 4,
            CsiIntermediate = 5,
            CsiIgnore = 6,
            OscString = 7,
            DcsIgnore = 8
        }

        private const int MaxParams = 16;

        private readonly IVtParserHandler _handler;

        private State _state = State.Ground;
        private readonly int[] _params = new int[MaxParams];
        private int _paramCount;
        private char _prefix;
        private char _intermediate;
        private readonly StringBuilder _oscBuffer = new StringBuilder(64);
        private bool _oscEscPending;

        public VtParser(IVtParserHandler handler)
        {
            if (handler == null)
                throw new ArgumentNullException(nameof(handler));
            _handler = handler;
        }

        public void Reset()
        {
            _state = State.Ground;
            ResetParams();
            _oscBuffer.Length = 0;
            _oscEscPending = false;
        }

        public void Process(char c)
        {
            switch (_state)
            {
                case State.Ground:
                    Ground(c);
                    break;
                case State.Escape:
                    Escape(c);
                    break;
                case State.EscapeIntermediate:
                    EscapeIntermediate(c);
                    break;
                case State.CsiEntry:
                case State.CsiParam:
                case State.CsiIntermediate:
                case State.CsiIgnore:
                    Csi(c);
                    break;
                case State.OscString:
                    Osc(c);
                    break;
                case State.DcsIgnore:
                    DcsIgnore(c);
                    break;
            }
        }

        private void Ground(char c)
        {
            if (c == '\x1b')
            {
                _state = State.Escape;
                _intermediate = '\0';
                return;
            }
            if (c < 0x20 || c == 0x7f)
            {
                _handler.Execute(c);
                return;
            }
            _handler.Print(c);
        }

        private void Escape(char c)
        {
            // C0 controls remain active during escape sequences.
            if (c < 0x20)
            {
                _handler.Execute(c);
                return;
            }
            if (c == '[')
            {
                _state = State.CsiEntry;
                ResetParams();
                return;
            }
            if (c == ']')
            {
                _state = State.OscString;
                _oscBuffer.Length = 0;
                _oscEscPending = false;
                return;
            }
            if (c == 'P' || c == 'X' || c == '^' || c == '_')
            {
                // DCS / SOS / PM / APC — collected and ignored.
                _state = State.DcsIgnore;
                _oscEscPending = false;
                return;
            }
            if (c >= 0x20 && c <= 0x2f)
            {
                _intermediate = c;
                _state = State.EscapeIntermediate;
                return;
            }
            // Final byte of a plain ESC sequence.
            _handler.EscDispatch(c, '\0');
            _state = State.Ground;
        }

        private void EscapeIntermediate(char c)
        {
            if (c >= 0x20 && c <= 0x2f)
            {
                _intermediate = c;
                return;
            }
            _handler.EscDispatch(c, _intermediate);
            _state = State.Ground;
        }

        private void Csi(char c)
        {
            // Controls still execute mid-CSI.
            if (c < 0x20)
            {
                _handler.Execute(c);
                return;
            }

            if (_state == State.CsiIgnore)
            {
                // Wait for a final byte, then drop the whole sequence.
                if (c >= 0x40 && c <= 0x7e)
                    _state = State.Ground;
                return;
            }

            // Private prefix marker, only valid as the first byte after CSI.
            if (_state == State.CsiEntry && (c == '?' || c == '<' || c == '=' || c == '>'))
            {
                _prefix = c;
                _state = State.CsiParam;
                return;
            }

            if (c >= '0' && c <= '9')
            {
                if (_paramCount == 0)
                    _paramCount = 1;
                long value = (long)_params[_paramCount - 1] * 10 + (c - '0');
                _params[_paramCount - 1] = value > 65535 ? 65535 : (int)value;
                _state = State.CsiParam;
                return;
            }

            if (c == ';')
            {
                if (_paramCount == 0)
                    _paramCount = 1;
                if (_paramCount < MaxParams)
                {
                    _params[_paramCount] = 0;
                    _paramCount++;
                }
                _state = State.CsiParam;
                return;
            }

            if (c >= 0x20 && c <= 0x2f)
            {
                _intermediate = c;
                _state = State.CsiIntermediate;
                return;
            }

            if (c >= 0x40 && c <= 0x7e)
            {
                int count = _paramCount == 0 ? 0 : _paramCount;
                _handler.CsiDispatch(c, _params, count, _prefix, _intermediate);
                _state = State.Ground;
                return;
            }

            // Anything else is invalid — ignore until the sequence terminates.
            _state = State.CsiIgnore;
        }

        private void Osc(char c)
        {
            if (_oscEscPending)
            {
                _oscEscPending = false;
                if (c == '\\')
                {
                    FinishOsc();
                    return;
                }
                // Lone ESC inside OSC — terminate and reprocess this char from ground.
                FinishOsc();
                Process(c);
                return;
            }

            if (c == 0x07) // BEL terminator
            {
                FinishOsc();
                return;
            }
            if (c == '\x1b') // possible ST (ESC \)
            {
                _oscEscPending = true;
                return;
            }
            if (_oscBuffer.Length < 4096)
                _oscBuffer.Append(c);
        }

        private void FinishOsc()
        {
            string data = _oscBuffer.ToString();
            _oscBuffer.Length = 0;
            _state = State.Ground;

            int command = 0;
            int semi = data.IndexOf(';');
            string payload;
            if (semi >= 0)
            {
                int.TryParse(data.Substring(0, semi), out command);
                payload = data.Substring(semi + 1);
            }
            else
            {
                int.TryParse(data, out command);
                payload = string.Empty;
            }
            _handler.OscDispatch(command, payload);
        }

        private void DcsIgnore(char c)
        {
            if (_oscEscPending)
            {
                _oscEscPending = false;
                if (c == '\\')
                {
                    _state = State.Ground;
                    return;
                }
            }
            if (c == 0x07)
            {
                _state = State.Ground;
                return;
            }
            if (c == '\x1b')
                _oscEscPending = true;
        }

        private void ResetParams()
        {
            _paramCount = 0;
            _prefix = '\0';
            _intermediate = '\0';
            for (int i = 0; i < MaxParams; i++)
                _params[i] = 0;
        }
    }
}
