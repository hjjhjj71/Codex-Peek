using System;
using System.IO;
using System.Threading;

namespace CodexPeek.Tests
{
    internal static class TestProgram
    {
        [STAThread]
        private static int Main(string[] args)
        {
            try
            {
                if (Array.IndexOf(args, "--native") >= 0)
                {
                    NativeWindows.EnableDpiAwareness();
                    IntPtr window = NativeWindows.FindCodexWindow();
                    System.Drawing.Rectangle bounds;
                    bool found = NativeWindows.TryGetWindowBounds(window, out bounds);
                    Console.WriteLine("codexWindow={0}; running={1}; bounds={2}; dpi={3}", found, NativeWindows.IsCodexRunning(), bounds, NativeWindows.GetDpi(window));
                    return 0;
                }
                if (Array.IndexOf(args, "--live") >= 0)
                {
                    using (QuotaClient client = new QuotaClient())
                    {
                        QuotaSnapshot q = client.RefreshAsync(CancellationToken.None).GetAwaiter().GetResult();
                        Console.WriteLine("quota: remaining={0}; stale={1}; login={2}; message={3}", Theme.Percent(q.RemainingPercent), q.IsStale, q.LoginRequired, q.Message);
                    }
                    using (TelemetryReader reader = new TelemetryReader())
                    {
                        UsageSnapshot u = reader.ReadSnapshot();
                        Console.WriteLine("telemetry: today={0}; week={1}; sessions={2}; status={3}; partial={4}; speed={5}", u.Today.Total, u.SevenDays.Total, u.SevenDays.Sessions, u.State, u.CoverageLimited, u.TokensPerSecond);
                    }
                    return 0;
                }
                int quota = QuotaTests.Run();
                int telemetry = TelemetryTests.Run();
                int settings = SettingsChecks();
                Console.WriteLine("PASS: quota={0}, telemetry={1}, settings={2}, total={3}", quota, telemetry, settings, quota + telemetry + settings);
                return 0;
            }
            catch (Exception e) { Console.Error.WriteLine("FAIL: " + e.GetType().Name + " " + e.Message); return 1; }
        }

        private static int SettingsChecks()
        {
            string directory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "CodexPeek-settings-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory); string path = Path.Combine(directory, "settings.json");
            try
            {
                AppSettings s = AppSettings.Load(path);
                Assert(s.RefreshSeconds == 60, "settings default");
                s.RefreshSeconds = 0; s.OpacityPercent = 101; s.ScalePercent = 1; s.Anchor = "invalid"; s.Normalize();
                Assert(s.RefreshSeconds == 30 && s.OpacityPercent == 100 && s.ScalePercent == 80 && s.Anchor == "右上", "settings bounds");
                Assert(s.Save(path), "settings save: " + s.LastSaveFailure); s.Left = -256; Assert(s.Save(path), "settings replace: " + s.LastSaveFailure);
                Assert(AppSettings.Load(path).Left == -256, "negative monitor coordinates");
                File.WriteAllText(path, "broken"); Assert(AppSettings.Load(path).RefreshSeconds == 60, "corrupt settings recovery");
                Assert(Theme.Percent(null) == "--" && Theme.Percent(-1) == "0%" && Theme.Percent(120) == "100%", "missing quota display");
                return 7;
            }
            finally { Directory.Delete(directory, true); }
        }
        private static void Assert(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    }
}
