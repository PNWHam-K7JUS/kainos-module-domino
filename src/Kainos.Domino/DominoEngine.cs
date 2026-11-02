/*  DominoEngine.cs

A thread that takes the receiver's audio from Kainos (IKainosAudio), brings it to 8000 a second for the waterfall
(and the 8 kHz modes) and to 11025 for the modes defined at that rate, and feeds the receiver at the clicked
frequency. It also feeds the transmitter's audio to Kainos while sending.

This file is part of the Kainos DominoEX / THOR module. Copyright (C) 2026 Justin Cron K7JUS. GPLv3 or later.
*/

using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using Kainos.Modules;

namespace Kainos.Domino
{
    internal sealed class DominoEngine
    {
        private readonly IKainosHost _host;
        private Thread _thread;
        private volatile bool _run;
        private volatile bool _tapOk, _lost;
        private int _tapRx = -1;

        public volatile int Rx;
        public event Action<string> Status;
        public event Action RxLost;

        // what the UI sets (picked up by the engine's thread)
        public volatile DominoMode Mode = DominoMode.ByName("DominoEX 16");
        public double RxFreq = 1000, TxFreq = 1000;
        public volatile bool TxLock;                // the transmit frequency stays put when the receive one moves
        public volatile int Squelch = 25;
        public volatile bool UserReverse;           // on top of the sideband's own

        private IfkRx _rx;
        private readonly Resampler _rs = new Resampler(), _rs11 = new Resampler(11025);
        private readonly float[] _buf = new float[16384], _a8 = new float[4096], _a11 = new float[5120];

        public DominoEngine(IKainosHost host) { _host = host; }

        // the tones are the other way up on the lower sideband
        public bool Reverse
        {
            get
            {
                string m = (_host.Radio.Mode(Rx) ?? "").ToUpperInvariant();
                bool lsb = m == "LSB" || m == "DIGL" || m == "CWL";
                return lsb ^ UserReverse;
            }
        }

        public void Start()
        {
            if (_thread != null) return;
            _run = true;
            _thread = new Thread(work) { IsBackground = true, Name = "Kainos DominoEX / THOR" };
            _thread.Start();
        }

        public void Stop()
        {
            _run = false;
            if (_thread != null && !_thread.Join(1500)) _host.Log("engine thread didn't stop in time");
            _thread = null;
            endTx();
            _host.Audio.ReleaseRx();
            _tapOk = false;
        }

        public void Listen() { _lost = false; _tapOk = false; }
        public bool Listening { get { return _tapOk; } }
        public bool Lost { get { return _lost; } }

        // moves the receiver (and the transmitter, unless it's locked), snapping to the nearest signal within reach
        public void TuneTo(double hz, bool snap) { _tune = hz; _tuneSnap = snap; }
        private volatile bool _tuneSnap;
        private double _tune = -1;

        private bool ensureTap()
        {
            int rx = Rx;
            if (_tapOk && _tapRx == rx) return true;
            if (_lost) return false;
            string why;
            if (!_host.Audio.AcquireRx(rx, () => { _tapOk = false; _lost = true; RxLost?.Invoke(); }, out why))
            {
                Status?.Invoke("Can't listen to RX" + (rx + 1) + ": " + why);
                _lost = true;
                return false;
            }
            _tapOk = true;
            _tapRx = rx;
            return true;
        }

        private void work()
        {
            while (_run)
            {
                try
                {
                    if (Sending || _pendingTx != null) feedTx();
                    if (ensureTap())
                    {
                        int rate = _host.Audio.RxRate, n;
                        while (rate > 0 && (n = _host.Audio.ReadRx(_buf, Math.Min(_buf.Length, rate / 10 * 4))) > 0)
                        {
                            int m = _rs.Process(_buf, n, rate, _a8);
                            int m11 = Mode.Rate == 11025 ? _rs11.Process(_buf, n, rate, _a11) : 0;
                            if (m > 0) receive(_a8, m, _a11, m11);
                        }
                    }
                }
                catch (Exception ex) { _host.Log("engine: " + ex); Thread.Sleep(200); }
                Thread.Sleep(Sending ? 10 : 20);
            }
        }

