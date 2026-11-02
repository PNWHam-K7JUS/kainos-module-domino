/*  DominoPane.cs

The DominoEX / THOR window: a bar (mode, receiver, squelch, reverse, pop out, options), the waterfall (click a
signal to read it), what's received (and what's sent, in red), the box you type into (what's typed while sending
goes straight out), and the F1 - F8 messages with the QSO's callsign, name and report, and Log QSO.

Keys while the window has focus: F1 - F8 the messages, Ctrl+T transmit, Ctrl+R back to receive when what's typed has
gone, Esc stop now.

This file is part of the Kainos DominoEX / THOR module. Copyright (C) 2026 Justin Cron K7JUS. GPLv3 or later.
*/

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows.Forms;
using Kainos.Modules;

namespace Kainos.Domino.UI
{
    internal class DominoPane : Panel, IKainosPaneHeight
    {
        private readonly IKainosHost _host;
        private readonly IKainosTheme _t;
        private readonly DominoEngine _e;

        private readonly KButton _mode, _rxSel, _sql, _rev, _pop, _options;
        private readonly KButton _txBtn, _rxBtn, _tuneBtn, _logBtn;
        private readonly KButton[] _macroBtn = new KButton[Macros.Count];
        private readonly Label _status, _callL, _nameL, _rstL;
        private readonly TextBox _call, _name, _rst;
        private readonly Waterfall _wf;
        private readonly RichTextBox _rxText;
        private readonly TextBox _txText;
        private readonly Timer _timer;
        private bool _built;

        private string _statusText = "";
        private DateTime _statusAt;
        private string _markersFor = "";
        private int _sentUpTo;                  // how much of the typing box has been handed to the transmitter
        private bool _rxAtBottom = true;
        private DateTime _qsoStart = DateTime.UtcNow;
        private bool _logAfterTx;

        public event Action PopToggled;

        public int PreferredHeight { get { return 340; } }
        public int MinimumHeight { get { return 220; } }

        private static readonly int[] SquelchSteps = { 0, 10, 25, 45, 65 };

