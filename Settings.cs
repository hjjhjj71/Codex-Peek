using System;
using System.IO;
using System.Web.Script.Serialization;

namespace CodexPeek
{
    public sealed class AppSettings
    {
        internal string LastSaveFailure = "";
        public bool AttachToCodex = false;
        public bool Expanded = false;
        public bool TopMost = true;
        public int RefreshSeconds = 60;
        public int OpacityPercent = 100;
        public int ScalePercent = 100;
        public int Left = -99999;
        public int Top = -99999;
        public string Anchor = "右上";
        public int AttachOffsetX = 0;
        public int AttachOffsetY = 0;
        public bool ShowStatusText = true;

        public void Normalize()
        {
            RefreshSeconds = Math.Max(30, Math.Min(600, RefreshSeconds));
            OpacityPercent = Math.Max(65, Math.Min(100, OpacityPercent));
            ScalePercent = Math.Max(80, Math.Min(160, ScalePercent));
            if (Anchor != "右上" && Anchor != "左上" && Anchor != "右下" && Anchor != "左下") Anchor = "右上";
            AttachOffsetX = Math.Max(-32768, Math.Min(32768, AttachOffsetX));
            AttachOffsetY = Math.Max(-32768, Math.Min(32768, AttachOffsetY));
        }

        public static AppSettings Load(string path)
        {
            try
            {
                if (File.Exists(path) && new FileInfo(path).Length < 16384)
                {
                    AppSettings value = new JavaScriptSerializer().Deserialize<AppSettings>(File.ReadAllText(path));
                    if (value != null) { value.Normalize(); return value; }
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            catch (ArgumentException) { }
            catch (InvalidOperationException) { }
            return new AppSettings();
        }

        public bool Save(string path)
        {
            string temp = path + ".tmp";
            try
            {
                Normalize();
                File.WriteAllText(temp, new JavaScriptSerializer().Serialize(this));
                if (File.Exists(path)) File.Replace(temp, path, null);
                else File.Move(temp, path);
                return true;
            }
            catch (IOException e) { LastSaveFailure = e.GetType().Name + ":" + e.HResult; return false; }
            catch (UnauthorizedAccessException e) { LastSaveFailure = e.GetType().Name + ":" + e.HResult; return false; }
        }
    }
}
