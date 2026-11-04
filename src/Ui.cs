// The browser UI's files and session token. C# 5 only, ASCII only.
// The UI is built off the workstation (ui-src, Svelte 5 + Tailwind 4) into ui\dist and shipped as compiled files. Windlass
// serves only files it found there when it started, by an exact URL match, so no request text ever becomes a file path.
// The listener itself stays in Server.cs (the locality check lets only that file open one).
using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace Windlass
{
    public sealed class UiAssets
    {
        // Scripts, styles, fonts and images from this origin only. Nothing inline, nothing remote, no framing.
        public const string Csp = "default-src 'none'; script-src 'self'; style-src 'self'; img-src 'self'; font-src 'self'; connect-src 'self'; manifest-src 'self'; base-uri 'none'; form-action 'none'; frame-ancestors 'none'";

        readonly Dictionary<string, string> _files = new Dictionary<string, string>(StringComparer.Ordinal);

        // root: the ui\dist folder. Files with other extensions than the ones below are never served.
        public UiAssets(string root)
        {
            Root = root;
            if (root == null || !Directory.Exists(root)) return;
            string full = Path.GetFullPath(root);
            foreach (string f in Directory.GetFiles(full, "*", SearchOption.AllDirectories))
            {
                if (ContentType(f) == null) continue;
                string rel = f.Substring(full.Length).TrimStart('\\', '/').Replace('\\', '/');
                _files["/" + rel] = f;
            }
        }

        public readonly string Root;

        public bool Available { get { return _files.ContainsKey("/index.html"); } }

        public int Count { get { return _files.Count; } }

        // urlPath is the request's absolute path ("/" is the page). False when it names no served file.
        public bool TryGet(string urlPath, out byte[] data, out string contentType)
        {
            data = null; contentType = null;
            if (urlPath == "/") urlPath = "/index.html";
            string file;
            if (!_files.TryGetValue(urlPath, out file)) return false;
            try { data = File.ReadAllBytes(file); }
            catch (IOException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
            contentType = ContentType(file);
            return true;
        }

        public static string ContentType(string file)
        {
            switch (Path.GetExtension(file).ToLowerInvariant())
            {
                case ".html": return "text/html; charset=utf-8";
                case ".js": case ".mjs": return "text/javascript; charset=utf-8";
                case ".css": return "text/css; charset=utf-8";
                case ".json": case ".webmanifest": return "application/json; charset=utf-8";
                case ".svg": return "image/svg+xml";
                case ".png": return "image/png";
                case ".ico": return "image/x-icon";
                case ".woff2": return "font/woff2";
                case ".txt": return "text/plain; charset=utf-8";
                default: return null;
            }
        }
    }

    public static class UiToken
    {
        // 24 random bytes as hex. Handed out by POST /api/session (which needs the API key when one is set) and kept in the
        // page's memory only; the leaving signal on page close carries it in the body because a beacon cannot set headers.
        public static string New()
        {
            byte[] b = new byte[24];
            using (RandomNumberGenerator rng = RandomNumberGenerator.Create()) rng.GetBytes(b);
            StringBuilder sb = new StringBuilder();
            foreach (byte x in b) sb.Append(x.ToString("x2"));
            return sb.ToString();
        }
    }
}
