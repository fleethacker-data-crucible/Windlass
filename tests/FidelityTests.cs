// Tests for the context-fidelity check (src\Fidelity.cs) and the `windlass` extension on /v1/models. C# 5 only.
// Part of the Runner class (tests\Tests.cs, which registers them), so these use its helpers and temp folder.
// A tiny random model scores about chance on fidelity (for free recall, about zero), so the model tests prove the
// machinery (a score is produced, recorded and shown), not model quality; the scoring and the whole pipeline are
// proved without a model, with hand-made replies and with a backend that answers from the log.
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;

namespace Windlass.Tests
{
    public static partial class Runner
    {
        // A backend with no model: answers from a function, and publishes the facts of a playbook, like LayeredBackend.
        sealed class ScriptedBackend : Backend
        {
            public Func<GenerationRequest, int, string> Answer;
            public Playbook Book;
            public readonly List<GenerationRequest> Calls = new List<GenerationRequest>();
            public ScriptedBackend() { ModelId = "scripted"; }
            public override void Load() { State = BackendState.Ready; }
            public override Dictionary<string, object> Facts() { return Book != null ? Book.ToFacts() : base.Facts(); }
            public override GenerationResult Generate(GenerationRequest request, Action<string> onText, Action<int, int> onStage, CancellationToken cancel)
            {
                Calls.Add(request);
                string text = Answer != null ? Answer(request, Calls.Count - 1) : "";
                if (cancel.IsCancellationRequested) return new GenerationResult("cancelled", 0, 0);
                onText(text);
                return new GenerationResult("stop", 0, 1);
            }
        }

        static FidelityFact Fact(string answer) { return new FidelityFact { Kind = "test", Answer = answer, Question = "?" }; }

        static void FidelityScoring()
        {
            // Exact, case, whitespace and punctuation.
            Check(Fidelity.Recalled("Odile Marrick", "Odile Marrick"), "exact");
            Check(Fidelity.Recalled("  odile   MARRICK. ", "Odile Marrick"), "case, spaces and a full stop");
            Check(Fidelity.Recalled("The relief signed on was Odile Marrick, I believe.", "Odile Marrick"), "inside a sentence");
            Check(Fidelity.Recalled("Odile\nMarrick", "Odile Marrick"), "a line break between the words");
            // Numbers: whole words only, thousands separators ignored.
            Check(Fidelity.Recalled("4172", "4172"), "a number");
            Check(Fidelity.Recalled("It held 4,172 litres.", "4172"), "a thousands separator and a unit");
            Check(!Fidelity.Recalled("41720", "4172"), "a longer number is not the planted one");
            Check(!Fidelity.Recalled("14172", "4172"), "a number that ends with it is not the planted one");
            Check(!Fidelity.Recalled("About 4173 litres", "4172"), "a near number is wrong");
            // Codes with or without their separator.
            Check(Fidelity.Recalled("KX4471", "KX4471"), "a code");
            Check(Fidelity.Recalled("KX-4471", "KX4471"), "a code written with a hyphen");
            Check(Fidelity.Recalled("kx 4471", "KX4471"), "a code written with a space");
            Check(!Fidelity.Recalled("KX4472", "KX4471"), "a wrong code");
            Check(!Fidelity.Recalled("KX", "KX4471"), "half a code");
            // Wrong, partial, missing.
            Check(!Fidelity.Recalled("Tamsin Penhallow", "Odile Marrick"), "a wrong name");
            Check(!Fidelity.Recalled("Odile", "Odile Marrick"), "a first name alone is partial");
            Check(!Fidelity.Recalled("Marrick", "Odile Marrick"), "a last name alone is partial");
            Check(!Fidelity.Recalled("Odile Marric", "Odile Marrick"), "a misspelling is wrong");
            Check(!Fidelity.Recalled("", "Odile Marrick") && !Fidelity.Recalled(null, "Odile Marrick") && !Fidelity.Recalled("   \n", "Odile Marrick"), "empty and missing replies");
            Check(!Fidelity.Recalled("anything", "") && !Fidelity.Recalled("anything", null), "an empty planted value never scores");
            Equal("kx 4471 a b", Fidelity.Normalise("KX-4471, a\tb"), "normalise");

            // Score: replies are matched to facts in order; a short list leaves the rest missing.
            List<FidelityFact> asked = new List<FidelityFact> { Fact("Odile Marrick"), Fact("4172"), Fact("KX4471"), Fact("Skerrowick") };
            FidelityResult all = Fidelity.Score(asked, new string[] { "Odile Marrick", "4,172", "kx-4471", "off Skerrowick" });
            Check(all.Correct == 4 && all.Total == 4 && all.Score == 1.0, "every recall right");
            FidelityResult mixed = Fidelity.Score(asked, new string[] { "Odile", "4172", null, "Skerrowick" });
            Check(mixed.Correct == 2 && mixed.Total == 4 && Math.Abs(mixed.Score - 0.5) < 1e-12, "half right: " + mixed.Correct);
            Check(!mixed.Recalled[0] && mixed.Recalled[1] && !mixed.Recalled[2] && mixed.Recalled[3], "which ones");
            FidelityResult none = Fidelity.Score(asked, new string[] { "no idea", "none" });
            Check(none.Correct == 0 && none.Total == 4 && none.Replies.Count == 4, "a short reply list counts the rest as missing");
            FidelityResult nothing = Fidelity.Score(asked, null);
            Check(nothing.Correct == 0 && nothing.Total == 4, "no replies at all");
            Check(Fidelity.Score(new List<FidelityFact>(), new string[0]).Score == 0, "nothing asked scores 0, not NaN");
        }

