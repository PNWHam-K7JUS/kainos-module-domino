/*  Convolution.cs

THOR's forward error correction: a rate 1/2 convolutional code (the first polynomial giving the first bit sent, as in
fldigi), a Viterbi decoder for it taking soft bits, and fldigi's interleaver.

  THOR 4 - 22, Micro      K = 7,  0x6d / 0x4f
  THOR 25 x4 - 100        K = 15, 044735 / 063057

This file is part of the Kainos DominoEX / THOR module. Copyright (C) 2026 Justin Cron K7JUS. GPLv3 or later.
*/

using System;

namespace Kainos.Domino
{
    internal sealed class ConvCode
    {
        public readonly int K, Poly1, Poly2;
        public readonly int[] Output;       // the 2-bit symbol for each K-bit window (the newest bit in bit 0)

        public ConvCode(int k, int poly1, int poly2)
        {
            K = k; Poly1 = poly1; Poly2 = poly2;
            Output = new int[1 << k];
            for (int w = 0; w < Output.Length; w++) Output[w] = parity(w & poly1) | (parity(w & poly2) << 1);
        }

        public int States { get { return 1 << (K - 1); } }

        private static int parity(int v) { int p = 0; while (v != 0) { p ^= v & 1; v >>= 1; } return p; }

        public static readonly ConvCode PskR = new ConvCode(7, 0x6d, 0x4f);        // THOR's (fldigi's "NASA" K = 7)
        public static readonly ConvCode K15 = new ConvCode(15, Convert.ToInt32("44735", 8), Convert.ToInt32("63057", 8));
    }

    internal sealed class ConvEncoder
    {
        private readonly ConvCode _c;
        private int _sh;
        public ConvEncoder(ConvCode c) { _c = c; }
        public void Reset() { _sh = 0; }
        public int Encode(int bit) { _sh = ((_sh << 1) | (bit & 1)) & ((1 << _c.K) - 1); return _c.Output[_sh]; }
    }

    // Soft bits are 0 - 255: 0 sure it's a 0, 255 sure it's a 1, 128 don't know. A decided bit comes out Depth
    // symbols after its own. Gain is how well the newest symbol fitted the best path (up to 254): averaged, it says
    // which of two decoders is lined up with the bit pairs.
    internal sealed class Viterbi
    {
        public readonly int Depth;
        public int Gain { get; private set; }
        private readonly ConvCode _c;
        private readonly int _states, _hist, _words;
        private int[] _metric, _next;
        private readonly ulong[] _from;             // for each step and state: the bit that fell off the far end (packed)
        private int _step;

        public Viterbi(ConvCode c, int depth)
        {
            _c = c;
            Depth = depth;
            _states = c.States;
            _hist = depth + 2;
            _words = (_states + 63) / 64;
            _metric = new int[_states];
            _next = new int[_states];
            _from = new ulong[_hist * _words];
            Reset();
        }

        // known: starting in state 0 (the encoder just reset); else any state
        public void Reset(bool known = false)
        {
            for (int s = 0; s < _states; s++) _metric[s] = known && s != 0 ? -100000 : 0;
            _step = 0;
        }

        // a symbol's two soft bits (s0 for poly 1, s1 for poly 2); returns the decided bit, or -1 while filling up
        public int Decode(int s0, int s1)
        {
            // the branch metric for each 2-bit output
            int m0 = (128 - s0) + (128 - s1), m1 = (s0 - 128) + (128 - s1), m2 = (128 - s0) + (s1 - 128), m3 = (s0 - 128) + (s1 - 128);
            int[] bm = { m0, m1, m2, m3 };
            int[] o = _c.Output;
            int half = _states >> 1, top = _states;      // window = (previous state << 1) | bit
            int best = int.MinValue, bestState = 0, h = (_step % _hist) * _words;
            Array.Clear(_from, h, _words);
            for (int ns = 0; ns < _states; ns++)
            {
                // the two states that lead to ns: ns >> 1 with the old top bit 0 or 1
                int pa = ns >> 1, pb = pa | half;
                int va = _metric[pa] + bm[o[ns]];
                int vb = _metric[pb] + bm[o[ns | top]];
                int v;
                if (vb > va) { v = vb; _from[h + (ns >> 6)] |= 1UL << (ns & 63); }
                else v = va;
                _next[ns] = v;
                if (v > best) { best = v; bestState = ns; }
            }
            int[] t = _metric; _metric = _next; _next = t;
            for (int s = 0; s < _states; s++) _metric[s] -= best;        // kept small
            Gain = best;
            _step++;
            if (_step <= Depth) return -1;
            // back from the best state now to Depth steps ago: that step's input bit is the state's bit 0
            int st = bestState, kk = _c.K - 2;
            for (int k = 0; k < Depth; k++)
            {
                int hh = ((_step - 1 - k) % _hist) * _words;
                int fb = (int)((_from[hh + (st >> 6)] >> (st & 63)) & 1);
                st = (st >> 1) | (fb << kk);
            }
            return st & 1;
        }
    }

    // fldigi's interleaver: depth square (size x size) blocks in a row, each passing a diagonal through. Forward on
    // transmit, reverse (starting full of "don't know") on receive.
    internal sealed class Interleaver
    {
        private readonly int _size, _depth;
        private readonly bool _reverse;
        private readonly byte[] _t;

        public Interleaver(int size, int depth, bool reverse)
        {
            _size = size; _depth = depth; _reverse = reverse;
            _t = new byte[size * size * depth];
            Flush();
        }

        public void Flush() { Flush(_reverse ? (byte)128 : (byte)0); }
        public void Flush(byte v) { for (int i = 0; i < _t.Length; i++) _t[i] = v; }

        private int at(int k, int i, int j) { return _size * _size * k + _size * i + j; }

        public void Symbols(byte[] s)
        {
            for (int k = 0; k < _depth; k++)
            {
                for (int i = 0; i < _size; i++)
                    for (int j = 0; j < _size - 1; j++) _t[at(k, i, j)] = _t[at(k, i, j + 1)];
                for (int i = 0; i < _size; i++) _t[at(k, i, _size - 1)] = s[i];
                for (int i = 0; i < _size; i++) s[i] = _reverse ? _t[at(k, i, i)] : _t[at(k, i, _size - i - 1)];
            }
        }

        // size bits (the first to send highest), interleaved
        public int Bits(int bits)
        {
            byte[] s = new byte[_size];
            for (int i = 0; i < _size; i++) s[i] = (byte)((bits >> (_size - i - 1)) & 1);
            Symbols(s);
            int r = 0;
            for (int i = 0; i < _size; i++) r = (r << 1) | s[i];
            return r;
        }
    }
}