        // ---- receive ----

        // what the main receiver decoded (and what was sent, marked), for the UI to take
        public sealed class TextPiece { public string Text; public bool Tx; }
        private readonly Queue<TextPiece> _text = new Queue<TextPiece>();
        public TextPiece[] TakeText() { lock (_text) { TextPiece[] a = _text.ToArray(); _text.Clear(); return a; } }
        private void put(string s, bool tx)
        {
            lock (_text)
            {
                if (_text.Count > 0) { TextPiece last = null; foreach (TextPiece p in _text) last = p; if (last.Tx == tx) { last.Text += s; return; } }
                _text.Enqueue(new TextPiece { Text = s, Tx = tx });
            }
        }

        public double Quality { get { IfkRx r = _rx; return r == null ? 0 : r.Metric; } }
        public double SnrDb { get { IfkRx r = _rx; return r == null ? 0 : r.SnrDb; } }
        public bool Dcd { get { IfkRx r = _rx; return r != null && r.Dcd; } }

        private void receive(float[] a, int n, float[] am, int nm)
        {
            spectrum(a, n);
            if (Sending || DateTime.UtcNow < _rxMuteUntil) return;      // our own signal, or nothing
            DominoMode mode = Mode;
            double t = _tune;
            if (t >= 0)
            {
                _tune = -1;
                if (_tuneSnap) t = snap(t, mode.Bandwidth * 0.75);
                RxFreq = t;
                if (!TxLock) TxFreq = t;
                _rx = null;
            }
            if (_rx == null || _rx.Mode != mode || Math.Abs(_rx.Frequency - RxFreq) > 0.01) _rx = newRx(mode, RxFreq);
            _rx.Squelch = Squelch;
            _rx.Reverse = Reverse;
            if (mode.Rate == Resampler.OutRate) _rx.Process(a, n);
            else _rx.Process(am, nm);
        }

        private IfkRx newRx(DominoMode mode, double hz)
        {
            IfkRx r = new IfkRx(mode, hz);
            r.Text += x => put(x, false);
            return r;
        }

        // ---- the waterfall: an 8192-point FFT (about 1 Hz a bin), a line every 100 ms ----

        public const int FftN = 8192;
        public const double BinHz = Resampler.OutRate / (double)FftN;
        public const double SpecHi = 3000;
        public static readonly int SpecBins = (int)(SpecHi / BinHz);
        private readonly float[] _ring = new float[FftN];
        private int _ringPos, _since;
        private readonly double[] _win = hann(FftN);
        private readonly double[] _avg = new double[(int)(SpecHi / BinHz)];
        private readonly Queue<float[]> _specLines = new Queue<float[]>();
        public volatile float PeakDb = -120;
        private double _peak;

        private static double[] hann(int n) { double[] w = new double[n]; for (int i = 0; i < n; i++) w[i] = 0.5 - 0.5 * Math.Cos(2 * Math.PI * i / (n - 1)); return w; }

        public float[][] TakeLines() { lock (_specLines) { float[][] l = _specLines.ToArray(); _specLines.Clear(); return l; } }

