/*  FakeHost.cs (self-test)

A stand-in for Kainos, so the self-test can start the module, build its pane in a window and drive it.

This file is part of the Kainos DominoEX / THOR module. Copyright (C) 2026 Justin Cron K7JUS. GPLv3 or later.
*/

using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Windows.Forms;
using Kainos.Modules;

namespace Kainos.Domino.SelfTest
{
    internal class FakeHost : IKainosHost, IKainosRadio, IKainosAudio, IKainosTheme
    {
        public readonly Dictionary<string, string> Settings = new Dictionary<string, string>();
        public readonly List<string> LogLines = new List<string>();
        public readonly List<FakeMenu> Menus = new List<FakeMenu>();
        public readonly List<FakePane> Panes = new List<FakePane>();
        public Form Window;
        public bool Mox;
        public double Vfo = 14230000;
        public readonly List<string> MarkerLog = new List<string>();
        public string ModeName = "DIGU";

        public FakeHost(string folder, string data) { ModuleFolder = folder; DataFolder = data; Directory.CreateDirectory(data); }

        public int ApiVersion { get { return KainosModuleApi.Version; } }
        public string KainosVersion { get { return "test"; } }
        public string ModuleId { get { return "psk"; } }
        public string ModuleFolder { get; private set; }
        public string DataFolder { get; private set; }
        public string Callsign { get { return "K7JUS"; } }
        public string Grid { get { return "CN87"; } }
        public IKainosRadio Radio { get { return this; } }
        public IKainosAudio Audio { get { return this; } }
        public IKainosTheme Theme { get { return this; } }

        public IKainosMenuItem AddMenuItem(string text, string toolTip, Action click) { FakeMenu m = new FakeMenu { Text = text, Click = click }; Menus.Add(m); return m; }
        public IKainosPane CreatePane(string title, Control content) { FakePane p = new FakePane(this, content); Panes.Add(p); return p; }
        public void SetMarkers(int rx, KainosMarker[] markers) { MarkerLog.Add(rx + ":" + (markers == null ? "none" : string.Join(",", Array.ConvertAll(markers, m => m.OffsetHz.ToString())))); }
        public string GetSetting(string key, string fallback) { string v; return Settings.TryGetValue(key, out v) ? v : fallback; }
        public void SetSetting(string key, string value) { Settings[key] = value; }
        public void Log(string message) { lock (LogLines) LogLines.Add(message); }
        public void BeginInvoke(Action action)
        {
            if (Window != null && Window.IsHandleCreated) Window.BeginInvoke(action);
            else if (Window == null) action();          // no window (the timed QSO test): at once
        }

        public bool PowerOn { get { return true; } }
        public bool Transmitting { get { return Mox; } }
        public int TxReceiver { get { return 0; } }
        public double VfoHz(int rx) { return Vfo; }
        public void TuneVfo(int rx, double hz) { Vfo = hz; }
        public string Mode(int rx) { return ModeName; }
        public void SetMode(int rx, string mode) { ModeName = mode; }
        public void SetMox(bool on) { Mox = on; }

        // the receiver's audio: what's queued, played at Speed times real time (and silence once it runs out)
        public double Speed = 1;
        private readonly Queue<float> _air = new Queue<float>();
        private System.Diagnostics.Stopwatch _rxClock;
        private long _rxRead;
        public void Play(float[] a) { lock (_air) foreach (float s in a) _air.Enqueue(s); }
        public bool AirEmpty { get { lock (_air) return _air.Count == 0; } }

        // the stand-in for another digital mode (FT8, RTTY...) taking the receiver's audio
        private Action _released;
        public int Acquires;
        public void TakeAway() { Action a = _released; _released = null; if (a != null) a(); }
        public bool AcquireRx(int rx, Action released, out string problem) { problem = null; _released = released; Acquires++; _rxClock = System.Diagnostics.Stopwatch.StartNew(); _rxRead = 0; return true; }
        public void ReleaseRx() { _rxClock = null; }
        public int RxRate { get { return 48000; } }
        public int ReadRx(float[] dst, int max)
        {
            if (_rxClock == null) return 0;
            long due = (long)(_rxClock.Elapsed.TotalSeconds * Speed * RxRate);
            int n = (int)Math.Min(Math.Min(max, dst.Length), due - _rxRead);
            if (n <= 0) return 0;
            lock (_air) for (int i = 0; i < n; i++) dst[i] = (_air.Count > 0 ? _air.Dequeue() * 0.5f : 0f) + (float)(Program.Gauss() * 0.02);      // and a little band noise
            _rxRead += n;
            return n;
        }