        static void FidelityGenerator()
        {
            int[] factCounts = { 1, 2, 4, 7, 13, 40 };
            int[] sizes = { 200, 1200, 5000, 20000 };
            foreach (int facts in factCounts)
                foreach (int chars in sizes)
                    for (int seed = 0; seed < 6; seed++)
                    {
                        FidelityCase c = Fidelity.Generate(seed, facts, chars);
                        string bad = Fidelity.Validate(c);
                        Check(bad == null, "seed " + seed + ", " + facts + " facts, " + chars + " chars: " + bad);
                        Equal(facts, c.Facts.Count, "fact count");
                        HashSet<string> answers = new HashSet<string>();
                        foreach (FidelityFact f in c.Facts) answers.Add(Fidelity.Normalise(f.Answer));
                        Equal(facts, answers.Count, "distinct answers (seed " + seed + ", " + facts + " facts)");
                        foreach (char ch in c.Passage) Check(ch == '\n' || (ch >= ' ' && ch < 127), "the log is plain ASCII");
                    }

            // Deterministic: the same inputs give the same log, questions and answers; another seed differs.
            FidelityCase a = Fidelity.Generate(7, 6, 3000), b = Fidelity.Generate(7, 6, 3000), other = Fidelity.Generate(8, 6, 3000);
            Equal(a.Passage, b.Passage, "same seed, same log");
            for (int i = 0; i < a.Facts.Count; i++)
                Check(a.Facts[i].Answer == b.Facts[i].Answer && a.Facts[i].Question == b.Facts[i].Question, "same seed, same fact " + i);
            Check(a.Passage != other.Passage, "another seed gives another log");
            Equal(Fidelity.Version, a.Version, "the case carries the check's version");

            // Scales with the target: longer targets give longer logs, roughly as long as asked.
            int small = Fidelity.Generate(1, 4, 1000).Passage.Length, mid = Fidelity.Generate(1, 4, 4000).Passage.Length, big = Fidelity.Generate(1, 4, 16000).Passage.Length;
            Check(small < mid && mid < big, "longer targets give longer logs: " + small + ", " + mid + ", " + big);
            Check(mid > 4000 * 0.7 && mid < 4000 * 1.4 && big > 16000 * 0.8 && big < 16000 * 1.25, "close to the target: " + mid + " for 4000, " + big + " for 16000");
            Check(Fidelity.CharsForTokens(1000) > Fidelity.CharsForTokens(300), "a token budget maps to more characters");

            // All four kinds are used once there are four facts; the facts are spread through the log.
            FidelityCase four = Fidelity.Generate(3, 4, 4000);
            HashSet<string> kinds = new HashSet<string>();
            foreach (FidelityFact f in four.Facts) kinds.Add(f.Kind);
            Equal(4, kinds.Count, "four kinds of fact");
            int firstAt = four.Passage.IndexOf(four.Facts[0].Statement, StringComparison.Ordinal), lastAt = four.Passage.IndexOf(four.Facts[3].Statement, StringComparison.Ordinal);
            Check(firstAt > 0 && lastAt - firstAt > four.Passage.Length / 4, "the facts are spread, not bunched: " + firstAt + " to " + lastAt);

            // Prompts: system message, then the whole log and one question.
            List<ChatMessage> p = Fidelity.Prompt(four, four.Facts[2]);
            Equal(2, p.Count, "two messages");
            Check(p[0].Role == "system" && p[1].Role == "user" && p[1].Content.StartsWith(four.Passage) && p[1].Content.EndsWith(four.Facts[2].Question), "log, then the question");
            Check(!p[1].Content.Contains(four.Facts[0].Question) || four.Facts[0].Question == four.Facts[2].Question, "only one question is asked");

            // Refusals.
            foreach (int bad in new int[] { 0, -1, Fidelity.MaxFacts + 1 })
            {
                bool threw = false;
                try { Fidelity.Generate(1, bad, 1000); } catch (ArgumentException) { threw = true; }
                Check(threw, bad + " facts is refused");
            }
            bool tooLong = false;
            try { Fidelity.Generate(1, 4, Fidelity.MaxChars + 1); } catch (ArgumentException) { tooLong = true; }
            Check(tooLong, "a huge log is refused");
        }

