// Console + file log. C# 5 only.
using System;
using System.IO;

namespace Windlass
{
    public static class Log
    {
        static readonly object Gate = new object();
        static string _file;
        public static bool Verbose;
        public static bool Quiet;

        public static void ToFile(string path)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            _file = path;
        }

        public static void Debug(string m) { if (Verbose) Write("DEBUG", m); }
        public static void Info(string m) { Write("INFO", m); }
        public static void Warn(string m) { Write("WARN", m); }
        public static void Error(string m) { Write("ERROR", m); }

        static void Write(string level, string m)
        {
            string line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " " + level.PadRight(5) + " " + m;
            lock (Gate)
            {
                if (!Quiet || level == "ERROR") Console.Error.WriteLine(line);
                if (_file != null) try { File.AppendAllText(_file, line + Environment.NewLine); } catch (IOException) { }
            }
        }
    }
}
