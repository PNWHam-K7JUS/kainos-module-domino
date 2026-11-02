/*  IfkRx.cs

One DominoEX / THOR receiver, as fldigi's: a bank of sliding DFTs a symbol long, five to a tone spacing (so a tone
between bins is still caught), across the 18 tones and a few either side for mistuning. At each symbol's end the
strongest bin is the tone; the difference from the last tone, in tones, less 2, is the symbol (0 - 15). The symbol
clock follows where the last tone's bin peaked (it peaks when the DFT exactly covers that tone).

DominoEX: the symbols are nibbles of its varicode. THOR: each is 4 coded bits, through the de-interleaver and the
Viterbi decoder, read as MFSK varicode; its preamble (tones stepping by 2) clears the decoder.

The squelch works on fldigi's metric: the tone's strength against the same bin a symbol earlier (when it was off).

This file is part of the Kainos DominoEX / THOR module. Copyright (C) 2026 Justin Cron K7JUS. GPLv3 or later.
*/

using System;

namespace Kainos.Domino
{
    internal sealed class IfkRx
    {
        public readonly DominoMode Mode;
        public readonly double Frequency;   // the signal's centre (Hz, audio)
        public bool Reverse;                // the tones the other way up (the other sideband)
        public int Squelch = 25;            // the metric (0 - 100) text shows from; 0: always
        public double Metric;               // 0 - 100
        public double SnrDb;
        public bool Dcd { get { return Squelch <= 0 || Metric >= Squelch; } }
        public event Action<string> Text;   // decoded text (on the caller's thread)

        private const int Paths = 5;        // bins to a tone spacing's worth of DFT resolution
        private const int ExtTones = 6;     // tones looked at beyond each end, for mistuning
        private readonly int _n, _bins, _ds;
        private readonly double[] _osRe, _osIm, _rotRe, _rotIm, _cRe, _cIm, _sRe, _sIm;
        private readonly bool[] _off;       // bins outside the audio (below 0 Hz or above half the rate): never the tone
        private readonly float[] _x;        // the last symbol's samples
        private int _xPos;
        private readonly float[] _pipe;     // two symbols of every bin's magnitude
        private int _pipePos;
        private int _renorm;

        private int _syncCounter;
        private readonly double[] _synAvg = new double[16];
        private int _synIdx, _synCount, _synUpdates, _good;
        private int _cur, _prev1, _prev2;
        private bool _staticBurst;
        private double _sig = 6, _noise = 6;

        private readonly DominoReader _domino;
        private readonly Viterbi _vit;
        private readonly Interleaver _il;
        private readonly MfskVaricodeReader _thor;
        private int _pairCount;
        private byte _pairFirst;
        private int _twoCount, _preambleCheck;
        private bool _neg16;

        public IfkRx(DominoMode mode, double frequency)
        {
            Mode = mode;
            Frequency = frequency;
            _n = mode.SymbolLen;
            _ds = mode.Spacing;
            _bins = (DominoMode.Tones + 2 * ExtTones) * _ds * Paths;
            _osRe = new double[_bins]; _osIm = new double[_bins];
            _rotRe = new double[_bins]; _rotIm = new double[_bins];
            _cRe = new double[_bins]; _cIm = new double[_bins];
            _sRe = new double[_bins]; _sIm = new double[_bins];
            _off = new bool[_bins];
            double res = mode.Rate / (double)_n;                    // a DFT bin
            double lowest = frequency - mode.Bandwidth / 2 + 0.5 * mode.ToneSpacing - ExtTones * mode.ToneSpacing;
            for (int k = 0; k < _bins; k++)
            {
                double f = lowest + k * res / Paths;
                _off[k] = f < 40 || f > mode.Rate / 2.0 - 40;
                double w = 2 * Math.PI * f / mode.Rate;
                _osRe[k] = 1; _osIm[k] = 0;
                _rotRe[k] = Math.Cos(w); _rotIm[k] = -Math.Sin(w);
                _cRe[k] = Math.Cos(w * _n); _cIm[k] = Math.Sin(w * _n);
            }
            _x = new float[_n];
            _pipe = new float[2 * _n * _bins];
            _syncCounter = _n;
            if (mode.Thor)
            {
                _vit = new Viterbi(mode.Code, mode.Code.K == 15 ? 15 * 12 : 45);
                _il = new Interleaver(4, mode.Interleave, true);
                _thor = new MfskVaricodeReader();
            }
            else _domino = new DominoReader();
        }

        // samples at the mode's rate
        public void Process(float[] a, int count)
        {
            for (int i = 0; i < count; i++)
            {
                double x = a[i], old = _x[_xPos];
                _x[_xPos] = a[i];
                if (++_xPos == _n) _xPos = 0;
                int row = _pipePos * _bins;
                for (int k = 0; k < _bins; k++)
                {
                    // the oscillator turned on a sample: e^(-j w n)
                    double oRe = _osRe[k] * _rotRe[k] - _osIm[k] * _rotIm[k];
                    double oIm = _osRe[k] * _rotIm[k] + _osIm[k] * _rotRe[k];
                    _osRe[k] = oRe; _osIm[k] = oIm;
                    // add the new sample, take off the one a symbol back: x(n-N) e^(-j w (n-N)) = x(n-N) osc c
                    double sRe = oRe * _cRe[k] - oIm * _cIm[k], sIm = oRe * _cIm[k] + oIm * _cRe[k];
                    _sRe[k] += x * oRe - old * sRe;
                    _sIm[k] += x * oIm - old * sIm;
                    _pipe[row + k] = (float)Math.Sqrt(_sRe[k] * _sRe[k] + _sIm[k] * _sIm[k]);
                }
                if (++_renorm == 4096)
                {
                    _renorm = 0;
                    for (int k = 0; k < _bins; k++)
                    {
                        double m = Math.Sqrt(_osRe[k] * _osRe[k] + _osIm[k] * _osIm[k]);
                        _osRe[k] /= m; _osIm[k] /= m;
                    }
                }
                if (--_syncCounter <= 0)
                {
                    _syncCounter = _n;
                    _cur = hardDecode();
                    decodeSymbol();
                    synchronize();
                    evalSnr();
                    _prev2 = _prev1;
                    _prev1 = _cur;
                }
                if (++_pipePos == 2 * _n) _pipePos = 0;
            }
        }

