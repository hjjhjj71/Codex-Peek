using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace CodexPeek
{
    internal sealed class MiniForm : Form
    {
        public readonly AppSettings Settings;
        public QuotaSnapshot Quota = new QuotaSnapshot();
        public UsageSnapshot Usage = new UsageSnapshot();
        public readonly bool Demo;
        public event EventHandler DataChanged;
        private readonly string settingsPath;
        private readonly QuotaClient quotaClient;
        private readonly TelemetryReader telemetry;
        private readonly CancellationTokenSource shutdown = new CancellationTokenSource();
        private readonly System.Windows.Forms.Timer tick = new System.Windows.Forms.Timer();
        private readonly NotifyIcon tray = new NotifyIcon();
        private readonly ContextMenuStrip menu = new ContextMenuStrip();
        private readonly ToolTip tooltip = new ToolTip();
        private DateTime nextQuota = DateTime.MinValue, nextUsage = DateTime.MinValue;
        private DateTime lastRefreshAttempt = DateTime.MinValue;
        private bool quotaBusy, usageBusy, closing, hiddenByUser, dragging;
        private IntPtr attached;
        private Rectangle targetBounds;
        private DetailsForm details;
        private SettingsForm settingsForm;
        private float drawingScale = 1;
        private Icon appIcon;

        public MiniForm(AppSettings settings, string path, bool demo)
        {
            Settings = settings; settingsPath = path; Demo = demo;
            quotaClient = new QuotaClient(); telemetry = new TelemetryReader();
            Text = demo ? "Codex Peek · 演示数据" : "Codex Peek";
            AccessibleName = "Codex 周额度悬浮窗，回车打开详情，右键打开菜单";
            BackColor = Theme.Background; ForeColor = Theme.Text;
            AutoScaleMode = AutoScaleMode.None; FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual; ShowInTaskbar = true;
            DoubleBuffered = true; KeyPreview = true; Cursor = Cursors.Hand;
            appIcon = CreateIcon(); Icon = appIcon;
            SetupMenu(); tray.Icon = appIcon; tray.Text = "Codex Peek · 周额度";
            tray.ContextMenuStrip = menu; tray.Visible = true;
            tray.DoubleClick += delegate { Reveal(); OpenDetails(); };
            ContextMenuStrip = menu;
            ApplySettings(false);
            Rectangle work = Screen.PrimaryScreen.WorkingArea;
            Location = Settings.Left == -99999 ? new Point(work.Right - Width - 24, work.Top + 90) : new Point(Settings.Left, Settings.Top);
            KeepOnScreen();
            if (Demo) LoadDemo();
            tick.Interval = 750;
            tick.Tick += OnTick;
            Shown += delegate { ApplySettings(false); tick.Start(); OnTick(this, EventArgs.Empty); };
        }

        private void SetupMenu()
        {
            menu.Items.Add("打开详情", null, delegate { Reveal(); OpenDetails(); });
            menu.Items.Add("立即刷新", null, delegate { RefreshNow(); });
            menu.Items.Add("展开 / 收起小窗", null, delegate { Settings.Expanded = !Settings.Expanded; ApplySettings(true); });
            menu.Items.Add("吸附 Codex / 独立悬浮", null, delegate { Settings.AttachToCodex = !Settings.AttachToCodex; hiddenByUser = false; ApplySettings(true); });
            menu.Items.Add("设置", null, delegate { OpenSettings(); });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("显示悬浮窗", null, delegate { Reveal(); });
            menu.Items.Add("隐藏到托盘", null, delegate { hiddenByUser = true; Hide(); });
            menu.Items.Add("退出", null, delegate { Close(); });
        }

        public void ApplySettings(bool save)
        {
            Settings.Normalize();
            drawingScale = Math.Max(1, NativeWindows.GetDpi(IsHandleCreated ? Handle : IntPtr.Zero) / 96f) * Settings.ScalePercent / 100f;
            ClientSize = new Size((int)(286 * drawingScale), (int)((Settings.Expanded ? 108 : 56) * drawingScale));
            Region old = Region;
            using (GraphicsPath path = Theme.Round(new RectangleF(0, 0, Width, Height), 12 * drawingScale)) Region = new Region(path);
            if (old != null) old.Dispose();
            Opacity = Settings.OpacityPercent / 100.0;
            TopMost = Settings.AttachToCodex || Settings.TopMost;
            if (save) SaveSettings();
            KeepOnScreen(); Invalidate(); nextQuota = DateTime.MinValue;
        }

        public void SaveSettings()
        {
            if (!Settings.Save(settingsPath)) tray.ShowBalloonTip(4000, "设置未保存", "程序所在文件夹不可写，本次设置仅在运行期间生效。", ToolTipIcon.Warning);
        }

        private void KeepOnScreen()
        {
            Rectangle area = Screen.FromRectangle(Bounds).WorkingArea;
            Location = new Point(Math.Max(area.Left, Math.Min(Left, area.Right - Width)), Math.Max(area.Top, Math.Min(Top, area.Bottom - Height)));
        }

        private void Reveal()
        {
            hiddenByUser = false;
            if (!Visible) Show();
            KeepOnScreen();
        }

        private void OnTick(object sender, EventArgs e)
        {
            if (closing) return;
            UpdateAttachment();
            if (!Demo)
            {
                if (DateTime.UtcNow >= nextQuota) RefreshQuota();
                if (DateTime.UtcNow >= nextUsage) RefreshUsage();
            }
            Invalidate();
        }

        private void UpdateAttachment()
        {
            if (hiddenByUser || dragging) return;
            if (!Settings.AttachToCodex)
            {
                attached = IntPtr.Zero;
                if (!Visible) Show();
                return;
            }
            IntPtr target = NativeWindows.FindCodexWindow();
            bool ownForeground = NativeWindows.IsForegroundWindow(Handle) ||
                (details != null && !details.IsDisposed && NativeWindows.IsForegroundWindow(details.Handle)) ||
                (settingsForm != null && !settingsForm.IsDisposed && NativeWindows.IsForegroundWindow(settingsForm.Handle)) || menu.Visible;
            if (target == IntPtr.Zero)
            {
                attached = IntPtr.Zero;
                if (NativeWindows.IsCodexRunning() && !ownForeground) { if (Visible) Hide(); }
                else if (!Visible) Show();
                return;
            }
            attached = target;
            if (!NativeWindows.IsForegroundWindow(target) && !ownForeground)
            {
                if (Visible) Hide();
                return;
            }
            if (!NativeWindows.TryGetWindowBounds(target, out targetBounds)) return;
            int dpi = NativeWindows.GetDpi(target);
            float scale = Math.Max(1, dpi / 96f) * Settings.ScalePercent / 100f;
            if (Math.Abs(scale - drawingScale) > .05f)
            {
                // WM_DPICHANGED 完成前先按目标显示器缩放，避免吸附时尺寸抖动。
                drawingScale = scale;
                ClientSize = new Size((int)(286 * scale), (int)((Settings.Expanded ? 108 : 56) * scale));
                using (GraphicsPath p = Theme.Round(new RectangleF(0, 0, Width, Height), 12 * scale))
                { Region old = Region; Region = new Region(p); if (old != null) old.Dispose(); }
            }
            Point anchor = GetAnchor();
            Rectangle bounds = new Rectangle(anchor.X + Settings.AttachOffsetX, anchor.Y + Settings.AttachOffsetY, Width, Height);
            Rectangle area = Screen.FromRectangle(targetBounds).WorkingArea;
            bounds.X = Math.Max(area.Left, Math.Min(bounds.X, area.Right - bounds.Width));
            bounds.Y = Math.Max(area.Top, Math.Min(bounds.Y, area.Bottom - bounds.Height));
            if (!Visible) Show();
            NativeWindows.PlaceOverlay(Handle, bounds, true);
        }

        private Point GetAnchor()
        {
            int margin = (int)(16 * drawingScale);
            int yMargin = (int)(48 * drawingScale);
            return new Point(Settings.Anchor.StartsWith("右") ? targetBounds.Right - Width - margin : targetBounds.Left + margin,
                Settings.Anchor.EndsWith("上") ? targetBounds.Top + yMargin : targetBounds.Bottom - Height - margin);
        }

        public void RefreshNow()
        {
            if (Demo) { Publish(); return; }
            if ((DateTime.UtcNow - lastRefreshAttempt).TotalSeconds >= 5) { nextQuota = DateTime.MinValue; RefreshQuota(); }
            nextUsage = DateTime.MinValue; RefreshUsage();
        }

        private async void RefreshQuota()
        {
            if (quotaBusy || closing) return;
            quotaBusy = true; lastRefreshAttempt = DateTime.UtcNow;
            nextQuota = DateTime.UtcNow.AddSeconds(Settings.RefreshSeconds);
            try { Quota = await quotaClient.RefreshAsync(shutdown.Token); }
            catch (OperationCanceledException) { }
            catch (Exception) { Quota = new QuotaSnapshot { Message = "额度读取失败，请稍后重试", IsStale = true }; }
            finally { quotaBusy = false; }
            if (!closing) Publish();
        }

        private async void RefreshUsage()
        {
            if (usageBusy || closing) return;
            usageBusy = true; nextUsage = DateTime.UtcNow.AddSeconds(3);
            try { Usage = await Task.Run(() => telemetry.ReadSnapshot()); }
            catch (Exception) { Usage = new UsageSnapshot { StatusDetail = "本地日志暂时无法读取", CoverageLimited = true }; }
            finally { usageBusy = false; }
            if (!closing) Publish();
        }

        private void Publish()
        {
            string status = Theme.StateText(Usage.State);
            string stale = Quota.IsStale && Quota.RemainingPercent.HasValue ? " · 旧数据" : "";
            tray.Text = ("Codex 周剩余 " + Theme.Percent(Quota.RemainingPercent) + stale + " · " + status);
            AccessibleDescription = tray.Text;
            tooltip.SetToolTip(this, (Demo ? "演示数据\n" : "") + Quota.Message + "\n" + Theme.Reset(Quota.ResetsAtUtc, false) + "\n" + Usage.StatusDetail + "\n点击查看详情 · 拖动移动 · 右键设置");
            Invalidate();
            EventHandler handler = DataChanged; if (handler != null) handler(this, EventArgs.Empty);
        }

        public void OpenDetails()
        {
            if (details != null && !details.IsDisposed) { details.Show(); details.Activate(); return; }
            details = new DetailsForm(this); details.Show(this);
        }

        public void OpenSettings()
        {
            if (settingsForm != null && !settingsForm.IsDisposed) { settingsForm.Activate(); return; }
            settingsForm = new SettingsForm(this); settingsForm.Show(this);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e); Graphics g = e.Graphics; g.ScaleTransform(drawingScale, drawingScale); g.SmoothingMode = SmoothingMode.AntiAlias;
            int h = Settings.Expanded ? 108 : 56;
            using (GraphicsPath p = Theme.Round(new RectangleF(.5f, .5f, 285, h - 1), 12))
            using (Pen pen = new Pen(Theme.Border)) g.DrawPath(pen, p);
            using (Brush b = new SolidBrush(Theme.StateColor(Usage.State))) g.FillEllipse(b, 15, 18, 7, 7);
            Draw(g, Settings.ShowStatusText ? Theme.StateText(Usage.State) : "CODEX", 28, 11, 56, 21, 9, Theme.Text, false);
            Draw(g, Demo ? "演示 / 周" : Quota.IsStale && Quota.RemainingPercent.HasValue ? "周剩余·旧" : "周剩余", 96, 11, 84, 21, 9, Theme.Muted, false);
            Color quotaColor = Quota.IsStale ? Theme.Amber : Quota.RemainingPercent <= 10 ? Theme.Red : Theme.Accent;
            Draw(g, Theme.Percent(Quota.RemainingPercent), 176, 7, 70, 29, 16, quotaColor, true);
            Draw(g, "···", 253, 4, 25, 30, 16, Theme.Muted, true);
            using (Brush b = new SolidBrush(Theme.Surface)) g.FillRectangle(b, 15, 42, 256, 3);
            if (Quota.RemainingPercent.HasValue)
                using (Brush b = new SolidBrush(quotaColor)) g.FillRectangle(b, 15, 42, (float)(256 * Math.Max(0, Math.Min(100, Quota.RemainingPercent.Value)) / 100), 3);
            if (Settings.Expanded)
            {
                Draw(g, Theme.Reset(Quota.ResetsAtUtc, true), 15, 51, 244, 22, 8.5f, Theme.Muted, false);
                string secondary = Quota.IsStale ? (Quota.RemainingPercent.HasValue ? "离线 · 显示上次额度" : "额度暂不可用 · 点击详情") : "今日 " + Theme.Tokens(Usage.Today.Total) + " Token";
                Draw(g, secondary, 15, 76, 250, 20, 8.5f, Quota.IsStale ? Theme.Amber : Theme.Text, false);
            }
        }

        private static void Draw(Graphics g, string text, float x, float y, float w, float h, float size, Color color, bool bold)
        {
            using (Font f = new Font("Microsoft YaHei UI", size * 96f / 72f, bold ? FontStyle.Bold : FontStyle.Regular, GraphicsUnit.Pixel)) using (Brush b = new SolidBrush(color))
            using (StringFormat format = new StringFormat { LineAlignment = StringAlignment.Center, Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap })
                g.DrawString(text, f, b, new RectangleF(x, y, w, h), format);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left) return;
            if (e.X > 246 * drawingScale) { menu.Show(this, new Point(Width - 20, Height)); return; }
            Point initial = Location;
            dragging = true; NativeWindows.BeginDrag(Handle); dragging = false;
            if (Math.Abs(Left - initial.X) + Math.Abs(Top - initial.Y) < 4) { OpenDetails(); return; }
            if (Settings.AttachToCodex && attached != IntPtr.Zero)
            { Point anchor = GetAnchor(); Settings.AttachOffsetX = Left - anchor.X; Settings.AttachOffsetY = Top - anchor.Y; }
            else { Settings.Left = Left; Settings.Top = Top; }
            SaveSettings();
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.KeyCode == Keys.Enter) { OpenDetails(); e.Handled = true; }
            if (e.KeyCode == Keys.F5) { RefreshNow(); e.Handled = true; }
            if (e.KeyCode == Keys.Escape) { hiddenByUser = true; Hide(); e.Handled = true; }
        }

        protected override void WndProc(ref Message message)
        {
            if ((uint)message.Msg == Program.ShowMessage)
            {
                Reveal(); OpenDetails(); return;
            }
            base.WndProc(ref message);
            if (message.Msg == 0x02E0 && !closing) BeginInvoke(new Action(delegate { ApplySettings(false); }));
        }

        protected override bool ShowWithoutActivation { get { return true; } }

        protected override bool ProcessCmdKey(ref Message message, Keys keyData)
        {
            if (keyData == Keys.Enter) { OpenDetails(); return true; }
            if (keyData == Keys.F5) { RefreshNow(); return true; }
            return base.ProcessCmdKey(ref message, keyData);
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            closing = true; tick.Stop(); shutdown.Cancel(); tray.Visible = false;
            if (!Settings.AttachToCodex) { Settings.Left = Left; Settings.Top = Top; }
            Settings.Save(settingsPath); base.OnFormClosing(e);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                tick.Dispose(); tray.Dispose(); menu.Dispose(); tooltip.Dispose();
                // 请求在取消后自行结束，避免 Dispose 与后台的锁/日志读取互相竞争。
                if (!quotaBusy) quotaClient.Dispose();
                if (!usageBusy) telemetry.Dispose();
                if (appIcon != null) appIcon.Dispose();
            }
            base.Dispose(disposing);
        }

        private static Icon CreateIcon()
        {
            using (Bitmap bitmap = new Bitmap(32, 32))
            using (Graphics g = Graphics.FromImage(bitmap))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias; g.Clear(Color.Transparent);
                using (Brush b = new SolidBrush(Theme.Background)) g.FillEllipse(b, 0, 0, 31, 31);
                using (Pen p = new Pen(Theme.Accent, 3)) g.DrawArc(p, 7, 7, 17, 17, 45, 275);
                using (Brush b = new SolidBrush(Theme.Green)) g.FillEllipse(b, 22, 22, 7, 7);
                IntPtr raw = bitmap.GetHicon();
                try { using (Icon temporary = Icon.FromHandle(raw)) return (Icon)temporary.Clone(); }
                finally { DestroyIcon(raw); }
            }
        }
        [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr hIcon);

        private void LoadDemo()
        {
            Quota = new QuotaSnapshot { RemainingPercent = 66, ResetsAtUtc = DateTime.UtcNow.AddDays(5).AddHours(7), FetchedAtUtc = DateTime.UtcNow, Plan = "Plus", Message = "演示数据 · 并非真实账号额度", Source = "演示" };
            Quota.Windows.Add(new RateWindow { Name = "5 小时", UsedPercent = 12, WindowMinutes = 300, ResetsAtUtc = DateTime.UtcNow.AddHours(3) });
            Usage = new UsageSnapshot { State = WorkState.Working, ActiveCount = 1, StatusDetail = "演示：1 个任务正在工作", TokensPerSecond = 38.4, SpeedObservedAtUtc = DateTime.UtcNow, LastEventUtc = DateTime.UtcNow, CoverageDescription = "演示数据 · 用于预览界面" };
            Usage.Today = new TokenTotals { Total = 126420, Input = 105260, Output = 21160, CachedInput = 72480, Reasoning = 6310, Sessions = 6 };
            Usage.SevenDays = new TokenTotals { Total = 8265072, Input = 7160200, Output = 1104872, CachedInput = 5236600, Reasoning = 316230, Sessions = 28 };
            Publish();
        }
    }
}
