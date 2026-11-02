/*  DominoLog.cs

DominoEX and THOR QSOs: an ADIF log (DataFolder\kainos-domino.adi, MODE DOMINO (submode DOMINOEX) or THOR (submode
THOR16...)), "worked before"
from it, and the logged QSO told to logging programs as WSJT-X's "Logged ADIF" UDP message (type 12, to
127.0.0.1:2237 by default), which Log4OM, N1MM+, HRD, DXKeeper, GridTracker and the Kainos Logbook take.

This file is part of the Kainos DominoEX / THOR module. Copyright (C) 2026 Justin Cron K7JUS. GPLv3 or later.
*/

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;

namespace Kainos.Domino
{
    internal class DominoQso
    {
        public string Call = "", Name = "", Grid = "", Qth = "", RstSent = "599", RstRcvd = "", Power = "", Comment = "";
        public string Mode = "DOMINO", Submode = "DOMINOEX";
        public DateTime On = DateTime.UtcNow, Off = DateTime.UtcNow;
        public double DialHz;
    }

    internal static class DominoLog
    {
        public static string FileOf(string dataFolder) { return Path.Combine(dataFolder, "kainos-domino.adi"); }

        // appends the QSO; returns its ADIF record
        public static string Append(string path, DominoQso q, string myCall, string myGrid)
        {
            string rec = Record(q, myCall, myGrid);
            bool fresh = !File.Exists(path);
            File.AppendAllText(path, (fresh ? "Kainos DominoEX / THOR log\r\n<ADIF_VER:5>3.1.4 <PROGRAMID:10>Kainos DominoEX / THOR <EOH>\r\n" : "") + rec + "\r\n", Encoding.UTF8);
            return rec;
        }

        public static string Record(DominoQso q, string myCall, string myGrid)
        {
            CultureInfo ic = CultureInfo.InvariantCulture;
            StringBuilder sb = new StringBuilder();
            f(sb, "CALL", q.Call.Trim().ToUpperInvariant());
            f(sb, "MODE", q.Mode);
            f(sb, "SUBMODE", q.Submode);
            f(sb, "RST_SENT", q.RstSent);
            f(sb, "RST_RCVD", q.RstRcvd);
            f(sb, "QSO_DATE", q.On.ToString("yyyyMMdd", ic));
            f(sb, "TIME_ON", q.On.ToString("HHmmss", ic));
            f(sb, "QSO_DATE_OFF", q.Off.ToString("yyyyMMdd", ic));
            f(sb, "TIME_OFF", q.Off.ToString("HHmmss", ic));
            if (q.DialHz > 0)
            {
                f(sb, "FREQ", (q.DialHz / 1e6).ToString("0.000000", ic));
                f(sb, "BAND", Band(q.DialHz));
            }
            f(sb, "NAME", q.Name);
            f(sb, "GRIDSQUARE", q.Grid);
            f(sb, "QTH", q.Qth);
            f(sb, "TX_PWR", q.Power);
            f(sb, "COMMENT", q.Comment);
            f(sb, "STATION_CALLSIGN", myCall);
            f(sb, "MY_GRIDSQUARE", myGrid);
            sb.Append("<EOR>");
            return sb.ToString();
        }

        private static void f(StringBuilder sb, string name, string v)
        {
            if (string.IsNullOrEmpty(v)) return;
            // ADIF counts characters (UTF-8 in ADIF 3's newer fields; plain ASCII here in practice)
            sb.Append('<').Append(name).Append(':').Append(v.Length).Append('>').Append(v).Append(' ');
        }

        private static readonly double[][] Bands =
        {
            new[] { 1.8e6, 2.0e6 }, new[] { 3.5e6, 4.0e6 }, new[] { 5.25e6, 5.45e6 }, new[] { 7.0e6, 7.3e6 }, new[] { 10.1e6, 10.15e6 },
            new[] { 14.0e6, 14.35e6 }, new[] { 18.068e6, 18.168e6 }, new[] { 21.0e6, 21.45e6 }, new[] { 24.89e6, 24.99e6 },
            new[] { 28.0e6, 29.7e6 }, new[] { 50e6, 54e6 }, new[] { 144e6, 148e6 }, new[] { 420e6, 450e6 },
        };
        private static readonly string[] BandNames = { "160M", "80M", "60M", "40M", "30M", "20M", "17M", "15M", "12M", "10M", "6M", "2M", "70CM" };

        public static string Band(double hz)
        {
            for (int i = 0; i < Bands.Length; i++) if (hz >= Bands[i][0] && hz <= Bands[i][1]) return BandNames[i];
            return "";
        }

        // worked before: each callsign's QSOs in the log (count, and the last date)
        public static Dictionary<string, KeyValuePair<int, string>> Worked(string path)
        {
            Dictionary<string, KeyValuePair<int, string>> d = new Dictionary<string, KeyValuePair<int, string>>(StringComparer.OrdinalIgnoreCase);
            if (!File.Exists(path)) return d;
            string all;
            try { all = File.ReadAllText(path); } catch { return d; }
            int eoh = all.IndexOf("<EOH>", StringComparison.OrdinalIgnoreCase);
            foreach (string rec in all.Substring(eoh < 0 ? 0 : eoh + 5).Split(new[] { "<EOR>", "<eor>" }, StringSplitOptions.RemoveEmptyEntries))
            {
                string call = field(rec, "CALL"), date = field(rec, "QSO_DATE");
                if (call.Length == 0) continue;
                KeyValuePair<int, string> was;
                d.TryGetValue(call, out was);
                d[call] = new KeyValuePair<int, string>(was.Key + 1, string.CompareOrdinal(date, was.Value ?? "") > 0 ? date : was.Value);
            }
            return d;
        }

        internal static string field(string rec, string name)
        {
            Match m = Regex.Match(rec, "<" + name + @":(\d+)(?::[^>]*)?>", RegexOptions.IgnoreCase);
            if (!m.Success) return "";
            int len = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture), at = m.Index + m.Length;
            return at + len <= rec.Length ? rec.Substring(at, len) : "";
        }

        // ---- WSJT-X's UDP "Logged ADIF" message, for logging programs ----

        public static bool ValidAddress(string address)
        {
            string[] hp = (address ?? "").Trim().Split(':');
            int port;
            return hp.Length == 2 && hp[0].Length > 0 && int.TryParse(hp[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out port) && port > 0 && port < 65536;
        }

        // address: "host:port"; returns null, or why it wasn't sent
        public static string TellLoggers(string address, string adifRecord)
        {
            try
            {
                string[] hp = (address ?? "").Trim().Split(':');
                int port;
                if (hp.Length != 2 || !int.TryParse(hp[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out port)) return "the address should be host:port, e.g. 127.0.0.1:2237";
                MemoryStream m = new MemoryStream();
                Action<uint> u32 = v => { for (int i = 3; i >= 0; i--) m.WriteByte((byte)(v >> (8 * i))); };
                Action<string> utf8 = s => { byte[] b = Encoding.UTF8.GetBytes(s); u32((uint)b.Length); m.Write(b, 0, b.Length); };
                u32(0xadbccbda);            // magic
                u32(3);                     // schema
                u32(12);                    // Logged ADIF
                utf8("Kainos DominoEX / THOR");        // the program's id
                utf8("\n<adif_ver:5>3.1.0\n<programid:10>Kainos DominoEX / THOR\n<EOH>\n" + adifRecord);
                byte[] pkt = m.ToArray();
                using (UdpClient c = new UdpClient()) c.Send(pkt, pkt.Length, hp[0], port);
                return null;
            }
            catch (Exception ex) { return ex.Message; }
        }
    }
}
