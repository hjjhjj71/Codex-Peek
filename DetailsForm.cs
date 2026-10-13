using System;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Windows.Forms;

namespace CodexPeek
{
    internal sealed class DetailsForm : Form
    {
        private readonly MiniForm app;
        private readonly Label remaining, reset, quotaMessage, otherQuota, state, total, sessionCount;
        private readonly Label input, cached, output, reasoning, speed, speedNote, activity, coverage;
        private readonly Button today, week;
        private readonly Panel meter;
        private readonly ToolTip tips = new ToolTip();
        private readonly Timer timer = new Timer();
        private bool sevenDays;

        public DetailsForm(MiniForm owner)
        {
            SuspendLayout();
            app = owner; Text = owner.Demo ? "Codex Peek · 详情（演示数据）" : "Codex Peek · 详情";
            Icon = owner.Icon; BackColor = Theme.Background; ForeColor = Theme.Text;
            ClientSize = new Size(440, 662); FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false; MinimizeBox = false; StartPosition = FormStartPosition.CenterScreen;
            Font = Theme.Font(9, false); KeyPreview = true;
            AddLabel("CODEX PEEK", 22, 16, 230, 26, 13, Theme.Text, true);
            state = AddLabel("", 294, 16, 124, 26, 9, Theme.Muted, false);
            state.TextAlign = ContentAlignment.MiddleRight;
            AddLabel(owner.Demo ? "演示数据 / 周剩余额度" : "周剩余额度", 22, 52, 230, 23, 9, Theme.Muted, false);
            remaining = AddLabel("--", 20, 72, 148, 51, 31, Theme.Accent, true);
            reset = AddLabel("重置时间未知", 180, 82, 238, 40, 9, Theme.Muted, false);
            reset.TextAlign = ContentAlignment.MiddleRight;
            meter = new Panel { Location = new Point(22, 135), Size = new Size(396, 5), BackColor = Theme.Surface };
            meter.Paint += delegate(object s, PaintEventArgs e)
            {
                if (app.Quota.RemainingPercent.HasValue)
                    using (Brush b = new SolidBrush(app.Quota.IsStale ? Theme.Amber : Theme.Accent))
                        e.Graphics.FillRectangle(b, 0, 0, (float)(meter.Width * Math.Max(0, Math.Min(100, app.Quota.RemainingPercent.Value)) / 100), meter.Height);
            }; Controls.Add(meter);
            quotaMessage = AddLabel("正在读取额度", 22, 151, 396, 38, 8.5f, Theme.Muted, false);
            otherQuota = AddLabel("", 22, 192, 396, 27, 8.5f, Theme.Muted, false);
            Line(222);
            today = Theme.Button("今日"); today.SetBounds(22, 238, 67, 29); today.Click += delegate { sevenDays = false; RefreshData(); }; Controls.Add(today);
            week = Theme.Button("近 7 天"); week.SetBounds(96, 238, 85, 29); week.Click += delegate { sevenDays = true; RefreshData(); }; Controls.Add(week);
            AddLabel("本机 Token 用量", 227, 238, 191, 29, 9, Theme.Muted, false).TextAlign = ContentAlignment.MiddleRight;
            total = AddLabel("--", 20, 281, 270, 42, 25, Theme.Text, true);
            sessionCount = AddLabel("", 282, 286, 136, 34, 9, Theme.Muted, false); sessionCount.TextAlign = ContentAlignment.MiddleRight;
            AddLabel("输入", 22, 339, 160, 22, 8.5f, Theme.Muted, false);
            AddLabel("输出", 235, 339, 180, 22, 8.5f, Theme.Muted, false);
            input = AddLabel("--", 22, 361, 174, 27, 15, Theme.Text, true);
            output = AddLabel("--", 235, 361, 180, 27, 15, Theme.Text, true);
            cached = AddLabel("其中缓存 --", 22, 392, 195, 24, 8.5f, Theme.Muted, false);
            reasoning = AddLabel("其中推理 --", 235, 392, 185, 24, 8.5f, Theme.Muted, false);
            Line(429);
            AddLabel("最近一轮估算速度", 22, 444, 239, 28, 9, Theme.Muted, false);
            speed = AddLabel("-- tok/s", 268, 441, 150, 33, 16, Theme.Text, true); speed.TextAlign = ContentAlignment.MiddleRight;
            speedNote = AddLabel("", 22, 479, 396, 46, 8.5f, Theme.Muted, false);
            activity = AddLabel("", 22, 532, 396, 36, 9, Theme.Text, false);
            coverage = AddLabel("", 22, 574, 396, 42, 8, Theme.Muted, false);
            Button refresh = Theme.Button("刷新"); refresh.SetBounds(22, 626, 72, 28); refresh.Click += delegate { app.RefreshNow(); }; Controls.Add(refresh);
            Button copy = Theme.Button("复制统计"); copy.SetBounds(104, 626, 92, 28); copy.Click += CopyStats; Controls.Add(copy);
            Button settings = Theme.Button("设置"); settings.SetBounds(346, 626, 72, 28); settings.Click += delegate { app.OpenSettings(); }; Controls.Add(settings);
            tips.SetToolTip(cached, "缓存 Token 已包含在输入中，不重复计入总量。");
            tips.SetToolTip(reasoning, "推理 Token 已包含在输出中，不重复计入总量。");
            tips.SetToolTip(speed, "完成回合的输出 Token ÷ 从开始到结束的耗时，包含工具、网络与等待时间。并非实时解码速度。");
            app.DataChanged += Changed;
            timer.Interval = 1000; timer.Tick += Changed; timer.Start();
            FormClosed += delegate { app.DataChanged -= Changed; timer.Stop(); timer.Dispose(); tips.Dispose(); };
            KeyDown += delegate(object s, KeyEventArgs e) { if (e.KeyCode == Keys.Escape) Close(); if (e.KeyCode == Keys.F5) app.RefreshNow(); };
            RefreshData();
            AutoScaleDimensions = new SizeF(96, 96); AutoScaleMode = AutoScaleMode.Dpi;
            ResumeLayout(false);
        }