        // Finds which fact a request asks about, from its last user message.
        static FidelityFact AskedAbout(FidelityCase c, GenerationRequest r)
        {
            string last = r.Messages[r.Messages.Count - 1].Content;
            foreach (FidelityFact f in c.Facts) if (last.EndsWith(f.Question, StringComparison.Ordinal)) return f;
            return null;
        }

        static void FidelityPipeline()
        {
            FidelityCase c = Fidelity.Generate(5, 6, 2500);
            ScriptedBackend oracle = new ScriptedBackend();
            oracle.Answer = delegate(GenerationRequest r, int n) { FidelityFact f = AskedAbout(c, r); return f == null ? "?" : "It was " + f.Answer + "."; };
            FidelityResult full = Fidelity.Run(oracle, c, 0, CancellationToken.None);
            Check(full.Correct == 6 && full.Total == 6 && full.Score == 1.0, "a backend that reads the log scores 6 of 6: " + full.Correct);
            Equal(6, oracle.Calls.Count, "one request per question");
            foreach (GenerationRequest r in oracle.Calls)
            {
                Equal(0.0, r.Temperature, "greedy");
                Equal(Fidelity.ReplyTokens, r.MaxTokens, "a small max_tokens");
                Check(r.Messages.Count == 2 && r.Messages[1].Content.Contains(c.Passage), "the whole log goes with every question");
            }

            // A backend that forgets every other fact, and one that never knows.
            ScriptedBackend half = new ScriptedBackend();
            half.Answer = delegate(GenerationRequest r, int n) { FidelityFact f = AskedAbout(c, r); return n % 2 == 0 ? f.Answer : "I do not remember."; };
            FidelityResult h = Fidelity.Run(half, c, 0, CancellationToken.None);
            Equal(3, h.Correct, "half recalled");
            ScriptedBackend blank = new ScriptedBackend();
            FidelityResult z = Fidelity.Run(blank, c, 0, CancellationToken.None);
            Check(z.Correct == 0 && z.Total == 6, "no recalls when nothing is answered");

            // K questions out of N facts.
            ScriptedBackend few = new ScriptedBackend();
            few.Answer = oracle.Answer;
            FidelityResult two = Fidelity.Run(few, c, 2, CancellationToken.None);
            Check(two.Total == 2 && two.Correct == 2 && few.Calls.Count == 2, "only two questions asked");

            // Cancel: before the first question, and between questions.
            CancellationTokenSource pre = new CancellationTokenSource();
            pre.Cancel();
            ScriptedBackend idle = new ScriptedBackend();
            bool cancelled = false;
            try { Fidelity.Run(idle, c, 0, pre.Token); } catch (OperationCanceledException) { cancelled = true; }
            Check(cancelled && idle.Calls.Count == 0, "a cancelled check asks nothing");
            CancellationTokenSource mid = new CancellationTokenSource();
            ScriptedBackend stopper = new ScriptedBackend();
            stopper.Answer = delegate(GenerationRequest r, int n) { if (n == 1) mid.Cancel(); return "x"; };
            cancelled = false;
            try { Fidelity.Run(stopper, c, 0, mid.Token); } catch (OperationCanceledException) { cancelled = true; }
            Check(cancelled && stopper.Calls.Count == 2, "cancel during the second question stops the check: " + stopper.Calls.Count);

            // The same on a real (tiny) model: a prompt-sized log, a score in range, replies short, and cancel honoured.
            LayeredBackend real = LoadBackend(new TinySpec { Arch = "llama", Type = GgmlType.Q8_0 });
            try
            {
                FidelityResult r = real.CheckFidelity(2, 450, 0, 3, CancellationToken.None);
                Check(r.Total == 2 && r.Correct >= 0 && r.Correct <= 2, "a score in range");
                Check(r.PromptTokens > 0 && r.PromptTokens <= 450, "the log fits the budget: " + r.PromptTokens + " tokens");
                Check(r.Seed == 3 && r.MaxTokens == Fidelity.ReplyTokens, "seed and reply budget recorded");
                FidelityResult again = real.CheckFidelity(2, 450, 0, 3, CancellationToken.None);
                Equal(r.Correct, again.Correct, "greedy: the same score twice");
                Equal(r.Replies[0], again.Replies[0], "greedy: the same reply twice");
                CancellationTokenSource stop = new CancellationTokenSource();
                stop.Cancel();
                bool threw = false;
                try { real.CheckFidelity(2, 450, 0, 3, stop.Token); } catch (OperationCanceledException) { threw = true; }
                Check(threw, "the real check honours cancel");
                bool tooMany = false;
                try { real.CheckFidelity(40, 300, 0, 3, CancellationToken.None); } catch (ArgumentException) { tooMany = true; }
                Check(tooMany, "more facts than the context holds is refused clearly");
            }
            finally { real.Dispose(); }
        }