        public DominoPane(IKainosHost host, DominoEngine engine)
        {
            _host = host;
            _t = host.Theme;
            _e = engine;
            BackColor = _t.Bg;
            DoubleBuffered = true;

            Func<string, string, KButton> btn = (text, tip) =>
            {
                KButton b = new KButton(_t, text);
                if (tip != null) new ToolTip().SetToolTip(b, tip);
                Controls.Add(b);
                return b;
            };
            _mode = btn("DominoEX 16", "The mode: DominoEX or THOR, and its speed");
            _rxSel = btn("RX1", "The receiver listened to: RX1 or RX2");
            _sql = btn("SQL", "Squelch: text shows only from a signal this clean (click to step: off, low, medium, high, very high)");
            _rev = btn("Rev", "Reverse: the tones the other way up, for a station on the other sideband. Lower sideband (LSB / DIGL) is allowed for already.");
            _pop = btn("Pop out", "Docked under the panadapter, or in its own window");
            _options = btn("Options", "Station, transmit level, display and logging");

            _txBtn = btn("TX", "Transmit what's in the box, then what you type (Ctrl+T)");
            _txBtn.OnColor = _t.Tx;
            _rxBtn = btn("RX", "Back to receive once what's typed has gone (Ctrl+R). Esc stops at once.");
            _tuneBtn = btn("Tune", "A steady carrier at the transmit frequency for up to 30 s, to set the drive (click again to stop)");
            _tuneBtn.OnColor = _t.Tx;
            _logBtn = btn("Log QSO", "Log this QSO (their callsign, name and report from the boxes)");
            for (int i = 0; i < Macros.Count; i++)
            {
                int ii = i;
                _macroBtn[i] = btn("", "F" + (i + 1) + ": send this message. Right-click to change it.");
                _macroBtn[i].FontPx = 10.5f;
                _macroBtn[i].MouseUp += (s, e) => { if (e.Button == MouseButtons.Right) editMacro(ii); else if (e.Button == MouseButtons.Left) sendMacro(ii); };
            }

            Func<string, Label> label = text =>
            {
                Label l = new Label { Text = text, ForeColor = _t.Dim, BackColor = _t.Bg, AutoSize = false, TextAlign = ContentAlignment.MiddleRight, Font = _t.Font(_t.S(10), true) };
                Controls.Add(l);
                return l;
            };
            _callL = label("CALL"); _nameL = label("NAME"); _rstL = label("RST");
            Func<TextBox> box = () =>
            {
                TextBox tb = new TextBox { BackColor = _t.Panel, ForeColor = _t.Text, BorderStyle = BorderStyle.FixedSingle, Font = _t.Font(_t.S(12), true) };
                Controls.Add(tb);
                return tb;
            };
            _call = box(); _call.CharacterCasing = CharacterCasing.Upper;
            _name = box();
            _rst = box(); _rst.Text = "599";

            _status = new Label { ForeColor = _t.Dim, BackColor = _t.Bg, AutoSize = false, TextAlign = ContentAlignment.MiddleLeft, Font = _t.Font(_t.S(11), false) };
            Controls.Add(_status);

            _wf = new Waterfall(_t);
            Controls.Add(_wf);
            _rxText = new RichTextBox
            {
                ReadOnly = true, BorderStyle = BorderStyle.None, BackColor = _t.Panel, ForeColor = _t.Text, Font = _t.Font(_t.S(13), false),
                DetectUrls = false, HideSelection = false, ScrollBars = RichTextBoxScrollBars.Vertical, WordWrap = true,
            };
            Controls.Add(_rxText);
            _txText = new TextBox
            {
                Multiline = true, AcceptsReturn = true, BorderStyle = BorderStyle.FixedSingle, BackColor = _t.Surface, ForeColor = _t.Text,
                Font = _t.Font(_t.S(13), false), ScrollBars = ScrollBars.None,
            };
            Controls.Add(_txText);

            wire();
            applySettings();
            refreshMacros();

            _timer = new Timer { Interval = 100 };
            _timer.Tick += (s, e) => tick();
            _timer.Start();
            _built = true;
            PerformLayout();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) { _timer.Stop(); _timer.Dispose(); }
            base.Dispose(disposing);
        }

