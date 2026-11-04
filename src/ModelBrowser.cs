// Lists folders and .gguf files for the browser UI's model picker, and remembers recent models. C# 5 only, ASCII only.
// It reads names and sizes, never file contents. Network paths (\\server\share) are refused: Windlass makes no outgoing
// connections, and a UNC path would open one.
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Windlass
{
    public static class ModelBrowser
    {
        public const int MaxEntries = 1000;
        public const int MaxRecent = 8;

        static bool IsNetworkPath(string p)
        {
            return p.StartsWith("\\\\", StringComparison.Ordinal) || p.StartsWith("//", StringComparison.Ordinal);
        }

        // A model file the page asked for: an existing local .gguf. Returns its full path or throws BackendException.
        public static string CheckModelFile(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) throw new BackendException("'path' is required");
            path = path.Trim().Trim('"');
            if (path.IndexOf('\0') >= 0 || IsNetworkPath(path)) throw new BackendException("only a .gguf file on a local drive can be loaded (no network paths)");
            string full;
            try { full = Path.GetFullPath(path); }
            catch (Exception e) { if (e is ArgumentException || e is NotSupportedException || e is PathTooLongException) throw new BackendException("that is not a valid path"); throw; }
            if (!full.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase)) throw new BackendException("a model file ends in .gguf");
            if (!File.Exists(full)) throw new BackendException("no model file at " + full, 404, "not_found_error");
            return full;
        }

        // dir = null or "": the drives and the recent models. Otherwise the folders and .gguf files inside dir.
        public static Dictionary<string, object> List(string dir, string recentFile)
        {
            Dictionary<string, object> r = new Dictionary<string, object>();
            r["schema"] = "windlass.ui.models/1";
            List<object> entries = new List<object>();
            bool truncated = false;
            if (string.IsNullOrWhiteSpace(dir))
            {
                r["dir"] = null;
                r["parent"] = null;
                foreach (DriveInfo d in DriveInfo.GetDrives())
                {
                    bool ready;
                    try { ready = d.IsReady; } catch (IOException) { ready = false; }
                    if (ready && d.DriveType != DriveType.Network) entries.Add(Entry(d.Name, d.Name, "dir", -1));
                }
            }
            else
            {
                dir = dir.Trim().Trim('"');
                if (dir.IndexOf('\0') >= 0 || IsNetworkPath(dir)) throw new BackendException("only folders on a local drive can be listed (no network paths)");
                string full;
                try { full = Path.GetFullPath(dir); }
                catch (Exception e) { if (e is ArgumentException || e is NotSupportedException || e is PathTooLongException) throw new BackendException("that is not a valid folder"); throw; }
                if (!Directory.Exists(full)) throw new BackendException("no folder at " + full, 404, "not_found_error");
                r["dir"] = full;
                DirectoryInfo parent = Directory.GetParent(full);
                r["parent"] = parent != null ? parent.FullName : null;
                try
                {
                    List<string> dirs = new List<string>(Directory.EnumerateDirectories(full));
                    List<string> files = new List<string>();
                    foreach (string f in Directory.EnumerateFiles(full)) if (f.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase)) files.Add(f);
                    dirs.Sort(StringComparer.OrdinalIgnoreCase);
                    files.Sort(StringComparer.OrdinalIgnoreCase);
                    foreach (string d in dirs)
                    {
                        if (entries.Count >= MaxEntries) { truncated = true; break; }
                        FileAttributes a;
                        try { a = File.GetAttributes(d); } catch (IOException) { continue; } catch (UnauthorizedAccessException) { continue; }
                        if ((a & (FileAttributes.Hidden | FileAttributes.System)) != 0) continue;
                        entries.Add(Entry(Path.GetFileName(d), d, "dir", -1));
                    }
                    foreach (string f in files)
                    {
                        if (entries.Count >= MaxEntries) { truncated = true; break; }
                        long len = -1;
                        try { len = new FileInfo(f).Length; } catch (IOException) { }
                        entries.Add(Entry(Path.GetFileName(f), f, "gguf", len));
                    }
                }
                catch (UnauthorizedAccessException) { throw new BackendException("Windows does not let Windlass read " + full, 403, "forbidden"); }
            }
            r["entries"] = entries;
            r["truncated"] = truncated;
            List<object> recent = new List<object>();
            foreach (string p in Recent(recentFile))
            {
                long len = -1;
                bool exists = false;
                try { if (File.Exists(p)) { exists = true; len = new FileInfo(p).Length; } } catch (IOException) { }
                Dictionary<string, object> e = Entry(Path.GetFileName(p), p, "gguf", len);
                e["exists"] = exists;
                recent.Add(e);
            }
            r["recent"] = recent;
            return r;
        }

        static Dictionary<string, object> Entry(string name, string path, string type, long bytes)
        {
            Dictionary<string, object> e = new Dictionary<string, object>();
            e["name"] = name; e["path"] = path; e["type"] = type; e["bytes"] = bytes < 0 ? null : (object)bytes;
            return e;
        }

        // Newest first, one path per line, UTF-8 without a BOM (playbooks\recent-models.txt, per computer, not in git).
        public static List<string> Recent(string file)
        {
            List<string> r = new List<string>();
            if (file == null || !File.Exists(file)) return r;
            try
            {
                foreach (string line in File.ReadAllLines(file, new UTF8Encoding(false)))
                {
                    string p = line.Trim();
                    if (p.Length > 0 && r.Count < MaxRecent) r.Add(p);
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            return r;
        }

        public static void Remember(string file, string path)
        {
            if (file == null) return;
            List<string> all = new List<string>();
            all.Add(path);
            foreach (string p in Recent(file)) if (!string.Equals(p, path, StringComparison.OrdinalIgnoreCase) && all.Count < MaxRecent) all.Add(p);
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(file));
                File.WriteAllLines(file, all.ToArray(), new UTF8Encoding(false));
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