        static string RunCli(params string[] args)
        {
            TextWriter old = Console.Out, oldErr = Console.Error;
            StringWriter sw = new StringWriter();
            int rc;
            try { Console.SetOut(sw); Console.SetError(new StringWriter()); rc = Program.Run(args, _temp, null, "0"); }
            finally { Console.SetOut(old); Console.SetError(oldErr); }
            return rc + "\n" + sw;
        }

        static void FidelityCli()
        {
            string model = Model(new TinySpec { Arch = "qwen2", Type = GgmlType.Q8_0 });
            string id = LayeredBackend.DefaultId(model);
            string run = RunCli("playbook", "fidelity", "--model", model, "--facts", "2", "--tokens", "450", "--seed", "4", "--json");
            Check(run.StartsWith("0\n"), "playbook fidelity exit code: " + run);
            Dictionary<string, object> f = Json.Parse(run.Substring(2).Trim()) as Dictionary<string, object>;
            Equal("windlass.fidelity/1", Json.GetStr(f, "schema", ""), "fidelity schema");
            Equal(2.0, Json.GetNum(f, "total", 0), "two facts asked");
            double correct = Json.GetNum(f, "correct", -1);
            Check(correct >= 0 && correct <= 2 && Math.Abs(Json.GetNum(f, "score", -1) - correct / 2) < 1e-9, "a score in range");
            Check(Json.GetNum(f, "promptTokens", 0) > 0 && Json.GetNum(f, "promptTokens", 0) <= 450, "the log fits the budget");
            Equal((double)Fidelity.Version, Json.GetNum(f, "version", 0), "version");
            Equal(4.0, Json.GetNum(f, "seed", 0), "seed");
            List<object> qs = f["questions"] as List<object>;
            Check(qs != null && qs.Count == 2 && ((Dictionary<string, object>)qs[0]).ContainsKey("planted"), "each question, the planted value and the reply are listed");

            // Recorded in the saved playbook next to the perplexity.
            string saved = File.ReadAllText(Playbook.PathFor(_temp, id));
            Dictionary<string, object> pb = Json.Parse(saved) as Dictionary<string, object>;
            Dictionary<string, object> fi = pb["fidelity"] as Dictionary<string, object>;
            Equal(2.0, Json.GetNum(fi, "total", 0), "the playbook records the total");
            Equal(correct, Json.GetNum(fi, "correct", -1), "the playbook records the score");
            Check(Json.GetStr(fi, "measured", null) != null, "and when it was measured");
            Check(saved.Contains("Context fidelity: " + (int)correct + " of 2 facts"), "and a rule says it in words");

            // A later eval keeps the fidelity score; a later fidelity run keeps the perplexity.
            string ev = RunCli("playbook", "eval", "--model", model, "--tokens", "32", "--json");
            Check(ev.StartsWith("0\n"), "eval after fidelity: " + ev);
            pb = Json.Parse(File.ReadAllText(Playbook.PathFor(_temp, id))) as Dictionary<string, object>;
            Equal(2.0, Json.GetNum((Dictionary<string, object>)pb["fidelity"], "total", 0), "eval keeps the fidelity score");
            Check(Json.GetNum((Dictionary<string, object>)pb["quality"], "perplexity", 0) > 1, "and records the perplexity");
            Check(Json.GetStr((Dictionary<string, object>)pb["quality"], "measured", null) != null, "with a time");
            RunCli("playbook", "fidelity", "--model", model, "--facts", "2", "--tokens", "450", "--json");
            pb = Json.Parse(File.ReadAllText(Playbook.PathFor(_temp, id))) as Dictionary<string, object>;
            Check(Json.GetNum((Dictionary<string, object>)pb["quality"], "perplexity", 0) > 1, "fidelity keeps the perplexity");
            RunCli("playbook", "probe", "--model", model, "--json");
            pb = Json.Parse(File.ReadAllText(Playbook.PathFor(_temp, id))) as Dictionary<string, object>;
            Check(Json.GetNum((Dictionary<string, object>)pb["quality"], "perplexity", 0) > 1 && Json.GetNum((Dictionary<string, object>)pb["fidelity"], "total", 0) == 2, "a new probe keeps both scores");

            // playbook show prints it, in the text and in the JSON.
            string show = RunCli("playbook", "show", "--model", model);
            Check(show.StartsWith("0\n") && show.Contains("Context fidelity: " + (int)correct + " of 2 facts planted in a "), "playbook show prints the fidelity score: " + show);
            Check(show.Contains("Quality: perplexity"), "next to the perplexity");
            string showJson = RunCli("playbook", "show", "--model", model, "--json");
            Dictionary<string, object> sj = Json.Parse(showJson.Substring(2).Trim()) as Dictionary<string, object>;
            Equal(2.0, Json.GetNum((Dictionary<string, object>)sj["fidelity"], "total", 0), "playbook show --json has it");

            // Text output, the command list, and bad input.
            string text = RunCli("playbook", "fidelity", "--model", model, "--facts", "2", "--tokens", "450");
            Check(text.StartsWith("0\n") && text.Contains("planted facts recalled") && text.Contains("recorded in"), "text output: " + text);
            Check(RunCli("commands", "--json").Contains("playbook fidelity"), "commands lists playbook fidelity");
            Check(RunCli("playbook", "fidelity", "--model", model, "--facts", "2", "--questions", "3").StartsWith("1\n"), "--questions above --facts is refused");
            Check(RunCli("playbook", "fidelity", "--model", model, "--facts", "0").StartsWith("1\n"), "--facts 0 is refused");
            Check(RunCli("playbook", "fidelity").StartsWith("1\n"), "--model is required");
        }

