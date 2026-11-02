/*  Program.cs (self-test)

Checks the DominoEX / THOR modem without a radio: the varicodes and the THOR decoder, then every mode sent and
received (clean, with noise, mistuned, on the other sideband, at 44.1 kHz), then the transmit path through the engine,
and the window drawn with a signal playing.

  Kainos.Domino.SelfTest.exe [--ui]

This file is part of the Kainos DominoEX / THOR module. Copyright (C) 2026 Justin Cron K7JUS. GPLv3 or later.
*/

using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace Kainos.Domino.SelfTest
{
    internal static class Program
    {
        private static int _fail;
        private static readonly Random Rnd = new Random(42);

        private static void check(bool ok, string what)
        {
            Console.WriteLine((ok ? "PASS  " : "FAIL  ") + what);
            if (!ok) _fail++;
        }

        internal static double Gauss()
        {
            double u1 = 1.0 - Rnd.NextDouble(), u2 = Rnd.NextDouble();
            return Math.Sqrt(-2 * Math.Log(u1)) * Math.Cos(2 * Math.PI * u2);
        }

        [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool SetProcessDPIAware();

        private const string Msg = "CQ CQ de K7JUS K7JUS pse K. The quick brown fox jumps over the lazy dog 0123456789";
        private const string Short = "CQ de K7JUS K";

        [STAThread]
        private static int Main(string[] args)
        {
            SetProcessDPIAware();
            varicodes();
            foreach (DominoMode m in DominoMode.All)
            {
                string text = m.Baud < 5 ? Short : Msg;
                double c0 = Math.Max(1000, m.Bandwidth / 2 + 400);        // the widest need room below them
                string clean = loop(m, text, c0, c0, 99, 48000, false, false);
                check(clean.Contains(text), m.Name + " clean" + show(clean));
                if (m.Baud < 5) continue;                // the slowest: clean is enough (they take minutes)
                // noise in 2.5 kHz: these work well below 0 dB there (the signal is narrow and slow)
                double snr = -6 + 10 * Math.Log10(m.Bandwidth / 280.0);
                string noisy = loop(m, text, Math.Max(1500, c0), Math.Max(1500, c0), snr, 48000, false, false);
                check(noisy.Contains(text.Substring(10)), m.Name + " at " + snr.ToString("0") + " dB SNR in 2.5 kHz" + show(noisy));
                double off = 1.6 * m.ToneSpacing;
                string tuned = loop(m, text, c0 + 200 + off, c0 + 200, 99, 48000, false, false);
                check(tuned.Contains(text), m.Name + " mistuned by " + off.ToString("0.#") + " Hz (1.6 tones)" + show(tuned));
            }
            string rev = loop(DominoMode.ByName("DominoEX 16"), Msg, 1000, 1000, 99, 48000, true, true);
            check(rev.Contains(Msg), "DominoEX 16 reversed both ends" + show(rev));
            string wrong = loop(DominoMode.ByName("THOR 16"), Msg, 1000, 1000, 99, 48000, true, false);
            check(!wrong.Contains(Msg.Substring(0, 12)), "THOR 16 reversed one end only: not read" + show(wrong));
            string r44 = loop(DominoMode.ByName("THOR 22"), Msg, 900, 900, 99, 44100, false, false);
            check(r44.Contains(Msg), "THOR 22 (11025) at 44.1 kHz" + show(r44));
            string utf = loop(DominoMode.ByName("THOR 22"), "73 de Jörg, 5°C\nNext line", 1000, 1000, 99, 48000, false, false);
            check(utf.Contains("73 de Jörg, 5°C\nNext line"), "THOR 22 UTF-8 and new lines" + show(utf));
            string utfD = loop(DominoMode.ByName("DominoEX 22"), "73 de Jörg, 5°C\nNext line", 1000, 1000, 99, 48000, false, false);
            check(utfD.Contains("73 de Jörg, 5°C\nNext line"), "DominoEX 22 UTF-8 and new lines" + show(utfD));
            speed("THOR 25 x4");
            bandwidth();
            transmit("DominoEX 16");
            transmit("THOR 22");
            if (args.Contains("--ui")) ui();
            Console.WriteLine(_fail == 0 ? "All passed" : _fail + " failed");
            return _fail == 0 ? 0 : 1;
        }

        private static string show(string s)
        {
            s = s.Replace("\n", "\\n").Replace("\b", "\\b");
            return "   [" + (s.Length > 120 ? s.Substring(0, 60) + " ... " + s.Substring(s.Length - 60) : s) + "]";
        }

        private static void varicodes()
        {
            bool ok = true;
            for (int c = 0; c < 256; c++)
            {
                List<int> nib = new List<int>();
                DominoVaricode.Nibbles(c, false, nib);
                if ((nib[0] & 8) != 0 || nib.Skip(1).Any(x => (x & 8) == 0)) ok = false;
                int sym = 0;
                for (int i = 0; i < nib.Count; i++) sym = (sym << 4) | nib[i];
                if (DominoVaricode.Decode(sym) != c) { ok = false; Console.WriteLine("      " + c + " decodes as " + DominoVaricode.Decode(sym)); }
            }
            check(ok, "DominoEX varicode: 256 characters, first nibble top bit clear, the rest set, each read back");
            ok = true;
            for (int b = 0; b < 256; b++)
            {
                List<int> bits = new List<int>();
                MfskVaricode.Bits(b, bits);
                if (bits[0] != 1 || string.Concat(bits).Contains("001") || MfskVaricode.Lookup(MfskVaricode.Codes[b]) != b) ok = false;
            }
            check(ok, "MFSK varicode (THOR): 256 codes, each starting with 1, none holding 001, each read back");

            // THOR's code and interleaver: 4 coded bits a symbol, some symbols flipped whole
            foreach (ConvCode code in new[] { ConvCode.PskR, ConvCode.K15 })
            {
                ConvEncoder enc = new ConvEncoder(code);
                Viterbi v = new Viterbi(code, code.K == 15 ? 180 : 45);
                Interleaver ti = new Interleaver(4, 10, false), ri = new Interleaver(4, 10, true);
                int n = 3000, errs = 0, hits = 0, bits = 0, count = 0;
                int[] sent = new int[n];
                List<int> got = new List<int>();
                byte first = 0;
                bool have = false;
                for (int i = 0; i < n + 800; i++)
                {
                    int b = i < n ? Rnd.Next(2) : 0;
                    if (i < n) sent[i] = b;
                    int data = enc.Encode(b);
                    for (int k = 0; k < 2; k++)
                    {
                        bits = (bits << 1) | ((data >> k) & 1);
                        if (++count < 4) continue;
                        int sym = ti.Bits(bits);
                        bits = count = 0;
                        if (Rnd.NextDouble() < 0.04) { sym = Rnd.Next(16); hits++; }       // a wrong tone
                        byte[] s = new byte[4];
                        for (int j = 3; j >= 0; j--) { s[j] = (sym & 1) == 1 ? (byte)255 : (byte)0; sym >>= 1; }
                        ri.Symbols(s);
                        foreach (byte x in s)
                        {
                            if (!have) { first = x; have = true; continue; }
                            have = false;
                            int o = v.Decode(first, x);
                            if (o >= 0) got.Add(o);
                        }
                    }
                }
                int start = 0, best = int.MaxValue;
                for (int st = 0; st < 400 && st + 200 < got.Count; st++)
                {
                    int e = 0;
                    for (int i = 0; i < 200; i++) if (got[st + i] != sent[i]) e++;
                    if (e < best) { best = e; start = st; }
                }
                for (int i = 0; i < n && start + i < got.Count; i++) if (got[start + i] != sent[i]) errs++;
                check(errs <= 3, "THOR's Viterbi (K = " + code.K + ", interleaved 4x4x10): " + hits + " symbols of " + (n / 2) + " replaced by a random tone, " + errs + " errors after decoding");
            }
        }

        // the audio for some text (with a little silence either side)
        internal static float[] TxAudio(DominoMode m, string text, double hz, int rate, bool rev)
        {
            IfkTx tx = new IfkTx(m, hz, rev, rate);
            tx.Add(text);
            tx.Finish();
            List<float> a = new List<float>(new float[rate / 2]);
            float[] buf = new float[4096];
            int n;
            while ((n = tx.Generate(buf, buf.Length)) > 0) for (int i = 0; i < n; i++) a.Add(buf[i] * 0.5f);
            a.AddRange(new float[rate + (int)(8 * rate / m.Baud)]);
            return a.ToArray();
        }

        // sends text and receives it: SNR (dB) in 2500 Hz
        private static string loop(DominoMode m, string text, double txHz, double rxHz, double snrDb, int rate, bool txRev, bool rxRev)
        {
            float[] a = TxAudio(m, text, txHz, rate, txRev);
            if (snrDb < 90)
            {
                double ps = 0.5 * 0.5 * 0.5;        // a tone at amplitude 0.5
                double sigma = Math.Sqrt(ps / Math.Pow(10, snrDb / 10) * (rate / 2.0) / 2500);
                for (int i = 0; i < a.Length; i++) a[i] += (float)(Gauss() * sigma);
            }
            Resampler rs = new Resampler(m.Rate);
            IfkRx rx = new IfkRx(m, rxHz) { Squelch = 0, Reverse = rxRev };
            StringBuilder sb = new StringBuilder();
            rx.Text += s => sb.Append(s);
            float[] o = new float[1400];
            for (int p = 0; p < a.Length; p += 4800)
            {
                int len = Math.Min(4800, a.Length - p);
                float[] part = new float[len];
                Array.Copy(a, p, part, 0, len);
                int k = rs.Process(part, len, rate, o);
                rx.Process(o, k);
            }
            return sb.ToString();
        }

        // the widest mode (THOR 25 x4: 600 bins, the K = 15 decoder) keeps up with time to spare
        private static void speed(string modeName)
        {
            DominoMode m = DominoMode.ByName(modeName);
            float[] a = TxAudio(m, Msg, 1500, 48000, false);
            Resampler rs = new Resampler(m.Rate);
            IfkRx rx = new IfkRx(m, 1500) { Squelch = 0 };
            float[] o = new float[a.Length / 4 + 8];
            int k = rs.Process(a, a.Length, 48000, o);
            System.Diagnostics.Stopwatch sw = System.Diagnostics.Stopwatch.StartNew();
            rx.Process(o, k);
            double secs = a.Length / 48000.0, took = sw.Elapsed.TotalSeconds;
            check(took < secs / 4, modeName + ": " + secs.ToString("0.0") + " s of signal decoded in " + took.ToString("0.00") + " s");
        }

        // the signal stays within its 18 tones (phase-continuous, so it doesn't splatter)
        private static void bandwidth()
        {
            DominoMode m = DominoMode.ByName("DominoEX 16");
            IfkTx tx = new IfkTx(m, 1000, false, 8000);
            tx.Add(Msg + " " + Msg);
            float[] a = new float[32768];
            tx.Generate(a, a.Length);
            double[] re = new double[32768], im = new double[32768];
            for (int i = 0; i < a.Length; i++) re[i] = a[i] * (0.5 - 0.5 * Math.Cos(2 * Math.PI * i / (a.Length - 1)));
            Fft.Transform(re, im);
            double inside = 0, all = 0, half = m.Bandwidth / 2 + 2 * m.ToneSpacing;
            for (int b = 0; b < 16384; b++)
            {
                double p = re[b] * re[b] + im[b] * im[b], hz = b * 8000.0 / 32768;
                all += p;
                if (Math.Abs(hz - 1000) <= half) inside += p;
            }
            check(inside / all > 0.99, "DominoEX 16's power within its " + m.Bandwidth.ToString("0") + " Hz (and 2 tones): " + (100 * inside / all).ToString("0.00") + " %");
        }

        // sending through the engine: MOX on, the audio fed, typed text added while sending, MOX off after; the audio
        // sent decodes to what was sent, and what was sent is echoed
        private static void transmit(string modeName)
        {
            string dir = Path.Combine(Path.GetTempPath(), "kainos-domino-selftest");
            FakeHost h = new FakeHost(dir, Path.Combine(dir, "data-tx")) { ModeName = "DIGU", Speed = 8 };
            DominoEngine e = new DominoEngine(h) { TxLock = true, TxFreq = 1234, LevelDb = -6 };
            e.Mode = DominoMode.ByName(modeName);
            e.Start();
            string why = e.StartTx("CQ CQ de K7JUS ");
            Thread.Sleep(1000);
            bool moxOn = h.Mox;
            e.AddText("typed while sending\n");
            e.Finish();
            DateTime until = DateTime.UtcNow.AddSeconds(60);
            while (e.Sending && DateTime.UtcNow < until) Thread.Sleep(50);
            Thread.Sleep(300);
            StringBuilder echo = new StringBuilder();
            foreach (DominoEngine.TextPiece p in e.TakeText()) if (p.Tx) echo.Append(p.Text);
            e.Stop();
            float[] a;
            lock (h.TxAudio) a = h.TxAudio.ToArray();
            Resampler rs = new Resampler(e.Mode.Rate);
            IfkRx rx = new IfkRx(e.Mode, 1234) { Squelch = 0 };
            StringBuilder got = new StringBuilder();
            rx.Text += s => got.Append(s);
            float[] o = new float[a.Length / 4 + 8];
            rx.Process(o, rs.Process(a, a.Length, 48000, o));
            float peak = a.Length == 0 ? 0 : a.Max(v => Math.Abs(v));
            check(why == null && moxOn && !h.Mox, modeName + " TX: MOX on while sending, off after" + (why != null ? " (" + why + ")" : ""));
            check(got.ToString().Contains("CQ CQ de K7JUS typed while sending\n"), modeName + " TX: the audio sent decodes" + show(got.ToString()));
            check(echo.ToString().Contains("CQ CQ de K7JUS typed while sending"), modeName + " TX: what's sent is echoed" + show(echo.ToString()));
            check(Math.Abs(peak - 0.501) < 0.02, modeName + " TX: peak " + peak.ToString("0.000") + " at -6 dB");
        }

        // the window with the module running and a signal playing; a screenshot saved
        private static void ui()
        {
            string dir = Path.Combine(Path.GetTempPath(), "kainos-domino-selftest");
            Form f = new Form { Text = "Kainos DominoEX / THOR self-test", ClientSize = new Size(1100, 420), StartPosition = FormStartPosition.Manual, Location = new Point(40, 40) };
            FakeHost h = new FakeHost(dir, Path.Combine(dir, "data-ui")) { Window = f, ModeName = "DIGU", Speed = 1 };
            h.Settings["open"] = "1";
            h.Settings["rxfreq"] = "1000";
            h.Settings["mode"] = "THOR 16";
            DominoModule mod = new DominoModule();
            f.Shown += (s, e) =>
            {
                mod.Start(h);
                h.Play(TxAudio(DominoMode.ByName("THOR 16"), "CQ CQ de K7JUS K7JUS pse K  The quick brown fox", 1000, 48000, false));
            };
            System.Windows.Forms.Timer t = new System.Windows.Forms.Timer { Interval = 22000 };
            t.Tick += (s, e) =>
            {
                t.Stop();
                Rectangle r = f.RectangleToScreen(f.ClientRectangle);
                using (Bitmap b = new Bitmap(r.Width, r.Height))
                {
                    using (Graphics g = Graphics.FromImage(b)) g.CopyFromScreen(r.Location, Point.Empty, r.Size);
                    string png = Path.Combine(dir, "ui.png");
                    b.Save(png);
                    Console.WriteLine("screenshot: " + png);
                }
                mod.Stop();
                f.Close();
            };
            t.Start();
            Application.Run(f);
            mod.Dispose();
        }
    }
}