        // ---- layout ----

        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e);
            if (!_built) return;
            int pad = _t.S(4), bh = _t.S(24), x = pad, y = pad;
            Action<Control, int> place = (c, w) => { c.SetBounds(x, y, _t.S(w), bh); x += _t.S(w) + pad; };
            place(_mode, 100); place(_rxSel, 44); x += pad;
            place(_sql, 66); place(_rev, 44);
            int right = Width - pad;
            _options.SetBounds(right - _t.S(64), y, _t.S(64), bh); right -= _t.S(64) + pad;
            _pop.SetBounds(right - _t.S(64), y, _t.S(64), bh); right -= _t.S(64) + pad;
            _status.SetBounds(x + pad, y, Math.Max(0, right - x - pad), bh);

            y += bh + pad;
            int bottomRows = 2 * bh + pad;                              // the messages, and the QSO boxes
            int txH = _t.S(46);
            int rest = Height - y - bottomRows - txH - 3 * pad;
            int wfH = Math.Max(_t.S(50), Math.Min(_t.S(150), rest * 30 / 100));
            _wf.SetBounds(pad, y, Width - 2 * pad, wfH);
            y += wfH + pad;
            int rxH = Math.Max(_t.S(30), rest - wfH);
            _rxText.SetBounds(pad, y, Width - 2 * pad, rxH);
            y += rxH + pad;

            int bw2 = _t.S(56);
            _txText.SetBounds(pad, y, Width - 2 * pad - 3 * (bw2 + pad), txH);
            int bx = Width - pad - 3 * bw2 - 2 * pad;
            _txBtn.SetBounds(bx, y, bw2, txH); bx += bw2 + pad;
            _rxBtn.SetBounds(bx, y, bw2, txH); bx += bw2 + pad;
            _tuneBtn.SetBounds(bx, y, bw2, txH);
            y += txH + pad;

            // the messages, sharing the width
            int mw = (Width - pad - Macros.Count * pad) / Macros.Count;
            for (int i = 0; i < Macros.Count; i++) _macroBtn[i].SetBounds(pad + i * (mw + pad), y, mw, bh);
            y += bh + pad;
            x = pad;
            place(_callL, 34); _call.SetBounds(x, y + 1, _t.S(96), bh - 2); x += _t.S(96) + pad * 2;
            place(_nameL, 40); _name.SetBounds(x, y + 1, _t.S(96), bh - 2); x += _t.S(96) + pad * 2;
            place(_rstL, 28); _rst.SetBounds(x, y + 1, _t.S(44), bh - 2); x += _t.S(44) + pad * 2;
            _logBtn.SetBounds(x, y, _t.S(72), bh);
        }

        // ---- wiring ----

        private void wire()
        {
            _mode.Click += (s, e) => modeMenu().Show(_mode, new Point(0, _mode.Height));
            _rxSel.Click += (s, e) =>
            {
                _e.Rx = 1 - _e.Rx;
                _host.SetSetting("rx", _e.Rx.ToString(CultureInfo.InvariantCulture));
                _e.Listen();
                _host.SetMarkers(1 - _e.Rx, null);
                _markersFor = "";
                applySettings();
            };
            _sql.Click += (s, e) =>
            {
                int i = Array.IndexOf(SquelchSteps, _e.Squelch);
                _host.SetSetting("sql", SquelchSteps[(i + 1) % SquelchSteps.Length].ToString(CultureInfo.InvariantCulture));
                applySettings();
            };
            _rev.Click += (s, e) => { _host.SetSetting("rev", _e.UserReverse ? "0" : "1"); applySettings(); };
            _pop.Click += (s, e) => PopToggled?.Invoke();
            _options.Click += (s, e) => optionsMenu().Show(_options, new Point(0, _options.Height));

            _wf.Clicked += (hz, b) =>
            {
                if (b == MouseButtons.Right)
                {
                    _e.TxLock = true;
                    _e.TxFreq = Math.Round(hz);
                    _host.SetSetting("txlock", "1");
                    status("Transmitting on " + _e.TxFreq.ToString("0") + " Hz (locked: Options to unlock)");
                }
                else if (b == MouseButtons.Left) _e.TuneTo(hz, true);
            };
            _wf.Nudged += d => _e.TuneTo(_e.RxFreq + d, false);

            _txBtn.Click += (s, e) => startTx();
            _rxBtn.Click += (s, e) => finishTx();
            _tuneBtn.Click += (s, e) =>
            {
                if (_e.Tuning) { _e.Abort(); return; }
                string why = _e.Tune();
                status(why ?? "Tune: a carrier at " + _e.TxFreq.ToString("0") + " Hz. Set the drive for full power with the ALC barely moving.");
            };
            _logBtn.Click += (s, e) => logQso();

            _txText.TextChanged += (s, e) => typed();
            _txText.KeyDown += (s, e) => { if (keys(e.KeyData)) { e.Handled = true; e.SuppressKeyPress = true; } };
            _rxText.KeyDown += (s, e) => { if (keys(e.KeyData)) { e.Handled = true; e.SuppressKeyPress = true; } };
            _rxText.MouseDoubleClick += (s, e) => pickWord();
            _rxText.MouseUp += (s, e) => { if (e.Button == MouseButtons.Right) rxMenu().Show(_rxText, e.Location); };
            _rxText.VScroll += (s, e) => _rxAtBottom = atBottom();
            _call.TextChanged += (s, e) => { if (_call.Text.Trim().Length > 0 && _call.Focused) _qsoStart = DateTime.UtcNow; };

            _e.Status += s => _host.BeginInvoke(() => status(s));
            _e.TxEnded += () => _host.BeginInvoke(txEnded);
        }

        private void applySettings()
        {
            _e.Mode = DominoMode.ByName(_host.GetSetting("mode", "DominoEX 16"));
            _e.Rx = _host.GetSetting("rx", "0") == "1" ? 1 : 0;
            int sql;
            _e.Squelch = int.TryParse(_host.GetSetting("sql", "25"), NumberStyles.Integer, CultureInfo.InvariantCulture, out sql) ? sql : 25;
            _e.UserReverse = _host.GetSetting("rev", "0") == "1";
            _e.TxLock = _host.GetSetting("txlock", "0") == "1";
            double lv;
            if (double.TryParse(_host.GetSetting("txlevel", "-10"), NumberStyles.Float, CultureInfo.InvariantCulture, out lv)) _e.LevelDb = lv;
            _mode.Text = _e.Mode.Name;
            _rxSel.Text = "RX" + (_e.Rx + 1);
            int si = Array.IndexOf(SquelchSteps, _e.Squelch);
            _sql.Text = si <= 0 ? "SQL off" : "SQL " + new[] { "", "low", "med", "high", "v.high" }[si];
            _sql.On = _e.Squelch > 0;
            _rev.On = _e.UserReverse;
            _wf.Width_Hz = _e.Mode.Bandwidth;
        }

        // the first time: start at the saved frequency
        public void Restore()
        {
            double hz;
            if (double.TryParse(_host.GetSetting("rxfreq", "1000"), NumberStyles.Float, CultureInfo.InvariantCulture, out hz) && hz > 100 && hz < 3500) _e.TuneTo(hz, false);
            if (double.TryParse(_host.GetSetting("txfreq", "1000"), NumberStyles.Float, CultureInfo.InvariantCulture, out hz) && hz > 100 && hz < 3500 && _e.TxLock) _e.TxFreq = hz;
        }

        private void refreshMacros()
        {
            for (int i = 0; i < Macros.Count; i++) _macroBtn[i].Text = "F" + (i + 1) + " " + Macros.Get(_host, i).Label;
        }

        // ---- the timer: the waterfall, text, browser, markers and status ----

        private void tick()
        {
            foreach (float[] line in _e.TakeLines()) _wf.AddLine(line, DominoEngine.BinHz);
            double rx = _e.RxFreq;
            _wf.RxHz = rx;
            _wf.TxHz = _e.TxFreq;
            _wf.TxLocked = _e.TxLock;
            _wf.Invalidate();

            foreach (DominoEngine.TextPiece p in _e.TakeText()) append(p.Text, p.Tx);

            bool sending = _e.Sending;
            _txBtn.On = sending && !_e.Tuning;
            _tuneBtn.On = _e.Tuning;
            _txBtn.Text = sending && !_e.Tuning ? (_e.Finishing ? "Ending" : "TX on") : "TX";
            _mode.Enabled = !sending;

            markers();
            string m = _host.Radio.Mode(_e.Rx) ?? "";
            if (_statusText.Length > 0 && (DateTime.UtcNow - _statusAt).TotalSeconds < 8) _status.Text = _statusText;
            else if (_e.Lost) _status.Text = "Another mode has the receiver's audio: click RX" + (_e.Rx + 1) + " to listen again";
            else
            {
                string warn = m.StartsWith("DIG") || m == "USB" || m == "LSB" ? "" : "   (the radio is in " + m + ": DominoEX and THOR want DIGU or USB)";
                _status.Text = (sending ? (_e.Tuning ? "Tune  " : "Sending  ") + _e.TxFreq.ToString("0") + " Hz" + (_e.TxQueuedChars > 0 ? ", " + _e.TxQueuedChars + " to go" : "")
                    : "RX " + rx.ToString("0") + " Hz   S/N " + _e.SnrDb.ToString("0") + " dB" + (_e.Dcd ? "   ●" : "")) + warn;
            }
            if (Math.Abs(rx - _savedRx) >= 1) { _savedRx = rx; _host.SetSetting("rxfreq", rx.ToString("0", CultureInfo.InvariantCulture)); }
            if (_e.TxLock && Math.Abs(_e.TxFreq - _savedTx) >= 1) { _savedTx = _e.TxFreq; _host.SetSetting("txfreq", _e.TxFreq.ToString("0", CultureInfo.InvariantCulture)); }
        }
        private double _savedRx, _savedTx;

        private void markers()
        {
            if (_host.GetSetting("markers", "1") != "1") { if (_markersFor != "off") { _host.SetMarkers(_e.Rx, null); _markersFor = "off"; } return; }
            bool lsb = (_host.Radio.Mode(_e.Rx) ?? "").EndsWith("L") || _host.Radio.Mode(_e.Rx) == "LSB";
            double sgn = lsb ? -1 : 1, rx = Math.Round(_e.RxFreq), tx = Math.Round(_e.TxFreq);
            string key = _e.Rx + ":" + sgn + ":" + rx + ":" + tx + ":" + _e.TxLock;
            if (key == _markersFor) return;
            _markersFor = key;
            List<KainosMarker> l = new List<KainosMarker> { new KainosMarker(sgn * rx, _e.Mode.Name, _t.Gold) };
            if (_e.TxLock && Math.Abs(tx - rx) > 2) l.Add(new KainosMarker(sgn * tx, "TX", _t.Tx));
            _host.SetMarkers(_e.Rx, l.ToArray());
        }

        public void ClearMarkers() { _host.SetMarkers(0, null); _host.SetMarkers(1, null); _markersFor = ""; }

        private void status(string s) { _statusText = s ?? ""; _statusAt = DateTime.UtcNow; _status.Text = _statusText; }

        // ---- received text ----

        private void append(string s, bool tx)
        {
            if (s.Length == 0) return;
            bool follow = _rxAtBottom;
            int selStart = _rxText.SelectionStart, selLen = _rxText.SelectionLength;
            foreach (string part in Regex.Split(s, "(\b)"))
            {
                if (part.Length == 0) continue;
                if (part == "\b")
                {
                    if (_rxText.TextLength > 0) { _rxText.Select(_rxText.TextLength - 1, 1); _rxText.ReadOnly = false; _rxText.SelectedText = ""; _rxText.ReadOnly = true; }
                    continue;
                }
                _rxText.Select(_rxText.TextLength, 0);
                _rxText.SelectionColor = tx ? _t.Tx : _t.Text;
                _rxText.AppendText(part);
            }
            if (_rxText.TextLength > 60000) { _rxText.Select(0, 20000); _rxText.ReadOnly = false; _rxText.SelectedText = ""; _rxText.ReadOnly = true; }
            if (follow || selLen == 0) { _rxText.Select(_rxText.TextLength, 0); _rxText.ScrollToCaret(); }
            else _rxText.Select(selStart, selLen);
        }

        private bool atBottom()
        {
            Point p = _rxText.GetPositionFromCharIndex(_rxText.TextLength);
            return p.Y < _rxText.ClientSize.Height + _t.S(20);
        }

        private static readonly Regex CallRx = new Regex(@"^(?=.*\d)(?=.*[A-Z])[A-Z0-9]{1,3}[0-9][A-Z0-9]{0,4}(/[A-Z0-9]{1,4})?$|^[A-Z0-9]{1,3}/[A-Z0-9]{3,8}$", RegexOptions.IgnoreCase);

        // a double-clicked word: a callsign goes to CALL, a name to NAME
        private void pickWord()
        {
            string w = _rxText.SelectedText.Trim().Trim('.', ',', ':', ';', '!', '?', '-', '"', '\'', '(', ')');
            if (w.Length < 2) return;
            if (CallRx.IsMatch(w) && w.Length >= 3) { setCall(w.ToUpperInvariant()); status("Their callsign: " + w.ToUpperInvariant()); }
            else if (Regex.IsMatch(w, @"^[A-Za-z]{2,12}$")) { _name.Text = char.ToUpperInvariant(w[0]) + w.Substring(1).ToLowerInvariant(); status("Their name: " + _name.Text); }
        }

        private void setCall(string c) { if (c != _call.Text) { _call.Text = c; _qsoStart = DateTime.UtcNow; } }

        private ContextMenuStrip rxMenu()
        {
            ContextMenuStrip m = new ContextMenuStrip();
            string w = _rxText.SelectedText.Trim();
            if (w.Length > 0 && w.Length < 16)
            {
                ToolStripMenuItem c = new ToolStripMenuItem("Their callsign: " + w.ToUpperInvariant());
                c.Click += (s, e) => setCall(w.ToUpperInvariant());
                m.Items.Add(c);
                ToolStripMenuItem n = new ToolStripMenuItem("Their name: " + w);
                n.Click += (s, e) => _name.Text = w;
                m.Items.Add(n);
                ToolStripMenuItem cp = new ToolStripMenuItem("Copy");
                cp.Click += (s, e) => { try { Clipboard.SetText(_rxText.SelectedText); } catch { } };
                m.Items.Add(cp);
                m.Items.Add(new ToolStripSeparator());
            }
            ToolStripMenuItem clr = new ToolStripMenuItem("Clear");
            clr.Click += (s, e) => _rxText.Clear();
            m.Items.Add(clr);
            ToolStripMenuItem save = new ToolStripMenuItem("Save to a file...");
            save.Click += (s, e) => saveText();
            m.Items.Add(save);
            m.Closed += (s, e) => BeginInvoke(new Action(m.Dispose));
            return m;
        }

        private void saveText()
        {
            try
            {
                string dir = Path.Combine(_host.DataFolder, "Text");
                Directory.CreateDirectory(dir);
                string path = Path.Combine(dir, DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + "Z_" + _e.Mode.Name + ".txt");
                File.WriteAllText(path, _rxText.Text.Replace("\n", "\r\n"));
                Process.Start("explorer.exe", "/select,\"" + path + "\"");
            }
            catch (Exception ex) { status("Couldn't save the text: " + ex.Message); }
        }

        // ---- sending ----

        private bool keys(Keys k)
        {
            if (k >= Keys.F1 && k <= Keys.F8) { sendMacro(k - Keys.F1); return true; }
            if (k == (Keys.Control | Keys.T)) { startTx(); return true; }
            if (k == (Keys.Control | Keys.R)) { finishTx(); return true; }
            if (k == Keys.Escape && _e.Sending) { abortTx(); return true; }
            return false;
        }

        private MacroFields fields()
        {
            return new MacroFields
            {
                MyCall = _host.Callsign ?? "", MyGrid = (_host.Grid ?? "").Length >= 4 ? _host.Grid.Substring(0, 4) : _host.Grid ?? "",
                MyName = _host.GetSetting("myname", ""), Call = _call.Text.Trim(), Name = _name.Text.Trim(), Rst = _rst.Text.Trim(), Mode = _e.Mode.Name,
            };
        }

        private bool checkStation()
        {
            if (string.IsNullOrWhiteSpace(_host.Callsign)) { status("Your callsign isn't set: Kainos Setup > DSP > FreeDV (RADE) holds it"); return false; }
            return true;
        }

        private void startTx()
        {
            if (_e.Sending && !_e.Tuning) return;
            if (!checkStation()) return;
            string text = _txText.Text.Replace("\r\n", "\n");
            string why = _e.StartTx(text);
            if (why != null) { status(why); return; }
            _sentUpTo = _txText.TextLength;
            _txText.Focus();
            _txText.Select(_txText.TextLength, 0);
        }

        private void finishTx()
        {
            if (!_e.Sending) return;
            _e.Finish();
        }

        private void abortTx()
        {
            _e.Abort();
            status("Stopped");
        }

        private void sendMacro(int i)
        {
            if (!checkStation()) return;
            Macro m = Macros.Get(_host, i);
            bool rx;
            string text = Macros.Expand(m.Text, fields(), out rx);
            if (m.Text.IndexOf("<CALL>", StringComparison.OrdinalIgnoreCase) >= 0 && _call.Text.Trim().Length == 0) { status("Their callsign first (double-click it in the text, or type it in CALL)"); return; }
            if (_e.Sending && !_e.Tuning)
            {
                if (_e.Finishing) { status("Finishing the last transmission"); return; }
                _e.AddText(text);
            }
            else
            {
                string why = _e.StartTx(text);
                if (why != null) { status(why); return; }
                _sentUpTo = _txText.TextLength;
            }
            if (rx) _e.Finish();
            if (m.Label.Trim() == "73" && _host.GetSetting("log73", "1") == "1" && _call.Text.Trim().Length > 0) _logAfterTx = true;
            _txText.Focus();
            _txText.Select(_txText.TextLength, 0);
        }

        // typing while sending: what's new at the end goes out; a backspace takes one back (sent as a backspace)
        private void typed()
        {
            if (!_e.Sending || _e.Tuning || _e.Finishing) return;
            string t = _txText.Text;
            if (t.Length > _sentUpTo) _e.AddText(t.Substring(_sentUpTo).Replace("\r\n", "\n").Replace("\r", ""));
            else if (t.Length < _sentUpTo) _e.AddText(new string('\b', _sentUpTo - t.Length));
            _sentUpTo = t.Length;
        }

        private void txEnded()
        {
            _txText.Clear();
            _sentUpTo = 0;
            if (_logAfterTx) { _logAfterTx = false; logQso(); }
        }

        // ---- logging ----

        private void logQso()
        {
            DominoQso q = new DominoQso
            {
                Call = _call.Text.Trim(), Name = _name.Text.Trim(), RstSent = _rst.Text.Trim(), Mode = _e.Mode.AdifMode, Submode = _e.Mode.AdifSubmode,
                On = _qsoStart, Off = DateTime.UtcNow, DialHz = _host.Radio.VfoHz(_e.Rx), Power = _host.GetSetting("power", ""),
            };
            using (LogQsoForm f = new LogQsoForm(_host, q))
            {
                if (f.ShowDialog(FindForm()) != DialogResult.OK) return;
                DominoQso r = f.Qso;
                try
                {
                    string rec = DominoLog.Append(DominoLog.FileOf(_host.DataFolder), r, _host.Callsign, _host.Grid);
                    string why = _host.GetSetting("udp.on", "1") == "1" ? DominoLog.TellLoggers(_host.GetSetting("udp", "127.0.0.1:2237"), rec) : null;
                    status("Logged " + r.Call + (why != null ? " (logging programs not told: " + why + ")" : ""));
                    _host.Log("logged " + r.Call + " " + r.Mode + " " + r.Submode);
                    _call.Text = ""; _name.Text = ""; _rst.Text = "599";
                }
                catch (Exception ex) { status("Couldn't log it: " + ex.Message); }
            }
        }

        // ---- menus ----

        private ContextMenuStrip modeMenu()
        {
            ContextMenuStrip m = new ContextMenuStrip();
            bool? thor = null;
            foreach (DominoMode mode in DominoMode.All)
            {
                if (mode.Thor != thor)
                {
                    if (thor != null) m.Items.Add(new ToolStripSeparator());
                    thor = mode.Thor;
                    m.Items.Add(new ToolStripMenuItem(mode.Thor ? "THOR (FEC)" : "DominoEX") { Enabled = false });
                }
                DominoMode mm = mode;
                ToolStripMenuItem it = new ToolStripMenuItem(mode.Name + "   " + mode.Baud.ToString("0.##", CultureInfo.InvariantCulture) + " baud, " + mode.Bandwidth.ToString("0", CultureInfo.InvariantCulture) + " Hz wide") { Checked = mode == _e.Mode };
                it.Click += (s, e) => { _host.SetSetting("mode", mm.Name); applySettings(); status(mm.Name); };
                m.Items.Add(it);
            }
            m.Closed += (s, e) => BeginInvoke(new Action(m.Dispose));
            return m;
        }

        private ContextMenuStrip optionsMenu()
        {
            ContextMenuStrip m = new ContextMenuStrip();
            Func<string, string, string, string, ToolStripMenuItem> check = (text, key, def, tip) =>
            {
                ToolStripMenuItem it = new ToolStripMenuItem(text) { Checked = _host.GetSetting(key, def) == "1", ToolTipText = tip };
                it.Click += (s, e) => { _host.SetSetting(key, it.Checked ? "0" : "1"); _markersFor = ""; applySettings(); };
                m.Items.Add(it);
                return it;
            };
            ToolStripMenuItem lockTx = new ToolStripMenuItem("Lock the transmit frequency") { Checked = _e.TxLock, ToolTipText = "Transmit where it is now, wherever you listen (a right click on the waterfall sets it). Off: transmit where you listen." };
            lockTx.Click += (s, e) => { _host.SetSetting("txlock", _e.TxLock ? "0" : "1"); applySettings(); if (!_e.TxLock) _e.TxFreq = _e.RxFreq; };
            m.Items.Add(lockTx);
            check("Show the frequencies on the panadapter", "markers", "1", "A line where you're listening (and transmitting, when locked)");
            check("Log window after sending the 73 message", "log73", "1", "Open Log QSO, filled in, when the F-key message labelled 73 has gone out");
            check("Send logged QSOs to logging programs", "udp.on", "1", "As WSJT-X does (UDP \"Logged ADIF\"), for Log4OM, N1MM+, HRD, DXKeeper, GridTracker and the Kainos Logbook");
            m.Items.Add(new ToolStripSeparator());
            ToolStripMenuItem lv = new ToolStripMenuItem("Transmit level") { ToolTipText = "Set with Tune: full power with the ALC barely moving" };
            foreach (int db in new[] { 0, -3, -6, -10, -15, -20, -25, -30 })
            {
                int d = db;
                ToolStripMenuItem it = new ToolStripMenuItem(db + " dB") { Checked = Math.Abs(_e.LevelDb - db) < 0.5 };
                it.Click += (s, e) => { _e.LevelDb = d; _host.SetSetting("txlevel", d.ToString(CultureInfo.InvariantCulture)); status("Transmit level " + d + " dB"); };
                lv.DropDownItems.Add(it);
            }
            m.Items.Add(lv);
            ToolStripMenuItem name = new ToolStripMenuItem("Your name (for <MYNAME>)...");
            name.Click += (s, e) =>
            {
                string v = Microsoft.VisualBasic.Interaction.InputBox("Your name, as the messages send it (<MYNAME>).", "DominoEX / THOR: your name", _host.GetSetting("myname", ""));
                if (v != null && v.Trim().Length > 0) { _host.SetSetting("myname", v.Trim()); status("Name: " + v.Trim()); }
            };
            m.Items.Add(name);
            ToolStripMenuItem pwr = new ToolStripMenuItem("Power for the log...");
            pwr.Click += (s, e) =>
            {
                string v = Microsoft.VisualBasic.Interaction.InputBox("The power to log (watts), or blank.", "DominoEX / THOR: power", _host.GetSetting("power", ""));
                if (v != null) _host.SetSetting("power", v.Trim());
            };
            m.Items.Add(pwr);
            m.Items.Add(new ToolStripSeparator());
            ToolStripMenuItem addr = new ToolStripMenuItem("Logging programs address...");
            addr.Click += (s, e) =>
            {
                string cur = _host.GetSetting("udp", "127.0.0.1:2237");
                string v = Microsoft.VisualBasic.Interaction.InputBox("Where logged QSOs are sent, as WSJT-X sends them (UDP host:port). Log4OM, N1MM+, HRD, DXKeeper (via JTAlert), GridTracker and the Kainos Logbook listen on 127.0.0.1:2237 unless set otherwise.", "DominoEX / THOR: logging programs", cur);
                if (string.IsNullOrWhiteSpace(v) || v == cur) return;
                if (!DominoLog.ValidAddress(v)) { status("Not changed: the address should be host:port, e.g. 127.0.0.1:2237"); return; }
                _host.SetSetting("udp", v.Trim());
                status("Logged QSOs go to " + v.Trim());
            };
            m.Items.Add(addr);
            ToolStripMenuItem logf = new ToolStripMenuItem("Open the log folder");
            logf.Click += (s, e) => { Directory.CreateDirectory(_host.DataFolder); Process.Start("explorer.exe", "\"" + _host.DataFolder + "\""); };
            m.Items.Add(logf);
            m.Closed += (s, e) => BeginInvoke(new Action(m.Dispose));
            return m;
        }

        private void editMacro(int i)
        {
            using (MacroForm f = new MacroForm(_host, i, Macros.Get(_host, i)))
            {
                if (f.ShowDialog(FindForm()) != DialogResult.OK || f.Macro == null) return;
                Macros.Set(_host, i, f.Macro);
                refreshMacros();
            }
        }
    }
}