        // ---------------- /v1/models extension ----------------

        static Dictionary<string, object> WindlassCard(Resp r, bool list)
        {
            return (list ? Path_(r.Json, "data", 0, "windlass") : Path_(r.Json, "windlass")) as Dictionary<string, object>;
        }

        static void ApiModelFacts()
        {
            // Nothing measured: the extension is there, every value is null, and the OpenAI fields are unchanged.
            ServerSettings st = new ServerSettings();
            st.MaxQueue = 3;
            using (Stub(0, st))
            {
                Resp m = Call("GET", "/v1/models", null);
                Equal(200, m.Status, "models status");
                Equal("stub-model", (string)Path_(m.Json, "data", 0, "id"), "id unchanged");
                Equal("model", (string)Path_(m.Json, "data", 0, "object"), "object unchanged");
                Equal(0.0, (double)Path_(m.Json, "data", 0, "created"), "created unchanged");
                Equal("windlass", (string)Path_(m.Json, "data", 0, "owned_by"), "owned_by unchanged");
                Equal(4096.0, (double)Path_(m.Json, "data", 0, "context_length"), "context_length unchanged");
                Equal("ready", (string)Path_(m.Json, "data", 0, "status"), "status unchanged");
                Dictionary<string, object> w = WindlassCard(m, true);
                Check(w != null, "the windlass object is on the model card");
                Equal(ModelFacts.Schema, (string)w["schema"], "schema");
                foreach (string k in new string[] { "architecture", "quant", "layers", "fileBytes", "modelContextLength", "guessAhead" })
                    Check(w.ContainsKey(k) && w[k] == null, k + " is null, not invented");
                Equal(4096.0, (double)w["contextLength"], "the serving context is known");
                foreach (string fig in new string[] { "secPerToken", "secPerPromptToken" })
                {
                    Dictionary<string, object> d = (Dictionary<string, object>)Path_(w, "speed", fig);
                    Check(d["value"] == null && d["predicted"] == null && d["observed"] == null && d["samples"] == null, fig + " has no numbers");
                    Equal(false, (bool)d["measured"], fig + " is not measured");
                    Equal("none", (string)d["source"], fig + " has no source");
                }
                Check(Path_(w, "speed", "limit") == null, "no limiting resource named");
                foreach (string k in new string[] { "perplexity", "tokens", "measuredAt" }) Check(Path_(w, "quality", k) == null, "quality." + k + " is null");
                foreach (string k in new string[] { "score", "correct", "total", "promptTokens", "version", "measuredAt" }) Check(Path_(w, "fidelity", k) == null, "fidelity." + k + " is null");
                Equal(0.0, (double)Path_(w, "queue", "length"), "queue length");
                Equal(3.0, (double)Path_(w, "queue", "max"), "queue max");
                Resp one = Call("GET", "/v1/models/stub-model", null);
                Equal(200, one.Status, "one model");
                Check(WindlassCard(one, false) != null && ((string)WindlassCard(one, false)["schema"]) == ModelFacts.Schema, "the same extension on /v1/models/{id}");
                Equal("stub-model", (string)Path_(one.Json, "id"), "one model's id");
            }

            // Measurements published by a backend show up; the numbers are the playbook's, and unmeasured ones stay null.
            Playbook p = new Playbook();
            Check(Json.Write(p.ToFacts()).Contains("\"source\":\"none\""), "an empty playbook publishes no source");
            p.PredictedSecPerToken = 7.5; p.Limit = "disk";
            p.SetQuality(new QualityResult { Tokens = 256, Perplexity = 5.5, Seconds = 307.2 });
            p.SetFidelity(new FidelityResult { Correct = 3, Total = 4, PromptTokens = 301 });
            p.Observe(100, 120, 5, 40, 0, 0);
            Dictionary<string, object> early = p.ToFacts();
            Dictionary<string, object> tok0 = (Dictionary<string, object>)Path_(early, "speed", "secPerToken");
            Check(tok0["observed"] == null && Math.Abs((double)tok0["value"] - 7.5) < 1e-9 && (string)tok0["source"] == "probe", "5 observed tokens are too few to publish: the prediction stays the value");
            p.Observe(100, 120, 35, 270, 0, 0);
            ScriptedBackend sb = new ScriptedBackend { Book = p };
            ServerSettings st2 = new ServerSettings();
            using (new TestServer(sb, st2))
            {
                Dictionary<string, object> w = WindlassCard(Call("GET", "/v1/models", null), true);
                Dictionary<string, object> tok = (Dictionary<string, object>)Path_(w, "speed", "secPerToken");
                Check(Math.Abs((double)tok["predicted"] - 7.5) < 1e-9 && Math.Abs((double)tok["observed"] - 7.75) < 1e-9, "predicted 7.5 and observed 310/40 = 7.75");
                Check(Math.Abs((double)tok["value"] - 7.75) < 1e-9 && (bool)tok["measured"] && (string)tok["source"] == "real-runs" && (double)tok["samples"] == 40, "real runs win once there are enough");
                Dictionary<string, object> pr = (Dictionary<string, object>)Path_(w, "speed", "secPerPromptToken");
                Check(Math.Abs((double)pr["predicted"] - 307.2 / 256) < 1e-9 && Math.Abs((double)pr["observed"] - 1.2) < 1e-9 && (string)pr["source"] == "real-runs", "prompt speed: the eval's number and the observed one");
                Equal("disk", (string)Path_(w, "speed", "limit"), "the limit the playbook named");
                Equal(5.5, (double)Path_(w, "quality", "perplexity"), "perplexity");
                Equal(256.0, (double)Path_(w, "quality", "tokens"), "eval tokens");
                Check(Path_(w, "quality", "measuredAt") is string && DateTime.Parse((string)Path_(w, "quality", "measuredAt")).Year >= 2026, "when it was measured");
                Equal(0.75, (double)Path_(w, "fidelity", "score"), "fidelity score");
                Check((double)Path_(w, "fidelity", "correct") == 3 && (double)Path_(w, "fidelity", "total") == 4 && (double)Path_(w, "fidelity", "promptTokens") == 301, "fidelity counts");
                Check(w["architecture"] == null && w["guessAhead"] == null, "what this backend did not report stays null");
            }

            // Only eval measured (no probe, no runs): prompt speed is the eval's, token speed stays null.
            Playbook q = new Playbook();
            q.SetQuality(new QualityResult { Tokens = 100, Perplexity = 9, Seconds = 50 });
            Dictionary<string, object> qf = q.ToFacts();
            Check(Path_(qf, "speed", "secPerToken", "value") == null && (string)Path_(qf, "speed", "secPerPromptToken", "source") == "eval", "eval alone: prompt speed from the eval, no token speed");
        }

