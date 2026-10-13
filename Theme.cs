using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Windows.Forms;

namespace CodexPeek
{
    internal static class Theme
    {
        public static readonly Color Background = Color.FromArgb(36, 42, 46);
        public static readonly Color Surface = Color.FromArgb(46, 53, 57);
        public static readonly Color Border = Color.FromArgb(69, 78, 82);
        public static readonly Color Text = Color.FromArgb(237, 243, 242);
        public static readonly Color Muted = Color.FromArgb(169, 183, 189);
        public static readonly Color Accent = Color.FromArgb(133, 211, 193);
        public static readonly Color Green = Color.FromArgb(130, 224, 166);
        public static readonly Color Red = Color.FromArgb(248, 115, 116);
        public static readonly Color Amber = Color.FromArgb(236, 190, 111);

        public static Font Font(float size, bool bold)
        {
            return new Font("Microsoft YaHei UI", size, bold ? FontStyle.Bold : FontStyle.Regular, GraphicsUnit.Point);
        }

        public static GraphicsPath Round(RectangleF r, float radius)
        {
            GraphicsPath p = new GraphicsPath();
            float d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure(); return p;
        }

        public static string StateText(WorkState state)
        {
            switch (state)
            {
                case WorkState.Working: return "工作中";
                case WorkState.Attention: return "需处理";
                case WorkState.Idle: return "空闲";
                case WorkState.Offline: return "未打开";
                default: return "待识别";
            }
        }

        public static Color StateColor(WorkState state)
        {
            return state == WorkState.Working ? Green : state == WorkState.Attention ? Red : Muted;
        }

        public static string Percent(double? percent)
        {
            return percent.HasValue ? Math.Max(0, Math.Min(100, percent.Value)).ToString("0.#", CultureInfo.InvariantCulture) + "%" : "--";
        }

        public static string Tokens(long value)
        {
            if (value >= 1000000000) return (value / 1000000000.0).ToString("0.##") + "B";
            if (value >= 1000000) return (value / 1000000.0).ToString("0.##") + "M";
            if (value >= 10000) return (value / 1000.0).ToString("0.#") + "K";
            return value.ToString("N0");
        }

        public static string Reset(DateTime? utc, bool shortText)
        {
            if (!utc.HasValue) return "重置时间未知";
            TimeSpan d = utc.Value - DateTime.UtcNow;
            if (d.TotalSeconds <= 0) return "等待额度刷新";
            if (d.TotalDays >= 1) return ((int)d.TotalDays) + "天 " + d.Hours + "小时后重置";
            if (d.TotalHours >= 1) return ((int)d.TotalHours) + "小时 " + d.Minutes + "分钟后重置";
            return Math.Max(1, (int)Math.Ceiling(d.TotalMinutes)) + "分钟后重置";
        }

        public static Label Label(string text, float size, Color color, bool bold)
        {
            Label l = new Label(); l.Text = text; l.AutoSize = false; l.Font = Font(size, bold);
            l.ForeColor = color; l.BackColor = Color.Transparent; l.TextAlign = ContentAlignment.MiddleLeft;
            return l;
        }

        public static Button Button(string text)
        {
            Button b = new Button(); b.Text = text; b.Font = Font(9, false);
            b.ForeColor = Text; b.BackColor = Surface; b.FlatStyle = FlatStyle.Flat;
            b.FlatAppearance.BorderColor = Border; b.FlatAppearance.MouseOverBackColor = Color.FromArgb(62, 73, 77);
            b.Cursor = Cursors.Hand; b.Height = 32; b.UseVisualStyleBackColor = false;
            return b;
        }
    }
}
