/*  MacroForm.cs

Editing one of the F1 - F8 messages: its button's label and its text, with the fields it can use.

This file is part of the Kainos DominoEX / THOR module. Copyright (C) 2026 Justin Cron K7JUS. GPLv3 or later.
*/

using System;
using System.Drawing;
using System.Windows.Forms;
using Kainos.Modules;

namespace Kainos.Domino.UI
{
    internal class MacroForm : Form
    {
        private readonly TextBox _label, _text;
        public Macro Macro { get; private set; }

        public MacroForm(IKainosHost host, int index, Macro m)
        {
            IKainosTheme t = host.Theme;
            int s(float v) => t.S(v);
            Text = "F" + (index + 1) + " message";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = MinimizeBox = false;
            StartPosition = FormStartPosition.CenterParent;
            ShowInTaskbar = false;
            ClientSize = new Size(s(520), s(330));
            Font = t.Font(s(12), false);

            Controls.Add(new Label { Text = "Label", Location = new Point(s(12), s(16)), Size = new Size(s(60), s(20)) });
            _label = new TextBox { Text = m.Label, Location = new Point(s(76), s(12)), Width = s(160), MaxLength = 12 };
            Controls.Add(_label);
            _text = new TextBox { Text = m.Text.Replace("\n", "\r\n"), Location = new Point(s(12), s(44)), Size = new Size(s(496), s(170)), Multiline = true, ScrollBars = ScrollBars.Vertical, AcceptsReturn = true, Font = new Font("Consolas", s(12), GraphicsUnit.Pixel) };
            Controls.Add(_text);
            Controls.Add(new Label
            {
                Text = "Fields: <MYCALL> <MYNAME> <MYGRID> <CALL> <NAME> <RST> <MODE>.  <RX> goes back to receive once what's before it has been sent; " +
                       "without it the transmitter stays on, idling, so you can carry on typing.",
                Location = new Point(s(12), s(222)), Size = new Size(s(496), s(52)), ForeColor = t.Dim,
            });

            Button def = new Button { Text = "Default", Location = new Point(s(12), s(286)), Size = new Size(s(88), s(32)) };
            def.Click += (o, e) => { Macro d = Macros.Default(index); _label.Text = d.Label; _text.Text = d.Text.Replace("\n", "\r\n"); };
            Button ok = new Button { Text = "Save", DialogResult = DialogResult.OK, Location = new Point(s(326), s(286)), Size = new Size(s(88), s(32)) };
            Button cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Location = new Point(s(420), s(286)), Size = new Size(s(88), s(32)) };
            Controls.Add(def); Controls.Add(ok); Controls.Add(cancel);
            CancelButton = cancel;
            t.Style(this);
            FormClosing += (o, e) =>
            {
                if (DialogResult != DialogResult.OK) return;
                Macro = new Macro { Label = _label.Text.Trim().Length > 0 ? _label.Text.Trim() : "F" + (index + 1), Text = _text.Text.Replace("\r\n", "\n") };
            };
        }
    }
}