        static void ApiModelFactsReal()
        {
            TinySpec spec = new TinySpec { Arch = "llama", Type = GgmlType.Q8_0 };
            // Other tests persist quality and fidelity for Model(spec). A probe deliberately
            // preserves those measurements, so this test needs its own model identity.
            string model = Path.Combine(_temp, "api-model-facts.gguf");
            TinyModels.Write(model, spec);
            Equal(0, int.Parse(RunCli("playbook", "probe", "--model", model, "--json").Split('\n')[0]), "probe");
            EngineOptions o = Program.Engine(Program.ParseArgs(new string[] { "run", "--model", model, "--guess" }, 1), _temp);
            Check(o.Playbook != null && o.Playbook.PredictedSecPerToken > 0, "the playbook predicts after a probe");
            LayeredBackend b = new LayeredBackend(o);
            using (TestServer ts = new TestServer(b, new ServerSettings()))
            {
                string id = b.ModelId;
                Dictionary<string, object> w = WindlassCard(Call("GET", "/v1/models/" + id, null), false);
                Equal("llama", (string)w["architecture"], "architecture");
                Equal("Q8_0", (string)w["quant"], "quant: the type holding most bytes");
                Equal((double)b.Config.Layers, (double)w["layers"], "layer count");
                Equal((double)new FileInfo(model).Length, (double)w["fileBytes"], "file bytes");
                Equal((double)b.ContextLength, (double)w["contextLength"], "serving context");
                Equal((double)b.Config.ContextLength, (double)w["modelContextLength"], "the model's own context");
                Equal(true, (bool)w["guessAhead"], "guess-ahead is on (--guess)");
                Dictionary<string, object> tok = (Dictionary<string, object>)Path_(w, "speed", "secPerToken");
                Check((double)tok["predicted"] > 0 && tok["observed"] == null && (string)tok["source"] == "probe" && (bool)tok["measured"], "after a probe: predicted, nothing observed yet");
                Check(Path_(w, "speed", "limit") is string, "the limit comes from the probe");
                Check(Path_(w, "quality", "perplexity") == null && Path_(w, "fidelity", "score") == null, "no eval or fidelity yet: null");

                // Measure on the live backend the way the CLI does, then ask again.
                QualityResult qr = b.Evaluate(32, CancellationToken.None);
                o.Playbook.SetQuality(qr);
                FidelityResult fr = b.CheckFidelity(2, 450, 0, 1, CancellationToken.None);
                o.Playbook.SetFidelity(fr);
                GenerationResult res;
                Generate(b, Req(24, "user", "one two one two one two"), out res);
                Generate(b, Req(24, "user", "hello there"), out res);
                w = WindlassCard(Call("GET", "/v1/models", null), true);
                Check(Math.Abs((double)Path_(w, "quality", "perplexity") - qr.Perplexity) < 1e-9 && (double)Path_(w, "quality", "tokens") == 32, "perplexity and its token count");
                Check(Path_(w, "quality", "measuredAt") is string, "and when");
                Check((double)Path_(w, "fidelity", "total") == 2 && (double)Path_(w, "fidelity", "promptTokens") == fr.PromptTokens, "fidelity from the check");
                Check(Math.Abs((double)Path_(w, "fidelity", "score") - fr.Score) < 1e-9, "fidelity score");
                tok = (Dictionary<string, object>)Path_(w, "speed", "secPerToken");
                Check(tok["observed"] != null && (double)tok["observed"] > 0 && (string)tok["source"] == "real-runs", "observed seconds per token after real runs: " + Json.Write(tok));
                Dictionary<string, object> pr = (Dictionary<string, object>)Path_(w, "speed", "secPerPromptToken");
                Check(pr["observed"] != null && (double)pr["observed"] > 0 && (double)pr["predicted"] > 0, "prompt speed observed and from the eval");

                // The engine lock is not needed to answer: hold it on this thread and ask from another.
                FieldInfo lockField = typeof(LayeredBackend).GetField("_run", BindingFlags.NonPublic | BindingFlags.Instance);
                object gate = lockField.GetValue(b);
                Dictionary<string, object> got = null;
                Resp over = null;
                lock (gate)
                {
                    Thread t = new Thread(delegate() { got = b.Facts(); over = Call("GET", "/v1/models/" + id, null); });
                    t.Start();
                    Check(t.Join(5000), "Facts() and /v1/models answer while the engine lock is held");
                }
                Check(got != null && over != null && over.Status == 200 && WindlassCard(over, false) != null, "and they answer correctly");
            }
        }
    }
}