        private void spectrum(float[] a, int n)
        {
            for (int i = 0; i < n; i++)
            {
                _ring[_ringPos] = a[i];
                if (++_ringPos == FftN) _ringPos = 0;
                double av = Math.Abs(a[i]);
                if (av > _peak) _peak = av;
            }
            _since += n;
            if (_since < Resampler.OutRate / 10) return;
            _since = 0;
            PeakDb = (float)(20 * Math.Log10(_peak + 1e-6));
            _peak = 0;
            double[] re = new double[FftN], im = new double[FftN];
            for (int i = 0; i < FftN; i++) re[i] = _ring[(_ringPos + i) % FftN] * _win[i];
            Fft.Transform(re, im);
            float[] line = new float[SpecBins];
            for (int b = 0; b < SpecBins; b++)
            {
                double p = (re[b] * re[b] + im[b] * im[b]) / ((double)FftN * FftN);
                _avg[b] = 0.7 * _avg[b] + 0.3 * p;
                line[b] = (float)(10 * Math.Log10(p + 1e-16));
            }
            lock (_specLines) { _specLines.Enqueue(line); while (_specLines.Count > 50) _specLines.Dequeue(); }
        }

        // the centre of the strongest signal within reach of a click (or the click itself, if there's nothing there)
        private double snap(double hz, double reach)
        {
            int c = (int)Math.Round(hz / BinHz), r = Math.Max(2, (int)(reach / BinHz));
            int lo = Math.Max(1, c - r), hi = Math.Min(_avg.Length - 2, c + r);
            if (hi <= lo) return hz;
            // the power-weighted centre over the window, around the strongest part
            double best = 0; int bi = c;
            for (int i = lo; i <= hi; i++) if (_avg[i] > best) { best = best < _avg[i] ? _avg[i] : best; bi = i; }
            double[] sorted = new double[hi - lo + 1];
            Array.Copy(_avg, lo, sorted, 0, sorted.Length);
            Array.Sort(sorted);
            if (best < sorted[sorted.Length / 4] * 8) return hz;       // nothing much there
            double sw = 0, s = 0;
            for (int i = lo; i <= hi; i++) { double w = Math.Max(0, _avg[i] - sorted[sorted.Length / 4]); sw += w; s += w * i; }
            return sw > 0 ? s / sw * BinHz : hz;
        }

        // ---- transmit ----
        // As the SSTV and Digital Modes modules do it: the feed started, then MOX; about 0.25 s kept queued; stopped by
        // Abort, by the radio being un-keyed elsewhere, or if MOX never comes; then the feed stopped and MOX off.

        public double LevelDb = -10;
        public volatile bool Sending;
        public volatile bool Tuning;
        public event Action TxEnded;

        private volatile IfkTx _pendingTx;
        private IfkTx _tx;
        private float[] _tune30;
        private int _tunePos;
        private volatile bool _abort;
        private bool _moxSeen;
        private DateTime _moxAskedAt, _rxMuteUntil;
        private int _txRate;
        private int _lead;

        // starts sending (the text so far, and whatever's added); returns why not, or null
        public string StartTx(string text)
        {
            if (!_host.Radio.PowerOn) return "The radio is off";
            if (_thread == null) return "DominoEX / THOR isn't open";
            int rate = _host.Audio.TxRate;
            if (rate <= 0) return "No transmit audio stream";
            IfkTx tx = _tx;
            if (Sending && tx != null && !Tuning)
            {
                if (tx.Finishing) return "Finishing the last transmission";
                tx.Add(text);
                return null;
            }
            if (Sending) return "Already sending";
            tx = new IfkTx(Mode, TxFreq, Reverse, rate);
            tx.Sent += c => put(c.ToString(), true);
            tx.Add(text);
            _pendingTx = tx;
            return null;
        }

        // adds text to what's going out (typed while sending)
        public void AddText(string s) { IfkTx tx = _tx ?? _pendingTx; if (tx != null && !tx.Finishing) tx.Add(s); }

        // finish: send what's queued, the postamble, then back to receive
        public void Finish() { IfkTx tx = _tx ?? _pendingTx; if (tx != null) tx.Finish(); if (Tuning) _abort = true; }

        public bool Finishing { get { IfkTx tx = _tx; return tx != null && tx.Finishing; } }
        public int TxQueuedChars { get { IfkTx tx = _tx ?? _pendingTx; return tx == null ? 0 : tx.Queued; } }

