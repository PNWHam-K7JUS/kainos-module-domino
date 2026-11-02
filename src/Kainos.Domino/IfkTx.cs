/*  IfkTx.cs

The DominoEX / THOR transmitter, made a little at a time so text typed while sending goes straight out. Each symbol is
a tone 2 to 17 steps above the last (wrapping round the 18), phase-continuous, as fldigi sends them:

  DominoEX   three idle characters (one for the slowest), then CR STX CR (not Micro), the text (each character's nibbles), idle characters
             while there's nothing to send, CR EOT CR (not Micro) and 4 idle characters to end
  THOR       16 steps of 2 (its preamble), a NUL, CR STX CR, the text (MFSK varicode through the K = 7 or 15
             encoder, each 4 coded bits through the interleaver to a symbol), NULs while idle, CR EOT CR and NULs
             to flush the decoder

DominoEX idles with the secondary alphabet's NUL, which fldigi doesn't print; THOR with NULs.

This file is part of the Kainos DominoEX / THOR module. Copyright (C) 2026 Justin Cron K7JUS. GPLv3 or later.
*/

using System;
using System.Collections.Generic;
using System.Text;

namespace Kainos.Domino
{
    internal sealed class IfkTx
    {
        public readonly DominoMode Mode;
        public readonly double Frequency;           // the signal's centre (Hz, audio)
        public readonly bool Reverse;
        private readonly int _rate;
        private readonly double _sps;               // samples a symbol at the transmit rate
        private double _phase, _step;

        private struct Sym { public int Tone; public bool Mark; public char C; }

        private readonly Queue<char> _text = new Queue<char>();
        private readonly Queue<Sym> _syms = new Queue<Sym>();
        private readonly ConvEncoder _enc;
        private readonly Interleaver _il;
        private int _bits, _bitCount;               // THOR: coded bits waiting for a symbol
        private int _prevTone;
        private bool _started, _ended, _finishing;
        private long _in, _out, _charEnd;
        private bool _mark;
        private char _markChar;
        private double _symPos;
        private bool _first = true;
        private int _fade = -1;                     // samples left fading out at the end

        public bool Done { get; private set; }
        public event Action<char> Sent;             // a character as its symbols start (on the caller's thread)

        public IfkTx(DominoMode mode, double frequency, bool reverse, int rate)
        {
            Mode = mode;
            Frequency = frequency;
            Reverse = reverse;
            _rate = rate;
            _sps = rate * (double)mode.SymbolLen / mode.Rate;
            if (mode.Thor)
            {
                _enc = new ConvEncoder(mode.Code);
                _il = new Interleaver(4, mode.Interleave, false);
            }
        }

        public void Add(string s) { lock (_text) foreach (char c in s) _text.Enqueue(c); }
        public int Queued { get { lock (_text) return _text.Count + (_out < _charEnd ? 1 : 0); } }
        public string ClearQueued() { lock (_text) { string s = new string(_text.ToArray()); _text.Clear(); return s; } }
        public void Finish() { _finishing = true; }
        public bool Finishing { get { return _finishing; } }

        // the next n samples of audio, amplitude up to 1
        public int Generate(float[] dst, int n)
        {
            int o = 0;
            while (o < n && !Done)
            {
                if (_fade >= 0)
                {
                    if (_fade == 0) { Done = true; break; }
                    double a = _fade / (_rate * 0.005);
                    dst[o++] = (float)(Math.Cos(_phase) * a);
                    _phase += _step;
                    _fade--;
                    continue;
                }
                if (_first || _symPos >= _sps)
                {
                    if (!_first) _symPos -= _sps;
                    _first = false;
                    if (!nextSymbol()) { _fade = (int)(_rate * 0.005); continue; }
                }
                // a short fade in at the very start
                double env = _out == 1 ? Math.Min(1, _symPos / (_rate * 0.005)) : 1;
                dst[o++] = (float)(Math.Cos(_phase) * env);
                _phase += _step;
                if (_phase > 2 * Math.PI) _phase -= 2 * Math.PI;
                _symPos += 1;
            }
            return o;
        }

        private bool nextSymbol()
        {
            while (_syms.Count == 0)
            {
                if (!_started) { _started = true; preamble(); }
                else if (next()) { }
                else if (!_finishing) idle();
                else if (!_ended) { _ended = true; postamble(); }
                else return false;
            }
            Sym s = _syms.Dequeue();
            _out++;
            if (s.Mark) Sent?.Invoke(s.C);
            int tone = Reverse ? DominoMode.Tones - 1 - s.Tone : s.Tone;
            double f = Frequency - Mode.Bandwidth / 2 + (tone + 0.5) * Mode.ToneSpacing;
            _step = 2 * Math.PI * f / _rate;
            return true;
        }

        // a symbol (0 - 15): the tone that many plus 2 above the last
        private void symbol(int sym)
        {
            int tone = (_prevTone + 2 + sym) % DominoMode.Tones;
            _prevTone = tone;
            _syms.Enqueue(new Sym { Tone = tone, Mark = _mark, C = _markChar });
            _in++;
            _mark = false;
        }

        private void sendChar(int c, bool secondary = false)
        {
            if (!Mode.Thor)
            {
                List<int> nib = new List<int>();
                DominoVaricode.Nibbles(c, secondary, nib);
                foreach (int x in nib) symbol(x);
                return;
            }
            List<int> bits = new List<int>();
            MfskVaricode.Bits(c, bits);
            foreach (int b in bits)
            {
                int data = _enc.Encode(b);
                for (int i = 0; i < 2; i++)
                {
                    _bits = (_bits << 1) | ((data >> i) & 1);
                    if (++_bitCount == 4)
                    {
                        symbol(_il.Bits(_bits));
                        _bits = _bitCount = 0;
                    }
                }
            }
        }

        private void idleChar() { if (Mode.Thor) sendChar(0); else sendChar(0, true); }

        private void preamble()
        {
            if (Mode.Thor) for (int i = 0; i < 16; i++) symbol(0);
            // DominoEX has no real preamble (fldigi sends one idle character): a few more give the receiver's symbol
            // clock time to settle before the text starts
            int idles = Mode.Thor || Mode.Baud < 5 ? 1 : 3;
            for (int i = 0; i < idles; i++) idleChar();
            sendChar('\r');
            if (!Mode.Micro) { sendChar(2); sendChar('\r'); }
        }

        private void idle() { idleChar(); }

        private void postamble()
        {
            sendChar('\r');
            if (!Mode.Micro) { sendChar(4); sendChar('\r'); }
            for (int i = 0; i < Mode.Flush; i++) idleChar();
        }

        // the next character's symbols (UTF-8 bytes for anything past ASCII): false if there's nothing to send
        private bool next()
        {
            char c;
            lock (_text)
            {
                if (_text.Count == 0) return false;
                c = _text.Dequeue();
            }
            if (c == '\r') return true;
            byte[] bytes = c == '\n' ? new byte[] { 13, 10 } : Encoding.UTF8.GetBytes(c.ToString());
            _mark = true;
            _markChar = c;
            foreach (byte b in bytes) sendChar(b);
            _charEnd = _in;
            if (_mark) { _mark = false; Sent?.Invoke(c); }       // THOR: its bits are waiting for a symbol
            return true;
        }
    }
}
