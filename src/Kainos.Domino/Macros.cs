/*  Macros.cs

The F1 - F8 messages: a label and text, with fields filled in when it's sent: <MYCALL> <MYNAME> <MYGRID> <CALL>
<NAME> <RST> <MODE>, and <RX> (back to receive once everything before it has gone; without it the transmitter stays
on, idling, for typing).

This file is part of the Kainos DominoEX / THOR module. Copyright (C) 2026 Justin Cron K7JUS. GPLv3 or later.
*/

using System;
using Kainos.Modules;

namespace Kainos.Domino
{
    internal sealed class Macro
    {
        public string Label, Text;
    }

    internal sealed class MacroFields
    {
        public string MyCall = "", MyName = "", MyGrid = "", Call = "", Name = "", Rst = "599", Mode = "";
    }

    internal static class Macros
    {
        public const int Count = 8;

        private static readonly string[][] Defaults =
        {
            new[] { "CQ", "\nCQ CQ CQ de <MYCALL> <MYCALL> <MYCALL>\nCQ CQ CQ de <MYCALL> <MYCALL> <MYCALL> pse K\n<RX>" },
            new[] { "Answer", "\n<CALL> <CALL> de <MYCALL> <MYCALL> <MYCALL> pse KN\n<RX>" },
            new[] { "Exchange", "\n<CALL> de <MYCALL>\nThanks for the call. Your RST is <RST> <RST>.\nName here is <MYNAME>, grid <MYGRID>.\nHow copy? <CALL> de <MYCALL> KN\n<RX>" },
            new[] { "BTU", "\n<CALL> de <MYCALL> KN\n<RX>" },
            new[] { "73", "\nThanks for the QSO <NAME>, 73 and good DX.\n<CALL> de <MYCALL> SK\n<RX>" },
            new[] { "QRZ?", "\nQRZ? de <MYCALL> K\n<RX>" },
            new[] { "Call", "<CALL> " },
            new[] { "de Me", "de <MYCALL> " },
        };

        public static Macro Get(IKainosHost host, int i)
        {
            string label = host.GetSetting("macro" + i + ".label", Defaults[i][0]);
            string text = host.GetSetting("macro" + i + ".text", Defaults[i][1].Replace("\n", "\\n"));
            return new Macro { Label = label, Text = text.Replace("\\n", "\n") };
        }

        public static void Set(IKainosHost host, int i, Macro m)
        {
            host.SetSetting("macro" + i + ".label", m.Label);
            host.SetSetting("macro" + i + ".text", m.Text.Replace("\r\n", "\n").Replace("\n", "\\n"));
        }

        public static Macro Default(int i) { return new Macro { Label = Defaults[i][0], Text = Defaults[i][1] }; }

        // the text to send, and whether it ends with going back to receive
        public static string Expand(string text, MacroFields f, out bool rx)
        {
            string s = text.Replace("\r\n", "\n");
            int at = s.IndexOf("<RX>", StringComparison.OrdinalIgnoreCase);
            rx = at >= 0;
            if (rx) s = s.Substring(0, at);
            s = rep(s, "<MYCALL>", f.MyCall); s = rep(s, "<MYNAME>", f.MyName); s = rep(s, "<MYGRID>", f.MyGrid);
            s = rep(s, "<CALL>", f.Call); s = rep(s, "<NAME>", f.Name); s = rep(s, "<RST>", f.Rst); s = rep(s, "<MODE>", f.Mode);
            return s;
        }

        private static string rep(string s, string token, string v)
        {
            int i;
            while ((i = s.IndexOf(token, StringComparison.OrdinalIgnoreCase)) >= 0) s = s.Substring(0, i) + (v ?? "") + s.Substring(i + token.Length);
            return s;
        }
    }
}
