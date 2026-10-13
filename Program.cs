using System;
using System.IO;
using System.Threading;
using System.Windows.Forms;

namespace CodexPeek
{
    internal static class Program
    {
        [STAThread]
        private static void Main(string[] args)
        {
            bool demo = Array.IndexOf(args, "--demo") >= 0;
            bool created;
            using (Mutex singleton = new Mutex(true, "Local\\CodexPeek-" + Environment.UserName + (demo ? "-demo" : ""), out created))
            {
                if (!created)
                {
                    PostMessage(new IntPtr(0xffff), ShowMessage, IntPtr.Zero, IntPtr.Zero);
                    return;
                }
                NativeWindows.EnableDpiAwareness();
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                string settingsPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, demo ? "CodexPeek.demo.settings.json" : "CodexPeek.settings.json");
                try { Application.Run(new MiniForm(AppSettings.Load(settingsPath), settingsPath, demo)); }
                catch (Exception ex)
                {
                    MessageBox.Show("无法启动 Codex Peek（" + ex.GetType().Name + "）。请将程序解压到可读写文件夹后重试。", "Codex Peek", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
                singleton.ReleaseMutex();
            }
        }

        internal static readonly uint ShowMessage = RegisterWindowMessage("CodexPeek.ShowDetails.1");
        [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        private static extern uint RegisterWindowMessage(string message);
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
    }
}
