// Keeps docs\CLI-API-PARITY.md in step with the code: every registered command and every API route is named on the
// page. A new command or route without a row on that page fails here. C# 5 only; no model, no server.
using System;
using System.Collections.Generic;
using System.IO;

namespace Windlass.Tests
{
    public static partial class Runner
    {
        static void CliApiParityDocs()
        {
            string root = Path.GetFullPath(Path.Combine(_fixtures, "..", ".."));
            string doc = File.ReadAllText(Path.Combine(root, "docs", "CLI-API-PARITY.md"));

            TextWriter old = Console.Out;
            StringWriter sw = new StringWriter();
            int rc;
            try { Console.SetOut(sw); rc = Program.Run(new string[] { "commands", "--json" }, _temp, null, "0"); }
            finally { Console.SetOut(old); }
            Equal(0, rc, "commands exit code");
            Dictionary<string, object> cmds = (Dictionary<string, object>)Json.Parse(sw.ToString().Trim());
            List<object> list = (List<object>)cmds["commands"];
            Check(list.Count >= 7, "the registry lists the commands");
            foreach (object c in list)
            {
                string name = (string)((Dictionary<string, object>)c)["name"];
                Check(doc.Contains("`" + name + "`") || doc.Contains("`" + name + " "), "docs/CLI-API-PARITY.md does not name the command '" + name + "'");
            }

            string server = File.ReadAllText(Path.Combine(root, "src", "Server.cs"));
            foreach (string route in new string[] { "/v1/chat/completions", "/v1/models", "/health", "/__fleethacker/id", "/__fleethacker/shutdown", "/__fleethacker/heartbeat", "/api/session", "/api/status", "/api/playbook", "/api/logs", "/api/heartbeat", "/api/shutdown" })
            {
                Check(server.Contains("\"" + route), "src/Server.cs no longer routes " + route + "; update this test and the page");
                Check(doc.Contains(route), "docs/CLI-API-PARITY.md does not name the route " + route);
            }
            Check(doc.Contains("Stop-Windlass.ps1"), "the page names the stop script");
        }
    }
}
