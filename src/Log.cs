// Console + file log. C# 5 only.
using System;
using System.Collections.Generic;
using System.IO;

namespace Windlass
{
    public static class Log
    {
        static readonly object Gate = new object();
        static string _file;
        public static bool Verbose;
        public static bool Quiet;

        // The last lines written, for the browser UI's log page (GET /api/logs). Same text as the console and the file.
        public const int KeepLines = 500;
        static readonly LinkedList<KeyValuePair<long, string>> Recent = new LinkedList<KeyValuePair<long, string>>();
        static long _seq;

        // Lines written after sequence number `after` (0 = all kept), oldest first; last = the newest number so far.
        public static List<KeyValuePair<long, string>> Tail(long after, out long last)
        {
            lock (Gate)
            {
                List<KeyValuePair<long, string>> r = new List<KeyValuePair<long, string>>();
                foreach (KeyValuePair<long, string> kv in Recent) if (kv.Key > after) r.Add(kv);
                last = _seq;
                return r;
            }
        }

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
                _seq++;
                Recent.AddLast(new KeyValuePair<long, string>(_seq, line));
                while (Recent.Count > KeepLines) Recent.RemoveFirst();
                if (!Quiet || level == "ERROR") Console.Error.WriteLine(line);
                if (_file != null) try { File.AppendAllText(_file, line + Environment.NewLine); } catch (IOException) { }
            }
        }
    }
}