        public int TxRate { get { return 48000; } }
        // transmit: what's written is kept (TxAudio) and drains at Speed times real time, as the radio takes it
        public readonly List<float> TxAudio = new List<float>();
        private System.Diagnostics.Stopwatch _txClock;
        private long _txWritten;
        private bool _txOn;
        public void TxStart() { _txOn = true; _txClock = System.Diagnostics.Stopwatch.StartNew(); _txWritten = 0; }
        public int TxWrite(float[] src, int n) { if (!_txOn) return 0; lock (TxAudio) for (int i = 0; i < n; i++) TxAudio.Add(src[i]); _txWritten += n; return n; }
        public int TxQueued { get { return _txOn ? (int)Math.Max(0, _txWritten - _txClock.Elapsed.TotalSeconds * Speed * TxRate) : 0; } }
        public void TxClear() { }
        public void TxStop() { _txOn = false; }

        public Color Bg { get { return Color.FromArgb(0x05, 0x0b, 0x13); } }
        public Color Surface { get { return Color.FromArgb(0x0a, 0x15, 0x20); } }
        public Color Panel { get { return Color.FromArgb(0x0e, 0x1b, 0x29); } }
        public Color Raised { get { return Color.FromArgb(0x14, 0x25, 0x38); } }
        public Color Line { get { return Color.FromArgb(0x1f, 0x33, 0x46); } }
        public Color Text { get { return Color.FromArgb(0xe9, 0xee, 0xf3); } }
        public Color Dim { get { return Color.FromArgb(0x94, 0xa7, 0xb2); } }
        public Color Faint { get { return Color.FromArgb(0x5f, 0x75, 0x84); } }
        public Color Ice { get { return Color.FromArgb(0x7f, 0xb0, 0xcc); } }
        public Color Gold { get { return Color.FromArgb(0xd4, 0xad, 0x6a); } }
        public Color Violet { get { return Color.FromArgb(0xb5, 0xa8, 0xe0); } }
        public Color Tx { get { return Color.FromArgb(0xe2, 0x57, 0x4c); } }
        public Color Selected { get { return Color.FromArgb(0x1d, 0x2a, 0x33); } }
        public float Scale { get { return 1f; } }
        public int S(float designPx) { return (int)Math.Round(designPx); }
        public Font Font(float px, bool bold) { return new Font("Segoe UI", px, bold ? FontStyle.Bold : FontStyle.Regular, GraphicsUnit.Pixel); }
        public void DrawButton(Graphics g, RectangleF r, string text, bool on, bool enabled, bool hover, float fontPx)
        {
            using (Brush b = new SolidBrush(on ? Selected : Raised)) g.FillRectangle(b, r);
            using (Font f = Font(fontPx, true)) TextRenderer.DrawText(g, text, f, Rectangle.Round(r), on ? Gold : Dim);
        }
        public void Style(Control root) { root.BackColor = Bg; root.ForeColor = Text; }
    }

    internal class FakeMenu : IKainosMenuItem
    {
        public Action Click;
        public bool Checked { get; set; }
        public bool Enabled { get; set; } = true;
        public string Text { get; set; }
        public void Remove() { }
    }

    internal class FakePane : IKainosPane
    {
        private readonly FakeHost _h;
        public readonly Control Content;
        public FakePane(FakeHost h, Control c) { _h = h; Content = c; }
        public bool IsOpen { get; private set; }
        public bool Popped { get; set; }
        public event EventHandler Closed;
        public void Open()
        {
            IsOpen = true;
            Content.Dock = DockStyle.Fill;
            if (Content.Parent != _h.Window) _h.Window.Controls.Add(Content);
        }
        public void Close() { IsOpen = false; if (Closed == null) { } }
    }
}
