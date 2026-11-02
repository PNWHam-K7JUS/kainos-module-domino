/*  LogQsoForm.cs

Log QSO: the QSO's details filled in (their callsign, name, the reports, start and end, frequency, mode, power)
to check or add to before it's logged.

This file is part of the Kainos DominoEX / THOR module. Copyright (C) 2026 Justin Cron K7JUS. GPLv3 or later.
*/

using System;
using System.Drawing;
using System.Globalization;
using System.Windows.Forms;
using Kainos.Modules;

namespace Kainos.Domino.UI
{
    internal class LogQsoForm : Form
    {
        private readonly TextBox _call, _name, _grid, _qth, _sent, _rcvd, _on, _off, _freq, _mode, _pwr, _comment;
        public DominoQso Qso { get; private set; }

        public LogQsoForm(IKainosHost host, DominoQso q)
        {
            IKainosTheme t = host.Theme;
            int s(float v) => t.S(v);
            Text = "DominoEX / THOR: log QSO";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = MinimizeBox = false;
            StartPosition = FormStartPosition.CenterParent;
            ShowInTaskbar = false;
            ClientSize = new Size(s(560), s(330));
            Font = t.Font(s(12), false);
            CultureInfo ic = CultureInfo.InvariantCulture;

            int col = 0, row = 0;
            Func<string, string, int, TextBox> field = (label, value, width) =>
            {
                int x = s(12) + col * s(276), y = s(12) + row * s(34);
                Controls.Add(new Label { Text = label, Location = new Point(x, y + s(4)), Size = new Size(s(96), s(20)) });
                TextBox tb = new TextBox { Text = value ?? "", Location = new Point(x + s(98), y), Width = s(width) };
                Controls.Add(tb);
                if (++col == 2) { col = 0; row++; }
                return tb;
            };
            _call = field("Callsign", q.Call, 160); _call.CharacterCasing = CharacterCasing.Upper;
            _name = field("Name", q.Name, 160);
            _grid = field("Grid", q.Grid, 160);
            _qth = field("QTH", q.Qth, 160);
            _sent = field("Report sent", q.RstSent, 160);
            _rcvd = field("Report rcvd", q.RstRcvd, 160);
            _on = field("Start (UTC)", q.On.ToString("yyyy-MM-dd HH:mm", ic), 160);
            _off = field("End (UTC)", q.Off.ToString("yyyy-MM-dd HH:mm", ic), 160);
            _freq = field("Freq (MHz)", q.DialHz > 0 ? (q.DialHz / 1e6).ToString("0.000###", ic) : "", 160);
            _mode = field("Mode", q.Submode, 160);
            _pwr = field("Power", q.Power, 160);
            col = 0; row++;
            Controls.Add(new Label { Text = "Comment", Location = new Point(s(12), s(12) + row * s(34) + s(4)), Size = new Size(s(96), s(20)) });
            _comment = new TextBox { Text = q.Comment, Location = new Point(s(110), s(12) + row * s(34)), Width = s(438) };
            Controls.Add(_comment);

            Button ok = new Button { Text = "Log", DialogResult = DialogResult.OK, Location = new Point(s(366), s(286)), Size = new Size(s(88), s(32)) };
            Button cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Location = new Point(s(460), s(286)), Size = new Size(s(88), s(32)) };
            Controls.Add(ok);
            Controls.Add(cancel);
            AcceptButton = ok;
            CancelButton = cancel;
            t.Style(this);
            Shown += (o, e) => { (_rcvd.Text.Length == 0 ? _rcvd : _comment).Focus(); };
            FormClosing += (o, e) =>
            {
                if (DialogResult != DialogResult.OK) return;
                DominoQso r = read(out string problem);
                if (problem != null) { MessageBox.Show(this, problem, "DominoEX / THOR"); e.Cancel = true; return; }
                Qso = r;
            };
        }

        private DominoQso read(out string problem)
        {
            problem = null;
            CultureInfo ic = CultureInfo.InvariantCulture;
            DominoQso q = new DominoQso
            {
                Call = _call.Text.Trim(), Name = _name.Text.Trim(), Grid = _grid.Text.Trim(), Qth = _qth.Text.Trim(),
                RstSent = _sent.Text.Trim(), RstRcvd = _rcvd.Text.Trim(), Submode = _mode.Text.Trim().ToUpperInvariant(), Power = _pwr.Text.Trim(), Comment = _comment.Text.Trim(),
            };
            if (q.Call.Length < 3) { problem = "Their callsign, please."; return null; }
            DateTime on, off;
            DateTimeStyles st = DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal;
            if (!DateTime.TryParseExact(_on.Text.Trim(), "yyyy-MM-dd HH:mm", ic, st, out on)) { problem = "Start: as 2026-10-18 20:15 (UTC)."; return null; }
            if (!DateTime.TryParseExact(_off.Text.Trim(), "yyyy-MM-dd HH:mm", ic, st, out off)) { problem = "End: as 2026-10-18 20:25 (UTC)."; return null; }
            q.On = on; q.Off = off < on ? on : off;
            double mhz;
            if (_freq.Text.Trim().Length > 0)
            {
                if (!double.TryParse(_freq.Text.Trim(), NumberStyles.Float, ic, out mhz) || mhz <= 0) { problem = "Frequency in MHz, e.g. 14.070."; return null; }
                q.DialHz = Math.Round(mhz * 1e6);
            }
            return q;
        }
    }
}
