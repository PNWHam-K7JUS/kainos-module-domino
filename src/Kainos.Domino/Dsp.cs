/*  Dsp.cs

The small pieces of signal processing: a complex number, windowed-sinc low-pass filters, the resampler that brings
the receiver's audio down to 8000 (or 11025) samples a second, and an FFT for the waterfall.

This file is part of the Kainos DominoEX / THOR module. Copyright (C) 2026 Justin Cron K7JUS. GPLv3 or later.
*/

using System;

namespace Kainos.Domino
{
    internal struct Cpx
    {
        public double Re, Im;
        public Cpx(double re, double im) { Re = re; Im = im; }

        public static Cpx operator +(Cpx a, Cpx b) { return new Cpx(a.Re + b.Re, a.Im + b.Im); }
        public static Cpx operator -(Cpx a, Cpx b) { return new Cpx(a.Re - b.Re, a.Im - b.Im); }
        public static Cpx operator *(Cpx a, Cpx b) { return new Cpx(a.Re * b.Re - a.Im * b.Im, a.Re * b.Im + a.Im * b.Re); }
        public static Cpx operator *(Cpx a, double k) { return new Cpx(a.Re * k, a.Im * k); }
        public Cpx Conj { get { return new Cpx(Re, -Im); } }
        public double Mag { get { return Math.Sqrt(Re * Re + Im * Im); } }
        public double Norm { get { return Re * Re + Im * Im; } }
        public double Arg { get { return Math.Atan2(Im, Re); } }
        public static Cpx Polar(double a) { return new Cpx(Math.Cos(a), Math.Sin(a)); }
    }

    internal static class Fir
    {
        // a low-pass filter: cutoff in cycles a sample (0 - 0.5), Blackman window, unity gain at DC
        public static double[] LowPass(int taps, double cutoff)
        {
            if (taps % 2 == 0) taps++;
            double[] h = new double[taps];
            int m = taps - 1;
            double sum = 0;
            for (int i = 0; i < taps; i++)
            {
                double x = i - m / 2.0;
                double s = x == 0 ? 2 * cutoff : Math.Sin(2 * Math.PI * cutoff * x) / (Math.PI * x);
                double w = 0.42 - 0.5 * Math.Cos(2 * Math.PI * i / m) + 0.08 * Math.Cos(4 * Math.PI * i / m);
                h[i] = s * w;
                sum += h[i];
            }
            for (int i = 0; i < taps; i++) h[i] /= sum;
            return h;
        }
    }

    // A complex FIR filter that's only worked out when an output is wanted (so decimating costs nothing extra)
    internal sealed class CpxFir
    {
        private readonly double[] _h;
        private readonly Cpx[] _ring;
        private int _pos;

        public CpxFir(double[] h) { _h = h; _ring = new Cpx[h.Length]; }

        public void Push(Cpx v) { _ring[_pos] = v; if (++_pos == _ring.Length) _pos = 0; }

        public Cpx Output()
        {
            double re = 0, im = 0;
            int n = _h.Length, p = _pos;
            for (int i = 0; i < n; i++)
            {
                Cpx v = _ring[p];
                re += v.Re * _h[i]; im += v.Im * _h[i];
                if (++p == n) p = 0;
            }
            return new Cpx(re, im);
        }
    }

    // The receiver's audio (48000 a second usually) brought to 8000: a low-pass below 3.6 kHz, worked out only at the
    // output instants, with linear interpolation between two for a rate that isn't a whole multiple of 8000
    internal sealed class Resampler
    {
        public const int OutRate = 8000;           // the waterfall's (and the 8 kHz modes')
        private readonly int _out;
        private int _inRate;

        public Resampler(int outRate = OutRate) { _out = outRate; }
        private double[] _h;
        private float[] _ring;
        private int _pos;
        private long _count;
        private double _next;      // the input sample (count) the next output is due at

        public void Reset(int inRate)
        {
            _inRate = inRate;
            int taps = Math.Max(31, (int)(3.6 * inRate / 600.0)) | 1;       // a 600 Hz transition
            _h = Fir.LowPass(taps, Math.Min(0.4375 * _out, 0.45 * inRate) / inRate);
            _ring = new float[taps + 1];        // one more: the filter's output one sample back can be worked out too
            _pos = 0; _count = 0; _next = taps;
        }

        // returns how many 8 kHz samples were written to dst (it holds n / (inRate / 8000) + 2)
        public int Process(float[] src, int n, int inRate, float[] dst)
        {
            if (inRate != _inRate || _h == null) Reset(inRate);
            if (inRate == _out) { Array.Copy(src, dst, n); return n; }
            double step = inRate / (double)_out;
            int o = 0, len = _ring.Length;
            for (int i = 0; i < n; i++)
            {
                _ring[_pos] = src[i];
                if (++_pos == len) _pos = 0;
                _count++;
                // the newest sample is number count-1: an output falls between it and the one before
                while (_count - 1 >= Math.Floor(_next) + 1 && o < dst.Length)
                {
                    double frac = _next - Math.Floor(_next);
                    double before = dot(_pos);                          // ending one sample back (len = taps + 1)
                    double y = frac < 1e-9 ? before : before * (1 - frac) + dot((_pos + 1) % len) * frac;
                    dst[o++] = (float)y;
                    _next += step;
                }
            }
            return o;
        }

        // the filter over taps samples, the oldest at ring position p
        private double dot(int p)
        {
            double s = 0;
            int len = _ring.Length, taps = _h.Length;
            for (int i = 0; i < taps; i++)
            {
                s += _ring[p] * _h[i];
                if (++p == len) p = 0;
            }
            return s;
        }
    }

    internal static class Fft
    {
        // in place, n a power of 2
        public static void Transform(double[] re, double[] im)
        {
            int n = re.Length;
            for (int i = 1, j = 0; i < n; i++)
            {
                int bit = n >> 1;
                for (; (j & bit) != 0; bit >>= 1) j ^= bit;
                j ^= bit;
                if (i < j) { double t = re[i]; re[i] = re[j]; re[j] = t; t = im[i]; im[i] = im[j]; im[j] = t; }
            }
            for (int len = 2; len <= n; len <<= 1)
            {
                double ang = -2 * Math.PI / len, wr = Math.Cos(ang), wi = Math.Sin(ang);
                for (int i = 0; i < n; i += len)
                {
                    double cr = 1, ci = 0;
                    for (int k = 0; k < len / 2; k++)
                    {
                        int a = i + k, b = a + len / 2;
                        double xr = re[b] * cr - im[b] * ci, xi = re[b] * ci + im[b] * cr;
                        re[b] = re[a] - xr; im[b] = im[a] - xi;
                        re[a] += xr; im[a] += xi;
                        double t = cr * wr - ci * wi; ci = cr * wi + ci * wr; cr = t;
                    }
                }
            }
        }
    }
}
