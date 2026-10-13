using System;
using System.Drawing;
using System.Windows.Forms;

namespace CodexPeek
{
    internal sealed class SettingsForm : Form
    {
        private readonly MiniForm app;
        private readonly ComboBox mode, anchor;
        private readonly CheckBox expanded, topMost, statusText;
        private readonly NumericUpDown refresh, opacity, scale;

        public SettingsForm(MiniForm owner)
        {
            SuspendLayout();
            app = owner; Text = "Codex Peek · 设置"; Icon = owner.Icon;
            ClientSize = new Size(424, 556); BackColor = Theme.Background; ForeColor = Theme.Text;
            Font = Theme.Font(9, false); FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false; MinimizeBox = false; StartPosition = FormStartPosition.CenterScreen;
            Label header = Theme.Label("让额度待在顺手的位置", 14, Theme.Text, true); header.SetBounds(22, 18, 380, 32); Controls.Add(header);
            mode = Combo("显示模式", 69, new string[] { "独立悬浮窗", "吸附 Codex 窗口" }); mode.SelectedIndex = app.Settings.AttachToCodex ? 1 : 0;
            anchor = Combo("吸附位置", 111, new string[] { "右上", "左上", "右下", "左下" }); anchor.SelectedItem = app.Settings.Anchor;
            Label attachNote = Theme.Label("吸附时随 Codex 移动，切换到其他应用时隐藏。\n找不到 Codex 窗口时，退回独立悬浮。", 8, Theme.Muted, false); attachNote.SetBounds(22, 150, 380, 39); Controls.Add(attachNote);
            expanded = Check("展开小窗，显示重置时间和今日 Token", 205, app.Settings.Expanded);
            topMost = Check("独立悬浮时保持置顶", 239, app.Settings.TopMost);
            statusText = Check("在状态灯旁显示工作状态文字", 273, app.Settings.ShowStatusText);
            refresh = Number("额度刷新间隔（秒）", 321, 30, 600, 30, app.Settings.RefreshSeconds);
            opacity = Number("不透明度（%）", 363, 65, 100, 5, app.Settings.OpacityPercent);
            scale = Number("悬浮窗大小（%）", 405, 80, 160, 10, app.Settings.ScalePercent);
            Label privacy = Theme.Label("本地日志每 3 秒检查；额度默认每 60 秒刷新。\n只读登录与日志，不上传会话内容、不改写凭据。", 8, Theme.Muted, false); privacy.SetBounds(22, 450, 380, 38); Controls.Add(privacy);
            Button resetPosition = Theme.Button("重置位置"); resetPosition.SetBounds(22, 509, 101, 30); resetPosition.Click += delegate { ResetPosition(); resetPosition.Text = "位置已重置"; }; Controls.Add(resetPosition);
            Button cancel = Theme.Button("取消"); cancel.SetBounds(239, 509, 72, 30); cancel.Click += delegate { Close(); }; Controls.Add(cancel);
            Button save = Theme.Button("保存"); save.SetBounds(323, 509, 79, 30); save.BackColor = Color.FromArgb(61, 90, 85); save.Click += Save; Controls.Add(save);
            AcceptButton = save; CancelButton = cancel;
            mode.SelectedIndexChanged += delegate { UpdateEnabled(); }; UpdateEnabled();
            AutoScaleDimensions = new SizeF(96, 96); AutoScaleMode = AutoScaleMode.Dpi;
            ResumeLayout(false);
        }

        private void UpdateEnabled() { anchor.Enabled = mode.SelectedIndex == 1; topMost.Enabled = mode.SelectedIndex == 0; }

        private ComboBox Combo(string title, int y, string[] values)
        {
            Label l = Theme.Label(title, 9, Theme.Text, false); l.SetBounds(22, y, 194, 28); Controls.Add(l);
            ComboBox c = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, BackColor = Theme.Surface, ForeColor = Theme.Text, FlatStyle = FlatStyle.Flat };
            c.Items.AddRange(values); c.SetBounds(226, y, 176, 28); Controls.Add(c); return c;
        }

        private CheckBox Check(string text, int y, bool value)
        {
            CheckBox c = new CheckBox { Text = text, Checked = value, ForeColor = Theme.Text, AutoSize = false, BackColor = Theme.Background };
            c.SetBounds(22, y, 380, 25); Controls.Add(c); return c;
        }

        private NumericUpDown Number(string title, int y, int min, int max, int step, int value)
        {
            Label l = Theme.Label(title, 9, Theme.Text, false); l.SetBounds(22, y, 260, 28); Controls.Add(l);
            NumericUpDown n = new NumericUpDown { Minimum = min, Maximum = max, Increment = step, Value = value, BackColor = Theme.Surface, ForeColor = Theme.Text, BorderStyle = BorderStyle.FixedSingle };
            n.SetBounds(292, y, 110, 28); Controls.Add(n); return n;
        }

        private void ResetPosition()
        {
            app.Settings.AttachOffsetX = 0; app.Settings.AttachOffsetY = 0;
            Rectangle area = Screen.FromControl(this).WorkingArea;
            app.Location = new Point(area.Right - app.Width - 24, area.Top + 90);
            app.Settings.Left = app.Left; app.Settings.Top = app.Top; app.SaveSettings();
        }

        private void Save(object sender, EventArgs e)
        {
            app.Settings.AttachToCodex = mode.SelectedIndex == 1;
            app.Settings.Anchor = (string)anchor.SelectedItem;
            app.Settings.Expanded = expanded.Checked; app.Settings.TopMost = topMost.Checked;
            app.Settings.ShowStatusText = statusText.Checked;
            app.Settings.RefreshSeconds = (int)refresh.Value;
            app.Settings.OpacityPercent = (int)opacity.Value; app.Settings.ScalePercent = (int)scale.Value;
            app.ApplySettings(true); Close();
        }
    }
}