        private float mag(int age, int bin)
        {
            int p = _pipePos - age;
            if (p < 0) p += 2 * _n;
            return _pipe[p * _bins + bin];
        }

        private int hardDecode()
        {
            double max = 0, avg = 0;
            int sym = 0, row = _pipePos * _bins;
            int used = 0;
            for (int k = 0; k < _bins; k++)
            {
                if (_off[k]) continue;
                double v = _pipe[row + k];
                avg += v;
                used++;
                if (v > max) { max = v; sym = k; }
            }
            avg /= Math.Max(1, used);
            _staticBurst = avg <= 0 || max / avg < 1.2;
            return sym;
        }

        private void decodeSymbol()
        {
            double fdiff = (_cur - _prev1) / (double)(Paths * _ds);
            if (Reverse) fdiff = -fdiff;
            int c = (int)Math.Floor(fdiff + 0.5);
            if (Mode.Thor)
            {
                if (preamble(c)) { flush(); return; }
                bool outOfRange = Math.Abs(fdiff) > 17;
                c -= 2;
                if (c < 0) c += DominoMode.Tones;
                byte[] s = new byte[4];
                for (int i = 3; i >= 0; i--)
                {
                    s[i] = _staticBurst || outOfRange ? (byte)128 : (c & 1) == 1 ? (byte)255 : (byte)0;
                    c >>= 1;
                }
                _il.Symbols(s);
                for (int i = 0; i < 4; i++) pair(s[i]);
            }
            else
            {
                c -= 2;
                if (c < 0) c += DominoMode.Tones;
                if (_staticBurst) return;
                string t = _domino.Nibble(c);
                if (t != null && Dcd) Text?.Invoke(t);
            }
        }

        // THOR: soft bits paired for the decoder (they come four to a symbol, so the pairs always line up)
        private void pair(byte s)
        {
            if (_pairCount == 0) { _pairFirst = s; _pairCount = 1; return; }
            _pairCount = 0;
            int bit = _vit.Decode(_pairFirst, s);
            if (bit < 0) return;
            string t = _thor.Bit(bit);
            if (t != null && Dcd) Text?.Invoke(t);
        }

        // THOR's preamble: steps of 2 (and -16 where they wrap round), as fldigi spots it
        private bool preamble(int c)
        {
            if (_twoCount > 14) _twoCount = 0;
            if (c == -16 && _twoCount > 2) _neg16 = true;
            else if (c != 2) _neg16 = false;
            else _twoCount++;
            if (c != -16 && c != 2 && _twoCount > 1) _twoCount -= 2;
            if (_twoCount > 4 && _neg16) return ++_preambleCheck > 4;
            _preambleCheck = 0;
            return false;
        }

        private void flush()
        {
            _il.Flush(128);
            _vit.Reset();
            _thor.Reset();
            _pairCount = 0;
        }

        // the symbol clock: where, over the last two symbols, the last tone's bin peaked; it should be a symbol ago
        private void synchronize()
        {
            _good = _staticBurst ? 0 : _good + 1;
            if (_staticBurst || _cur == _prev1 || _prev1 == _prev2) return;
            if (_synUpdates < 8 && _good < 3) return;   // the two symbols looked back over are both signal
            double max = 0;
            int syn = -1;
            for (int i = 0; i < 2 * _n; i++)
            {
                // i = 0 the newest sample; fldigi counts from the oldest, the newest first: i = 2N - age
                int age = i == 0 ? 0 : 2 * _n - i;
                double v = mag(age, _prev1);
                if (v > max) { max = v; syn = i; }
            }
            // fast at first (each error halved), so the first characters aren't lost; then gently, as fldigi does
            // (the last 16 averaged, an 18th of the error taken off)
            if (_synUpdates < 8)
            {
                _synUpdates++;
                _syncCounter += (int)Math.Floor((syn - _n) * 0.5 + 0.5);
                return;
            }
            _synAvg[_synIdx] = syn;
            _synIdx = (_synIdx + 1) % _synAvg.Length;
            if (_synCount < _synAvg.Length) _synCount++;
            double avg = 0;
            for (int i = 0; i < _synCount; i++) avg += _synAvg[i];
            avg /= _synCount;
            _syncCounter += (int)Math.Floor((avg - _n) / DominoMode.Tones + 0.5);
        }

        private void evalSnr()
        {
            double s = mag(0, _cur);
            double n = (DominoMode.Tones - 1) * mag(_n, _cur);
            _sig += (s - _sig) / (Math.Abs(s - _sig) > 4 ? 4 : 32);
            _noise += (n - _noise) / 64;
            SnrDb = _noise > 0 && _sig > 0 ? 20 * Math.Log10(_sig / _noise) - 6 : 0;
            Metric = Math.Max(0, Math.Min(100, 6 * (SnrDb + 18.4)));
        }
    }
}
