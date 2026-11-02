/*  Controls.cs

Controls drawn in the Kainos look (IKainosTheme): a push button and the waterfall (click to tune).

This file is part of the Kainos DominoEX / THOR module. Copyright (C) 2026 Justin Cron K7JUS. GPLv3 or later.
*/

using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Kainos.Modules;

namespace Kainos.Domino.UI
{
    internal class KButton : Control
    {
        private readonly IKainosTheme _t;
        private bool _on, _hover;
        public float FontPx = 11;
        public Color? OnColor;              // lit in this colour rather than the theme's (TX: red)

        public KButton(IKainosTheme t, string text)
        {
            _t = t;
            Text = text;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            BackColor = t.Bg;
            Cursor = Cursors.Hand;
        }

        public bool On { get { return _on; } set { if (_on != value) { _on = value; Invalidate(); } } }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(BackColor);
            RectangleF r = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
            if (_on && OnColor.HasValue)
            {
                using (Brush b = new SolidBrush(Color.FromArgb(70, OnColor.Value))) e.Graphics.FillRectangle(b, r);
                using (Pen p = new Pen(OnColor.Value)) e.Graphics.DrawRectangle(p, r.X, r.Y, r.Width, r.Height);
                using (Font f = _t.Font(_t.S(FontPx), true))
                    TextRenderer.DrawText(e.Graphics, Text, f, Rectangle.Round(r), _t.Text, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                return;
            }
            _t.DrawButton(e.Graphics, r, Text, _on, Enabled, _hover, _t.S(FontPx));
        }

        protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnTextChanged(EventArgs e) { Invalidate(); base.OnTextChanged(e); }
        protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }
    }

    // The waterfall of the receiver's audio, 0 - 3000 Hz, with a scale along the top, the receive frequency (a gold
    // band the signal's width) and the transmit frequency (a red line). Left click: listen there; right click: transmit
    // there (locks it); the wheel: nudge the receiver 1 Hz.
    internal class Waterfall : Control
    {
        private readonly IKainosTheme _t;
        public double LoHz = 0, HiHz = 3000;
        public double RxHz = 1000, TxHz = 1000, Width_Hz = 50;
        public bool TxLocked;
        public float RangeDb = 30;
        public double[] Others;                 // the browser's signals, marked small along the scale

        public event Action<double, MouseButtons> Clicked;
        public event Action<int> Nudged;

        private Bitmap _wf;
        private int _row, _count;
        private float _floor = float.NaN;
        private int _hoverX = -1;

        public Waterfall(IKainosTheme t)
        {
            _t = t;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            BackColor = t.Bg;
            Cursor = Cursors.Cross;
        }

        protected override void Dispose(bool disposing) { if (disposing && _wf != null) { _wf.Dispose(); _wf = null; } base.Dispose(disposing); }

        private int scaleH { get { return _t.S(16); } }

        public double HzAt(int x) { return LoHz + (HiHz - LoHz) * x / Math.Max(1.0, Width - 1); }
        public float XOf(double hz) { return (float)((hz - LoHz) / (HiHz - LoHz) * (Width - 1)); }

        // a spectrum line (dB per FFT bin of binHz): onto the top of the waterfall
        public void AddLine(float[] db, double binHz)
        {
            int w = Math.Max(1, Width), h = Math.Max(1, Height - scaleH);
            if (_wf == null || _wf.Width != w || _wf.Height != h)
            {
                if (_wf != null) _wf.Dispose();
                _wf = new Bitmap(w, h, PixelFormat.Format32bppRgb);
                using (Graphics g = Graphics.FromImage(_wf)) g.Clear(_t.Bg);
                _count = 0; _row = 0;
            }
            // each pixel column: the strongest bin under it
            float[] col = new float[w];
            for (int x = 0; x < w; x++)
            {
                int b0 = (int)(HzAt(x) / binHz), b1 = Math.Max(b0, (int)(HzAt(x + 1) / binHz) - 1);
                float m = -200;
                for (int b = b0; b <= b1; b++) if (b >= 0 && b < db.Length && db[b] > m) m = db[b];
                col[x] = m;
            }
            float[] sorted = (float[])col.Clone();
            Array.Sort(sorted);
            float f = sorted[w * 3 / 10];
            _floor = float.IsNaN(_floor) ? f : _floor + 0.1f * (f - _floor);
            _row = (_row - 1 + h) % h;
            _count = Math.Min(h, _count + 1);
            int[] px = new int[w];
            for (int x = 0; x < w; x++) px[x] = colour((col[x] - _floor - 2) / RangeDb).ToArgb();
            BitmapData bd = _wf.LockBits(new Rectangle(0, _row, w, 1), ImageLockMode.WriteOnly, PixelFormat.Format32bppRgb);
            Marshal.Copy(px, 0, bd.Scan0, w);
            _wf.UnlockBits(bd);
        }

        // 0 (noise) to 1 (strong): navy, blue, ice, gold, white
        private Color colour(float v)
        {
            v = Math.Max(0, Math.Min(1, v));
            Color[] stops = { _t.Bg, Color.FromArgb(0x16, 0x3a, 0x5c), _t.Ice, _t.Gold, Color.White };
            float p = v * (stops.Length - 1);
            int i = Math.Min(stops.Length - 2, (int)p);
            float fr = p - i;
            Color a = stops[i], b = stops[i + 1];
            return Color.FromArgb((int)(a.R + (b.R - a.R) * fr), (int)(a.G + (b.G - a.G) * fr), (int)(a.B + (b.B - a.B) * fr));
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(_t.Bg);
            int sh = scaleH;
            // the scale
            using (Brush sb = new SolidBrush(_t.Surface)) g.FillRectangle(sb, 0, 0, Width, sh);
            using (Font f = _t.Font(_t.S(9), false))
            using (Brush tb = new SolidBrush(_t.Dim))
            using (Pen tick = new Pen(_t.Line))
            {
                for (int hz = (int)(Math.Ceiling(LoHz / 100) * 100); hz <= HiHz; hz += 100)
                {
                    float x = XOf(hz);
                    bool major = hz % 500 == 0;
                    g.DrawLine(tick, x, sh - (major ? _t.S(6) : _t.S(3)), x, sh);
                    if (major) g.DrawString(hz.ToString(), f, tb, x + 2, 0);
                }
            }
            if (Others != null)
                using (Brush ob = new SolidBrush(_t.Violet))
                    foreach (double o in Others) { float x = XOf(o); g.FillRectangle(ob, x - _t.S(2), sh - _t.S(4), _t.S(4), _t.S(4)); }
            // the waterfall, newest at the top
            if (_wf != null && _count > 0)
            {
                int h = _wf.Height, vis = Math.Min(Height - sh, _count);
                int a = Math.Min(vis, h - _row);
                g.SetClip(new Rectangle(0, sh, Width, Height - sh));
                g.DrawImage(_wf, new Rectangle(0, sh, _wf.Width, a), new Rectangle(0, _row, _wf.Width, a), GraphicsUnit.Pixel);
                if (vis > a) g.DrawImage(_wf, new Rectangle(0, sh + a, _wf.Width, vis - a), new Rectangle(0, 0, _wf.Width, vis - a), GraphicsUnit.Pixel);
                g.ResetClip();
            }
            // receive: a band the signal's width; transmit: a line
            float rx = XOf(RxHz), half = Math.Max(2, XOf(RxHz + Width_Hz / 2) - rx);
            using (Brush rb = new SolidBrush(Color.FromArgb(40, _t.Gold))) g.FillRectangle(rb, rx - half, sh, half * 2, Height - sh);
            using (Pen rp = new Pen(_t.Gold)) { g.DrawLine(rp, rx - half, sh, rx - half, Height); g.DrawLine(rp, rx + half, sh, rx + half, Height); }
            using (Brush rb = new SolidBrush(_t.Gold)) g.FillPolygon(rb, new[] { new PointF(rx - _t.S(5), 0), new PointF(rx + _t.S(5), 0), new PointF(rx, _t.S(7)) });
            if (TxLocked || Math.Abs(TxHz - RxHz) > 0.5)
            {
                float tx = XOf(TxHz);
                using (Pen tp = new Pen(_t.Tx, Math.Max(1, _t.S(1.5f))) { DashStyle = TxLocked ? DashStyle.Solid : DashStyle.Dash }) g.DrawLine(tp, tx, sh, tx, Height);
                using (Brush tb = new SolidBrush(_t.Tx)) g.FillPolygon(tb, new[] { new PointF(tx - _t.S(5), sh), new PointF(tx + _t.S(5), sh), new PointF(tx, sh + _t.S(7)) });
            }
            if (_hoverX >= 0)
            {
                using (Pen hp = new Pen(Color.FromArgb(110, _t.Text)) { DashStyle = DashStyle.Dot }) g.DrawLine(hp, _hoverX, sh, _hoverX, Height);
                using (Font f = _t.Font(_t.S(10), true))
                using (Brush tb = new SolidBrush(_t.Text))
                {
                    string s = HzAt(_hoverX).ToString("0") + " Hz";
                    SizeF sz = g.MeasureString(s, f);
                    float x = _hoverX + sz.Width + 8 > Width ? _hoverX - sz.Width - 4 : _hoverX + 4;
                    using (Brush bg = new SolidBrush(Color.FromArgb(170, _t.Bg))) g.FillRectangle(bg, x, sh + 2, sz.Width, sz.Height);
                    g.DrawString(s, f, tb, x, sh + 2);
                }
            }
        }

        protected override void OnMouseMove(MouseEventArgs e) { _hoverX = e.X; Invalidate(); base.OnMouseMove(e); }
        protected override void OnMouseLeave(EventArgs e) { _hoverX = -1; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e) { Clicked?.Invoke(HzAt(e.X), e.Button); base.OnMouseDown(e); }
        protected override void OnMouseWheel(MouseEventArgs e) { Nudged?.Invoke(e.Delta > 0 ? 1 : -1); base.OnMouseWheel(e); }
        protected override void OnMouseEnter(EventArgs e) { Focus(); base.OnMouseEnter(e); }
    }
}
