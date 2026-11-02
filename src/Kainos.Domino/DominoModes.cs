/*  DominoModes.cs

The modes, as fldigi defines them. Both families are IFK+ (incremental frequency keying): 18 tones, and each symbol
is a step up from the last tone of 2 to 17 tones (wrapping round), so the data is in the difference between one tone
and the next. That makes them forgiving of tuning and drift, and the "2 at least" means a tone never repeats.

  DominoEX   4 bits a symbol, DominoEX's own varicode (one to three nibbles a character), no FEC
  THOR       4 coded bits a symbol: MFSK varicode through a rate 1/2 code (K = 7, or K = 15 for the fast modes)
             and an interleaver, so it rides through static crashes and fades

A symbol is SymbolLen samples at Rate (8000 or 11025 a second); the tones are Rate * Spacing / SymbolLen apart
(Spacing 1, 2 or 4: "double spaced" modes keep their tones further apart than their baud).

This file is part of the Kainos DominoEX / THOR module. Copyright (C) 2026 Justin Cron K7JUS. GPLv3 or later.
*/

namespace Kainos.Domino
{
    internal sealed class DominoMode
    {
        public const int Tones = 18;

        public readonly string Name;        // as fldigi names it: "DominoEX 16", "THOR 22"...
        public readonly string Short;       // for the button and the log's submode: "DOMINOEX16", "THOR22"
        public readonly bool Thor;
        public readonly int Rate;           // samples a second the mode is defined at
        public readonly int SymbolLen;      // samples a symbol at Rate
        public readonly int Spacing;        // tones this many DFT bins apart
        public readonly ConvCode Code;      // THOR's FEC
        public readonly int Interleave;     // THOR's interleaver depth (4 x 4 blocks)
        public readonly int Flush;          // NULs sent at the end to flush the receiver (THOR: through the interleaver and decoder)
        public readonly bool Micro;         // the slowest: no STX / EOT framing

        private DominoMode(string name, string shortName, bool thor, int rate, int symbolLen, int spacing, int interleave = 10, int flush = 4, bool k15 = false)
        {
            Name = name; Short = shortName; Thor = thor; Rate = rate; SymbolLen = symbolLen; Spacing = spacing;
            Interleave = interleave;
            // fldigi sends 4 (40 for the fast THORs), fewer than THOR's interleaver and decoder hold, so the last
            // characters only come out on the noise after; enough here to bring them out on their own
            Flush = thor ? (k15 ? 64 : interleave * 3 * 4 / 11 + 8) : flush;
            Micro = symbolLen == 4000;
            if (thor) Code = k15 ? ConvCode.K15 : ConvCode.PskR;
        }

        public double Baud { get { return Rate / (double)SymbolLen; } }
        public double ToneSpacing { get { return Rate * (double)Spacing / SymbolLen; } }
        public double Bandwidth { get { return Tones * ToneSpacing; } }

        public static readonly DominoMode[] All =
        {
            new DominoMode("DominoEX Micro", "DOMINOEXMICRO", false, 8000, 4000, 1),
            new DominoMode("DominoEX 4", "DOMINOEX4", false, 8000, 2048, 2),
            new DominoMode("DominoEX 5", "DOMINOEX5", false, 11025, 2048, 2),
            new DominoMode("DominoEX 8", "DOMINOEX8", false, 8000, 1024, 2),
            new DominoMode("DominoEX 11", "DOMINOEX11", false, 11025, 1024, 1),
            new DominoMode("DominoEX 16", "DOMINOEX16", false, 8000, 512, 1),
            new DominoMode("DominoEX 22", "DOMINOEX22", false, 11025, 512, 1),
            new DominoMode("DominoEX 44", "DOMINOEX44", false, 11025, 256, 2),
            new DominoMode("DominoEX 88", "DOMINOEX88", false, 11025, 128, 1),
            new DominoMode("THOR Micro", "THORM", true, 8000, 4000, 1, 4),
            new DominoMode("THOR 4", "THOR4", true, 8000, 2048, 2),
            new DominoMode("THOR 5", "THOR5", true, 11025, 2048, 2),
            new DominoMode("THOR 8", "THOR8", true, 8000, 1024, 2),
            new DominoMode("THOR 11", "THOR11", true, 11025, 1024, 1),
            new DominoMode("THOR 16", "THOR16", true, 8000, 512, 1),
            new DominoMode("THOR 22", "THOR22", true, 11025, 512, 1),
            new DominoMode("THOR 25 x4", "THOR-25X4", true, 8000, 320, 4, 50, 40, true),
            new DominoMode("THOR 50 x1", "THOR-50X1", true, 8000, 160, 1, 50, 40, true),
            new DominoMode("THOR 50 x2", "THOR-50X2", true, 8000, 160, 2, 50, 40, true),
            new DominoMode("THOR 100", "THOR-100", true, 8000, 80, 1, 50, 40, true),
        };

        public static DominoMode ByName(string name)
        {
            foreach (DominoMode m in All) if (string.Equals(m.Name, name, System.StringComparison.OrdinalIgnoreCase) || string.Equals(m.Short, name, System.StringComparison.OrdinalIgnoreCase)) return m;
            return ByName("DominoEX 16");
        }

        // ADIF: MODE DOMINO (submode DOMINOEX) or THOR (submode THOR4, THOR16...)
        public string AdifMode { get { return Thor ? "THOR" : "DOMINO"; } }
        public string AdifSubmode { get { return Thor ? (Micro ? "THOR-M" : Short) : "DOMINOEX"; } }

        public override string ToString() { return Name; }
    }
}