        // stop at once: what's queued is dropped
        public void Abort() { _abort = true; _pendingTx = null; }

        // a steady carrier at the transmit frequency (up to 30 s) to set the drive
        public string Tune()
        {
            if (Sending) return "Already sending";
            if (!_host.Radio.PowerOn) return "The radio is off";
            if (_thread == null) return "DominoEX / THOR isn't open";
            int rate = _host.Audio.TxRate;
            if (rate <= 0) return "No transmit audio stream";
            float[] t = new float[rate * 30];
            double step = 2 * Math.PI * TxFreq / rate;
            for (int i = 0; i < t.Length; i++)
            {
                double env = Math.Min(1, Math.Min(i, t.Length - i) / (rate * 0.005));
                t[i] = (float)(Math.Sin(step * i) * env);
            }
            _tune30 = t;
            Tuning = true;
            _pendingTx = new IfkTx(Mode, TxFreq, false, rate);        // a stand-in: the tone is sent instead of its audio
            return null;
        }

        private void feedTx()
        {
            IfkTx pend = _pendingTx;
            if (pend != null && !Sending)
            {
                _pendingTx = null;
                if (!_host.Radio.PowerOn) { Status?.Invoke("The radio is off"); Tuning = false; return; }
                _tx = pend;
                _tunePos = 0;
                _txRate = _host.Audio.TxRate;
                _abort = false;
                _moxSeen = false;
                _moxAskedAt = DateTime.UtcNow;
                _lead = (int)(0.15 * _txRate);           // silence first, for the radio to switch over
                Sending = true;
                _host.Audio.TxStart();
                _host.BeginInvoke(() => _host.Radio.SetMox(true));
                _host.Log((Tuning ? "tune" : "TX " + pend.Mode.Name) + " at " + pend.Frequency.ToString("0") + " Hz, " + LevelDb.ToString("0") + " dB");
            }
            if (!Sending || _tx == null) return;
            bool keyed = _host.Radio.Transmitting;
            if (keyed) _moxSeen = true;
            if (_abort || (_moxSeen && !keyed) || (!_moxSeen && (DateTime.UtcNow - _moxAskedAt).TotalSeconds > 2))
            {
                if (!_moxSeen && !_abort) Status?.Invoke("The radio didn't go into transmit");
                else if (!_abort) Status?.Invoke("Transmit stopped: the radio was un-keyed");
                if (_abort) _host.Audio.TxClear();
                endTx();
                return;
            }
            float amp = (float)Math.Pow(10, LevelDb / 20.0);
            int ahead = Math.Max(2048, _txRate / 4);
            float[] part = new float[1024];
            bool done = false;
            while (_host.Audio.TxQueued < ahead)
            {
                int n;
                if (_lead > 0) { n = Math.Min(part.Length, _lead); Array.Clear(part, 0, n); _lead -= n; }
                else if (Tuning)
                {
                    n = Math.Min(part.Length, _tune30.Length - _tunePos);
                    for (int i = 0; i < n; i++) part[i] = _tune30[_tunePos + i] * amp;
                    _tunePos += n;
                    if (n == 0) { done = true; break; }
                }
                else
                {
                    n = _tx.Generate(part, part.Length);
                    for (int i = 0; i < n; i++) part[i] *= amp;
                    if (n == 0) { done = true; break; }
                }
                int w = _host.Audio.TxWrite(part, n);
                if (w < n) break;
            }
            if (done && _host.Audio.TxQueued == 0) endTx();
        }

        private void endTx()
        {
            if (!Sending) return;
            Sending = false;
            Tuning = false;
            _tx = null;
            _host.Audio.TxStop();
            _host.BeginInvoke(() => _host.Radio.SetMox(false));
            _rxMuteUntil = DateTime.UtcNow.AddSeconds(0.5);
            put("\n", true);
            TxEnded?.Invoke();
        }
    }
}