        private Label AddLabel(string text, int x, int y, int w, int h, float size, Color color, bool bold)
        {
            Label label = Theme.Label(text, size, color, bold); label.SetBounds(x, y, w, h); Controls.Add(label); return label;
        }

        private void Line(int y)
        {
            Controls.Add(new Panel { Location = new Point(22, y), Size = new Size(396, 1), BackColor = Theme.Border });
        }

        private void Changed(object sender, EventArgs e) { if (!IsDisposed) RefreshData(); }

        private void RefreshData()
        {
            QuotaSnapshot q = app.Quota; UsageSnapshot u = app.Usage;
            remaining.Text = Theme.Percent(q.RemainingPercent);
            remaining.ForeColor = q.IsStale ? Theme.Amber : q.RemainingPercent <= 10 ? Theme.Red : Theme.Accent;
            reset.Text = Theme.Reset(q.ResetsAtUtc, false) + (q.ResetsAtUtc.HasValue ? "\n" + q.ResetsAtUtc.Value.ToLocalTime().ToString("MM-dd HH:mm") : "");
            string age = q.FetchedAtUtc.HasValue ? " · " + Age(q.FetchedAtUtc.Value) + "更新" : "";
            quotaMessage.Text = q.Message + age;
            quotaMessage.ForeColor = q.IsStale || q.LoginRequired ? Theme.Amber : Theme.Muted;
            tips.SetToolTip(quotaMessage, q.Message + (q.FetchedAtUtc.HasValue ? "\n最后成功：" + q.FetchedAtUtc.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") : ""));
            RateWindow other = q.Windows.FirstOrDefault(x => Math.Abs(x.WindowMinutes - 10080) > 1);
            otherQuota.Text = (String.IsNullOrEmpty(q.Plan) ? "" : q.Plan + "   ·   ") + (other == null ? "暂无其他周期额度" : other.Name + "剩余 " + Theme.Percent(100 - other.UsedPercent));
            if (!String.IsNullOrEmpty(q.Credits) && q.Credits != "0") otherQuota.Text += "   ·   附加额度 " + q.Credits;
            otherQuota.AutoEllipsis = true; tips.SetToolTip(otherQuota, otherQuota.Text);
            state.Text = "● " + Theme.StateText(u.State) + (u.ActiveCount > 1 ? " ×" + u.ActiveCount : "");
            state.ForeColor = Theme.StateColor(u.State);
            tips.SetToolTip(state, u.StatusDetail);
            TokenTotals t = sevenDays ? u.SevenDays : u.Today;
            bool hasData = t.Sessions > 0 || t.Total > 0;
            total.Text = hasData ? Theme.Tokens(t.Total) : "暂无数据";
            tips.SetToolTip(total, t.Total.ToString("N0") + " Token（输入 + 输出）");
            sessionCount.Text = t.Sessions + " 个会话";
            input.Text = hasData ? Theme.Tokens(t.Input) : "--"; output.Text = hasData ? Theme.Tokens(t.Output) : "--";
            tips.SetToolTip(input, t.Input.ToString("N0") + " Token，包含缓存"); tips.SetToolTip(output, t.Output.ToString("N0") + " Token，包含推理");
            cached.Text = "其中缓存 " + (hasData ? Theme.Tokens(t.CachedInput) : "--");
            reasoning.Text = "其中推理 " + (hasData ? Theme.Tokens(t.Reasoning) : "--");
            speed.Text = u.TokensPerSecond.HasValue ? u.TokensPerSecond.Value.ToString("0.0") + " tok/s" : "-- tok/s";
            speedNote.Text = "输出 Token / 整轮耗时，含工具与等待。" + (u.SpeedObservedAtUtc.HasValue ? "\n样本：" + Age(u.SpeedObservedAtUtc.Value) : "\n等待一轮任务完成后计算。");
            activity.Text = u.StatusDetail;
            if (u.LastEventUtc.HasValue) activity.Text += "\n最近活动 " + Age(u.LastEventUtc.Value);
            coverage.Text = u.CoverageDescription + "\n本机用量与账号周限统计范围不同。";
            coverage.ForeColor = u.CoverageLimited ? Theme.Amber : Theme.Muted;
            today.BackColor = sevenDays ? Theme.Surface : Color.FromArgb(61, 90, 85);
            week.BackColor = sevenDays ? Color.FromArgb(61, 90, 85) : Theme.Surface;
            meter.Invalidate();
        }

        private static string Age(DateTime utc)
        {
            double seconds = Math.Max(0, (DateTime.UtcNow - utc).TotalSeconds);
            if (seconds < 60) return "刚刚";
            if (seconds < 3600) return ((int)(seconds / 60)) + " 分钟前";
            if (seconds < 86400) return ((int)(seconds / 3600)) + " 小时前";
            return utc.ToLocalTime().ToString("MM-dd HH:mm");
        }

        private void CopyStats(object sender, EventArgs e)
        {
            TokenTotals t = sevenDays ? app.Usage.SevenDays : app.Usage.Today;
            StringBuilder s = new StringBuilder(app.Demo ? "Codex Peek 演示数据\n" : "Codex Peek 统计\n");
            s.AppendLine("周剩余：" + Theme.Percent(app.Quota.RemainingPercent) + (app.Quota.IsStale ? "（旧数据/未同步）" : ""));
            s.AppendLine((sevenDays ? "近 7 天" : "今日") + "本机 Token：" + t.Total.ToString("N0"));
            s.AppendLine("输入：" + t.Input + "；其中缓存：" + t.CachedInput);
            s.AppendLine("输出：" + t.Output + "；其中推理：" + t.Reasoning);
            s.AppendLine("估算速度：" + speed.Text + "（含工具与等待）");
            s.AppendLine(app.Usage.CoverageDescription);
            try { Clipboard.SetText(s.ToString()); ((Button)sender).Text = "已复制"; }
            catch (System.Runtime.InteropServices.ExternalException) { MessageBox.Show(this, "剪贴板正被其他程序使用，请重试。", "复制统计"); }
        }
    }
}
