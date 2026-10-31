// Tests for --batch (batching of queued requests): src\Batch.cs, src\BatchEngine.cs, Transformer.ForwardBatch.
// Tiny random models only. Part of Runner (see Tests.cs) so it can use that file's helpers. C# 5 only.
// Not parity-checked against llama.cpp (it has no batched mode to compare with): the reference here is Windlass's own
// single-request path, which the llama.cpp tests cover, so "batched equals alone" is the property under test.
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace Windlass.Tests
{
    public static partial class Runner
    {
        static int BatchTok(int seq, int step) { return TinyModels.ProbeTokens[(seq * 5 + step * 3 + 1) % TinyModels.ProbeTokens.Length]; }

        static bool SameBits(float[] want, long wantAt, float[] got, long gotAt, int n)
        {
            for (int i = 0; i < n; i++) if (want[wantAt + i] != got[gotAt + i]) return false;
            return true;
        }

        // ForwardBatch of N sequences gives the very same logits as N separate Forward runs (bit for bit: the matrices
        // use the single-token kernels for every row, and every other step is per row), with sequences of different
        // lengths, one joining late, one leaving early, and a step with a single sequence.
        static void BatchForwardMatchesSingle()
        {
            foreach (TinySpec s in new TinySpec[] {
                new TinySpec { Arch = "llama", Type = GgmlType.Q8_0, RopeFreqs = true },
                new TinySpec { Arch = "qwen2", Type = GgmlType.Q6_K, Tied = true },
                new TinySpec { Arch = "qwen3", Type = GgmlType.Q4_K },
                new TinySpec { Arch = "llama", Type = GgmlType.Q3_K } })     // Q3_K has no fused kernel: the decode-then-dot path
            {
                string p = Model(s);
                GgufModelFile g = GgufModelFile.Open(p);
                ModelConfig c = ModelConfig.From(g);
                int[] lens = { 3, 7, 12 }, joinAt = { 0, 0, 2 }, leaveAt = { 9, 5, 8 };
                const int Rounds = 9;
                int[][] prompts = new int[3][];
                for (int i = 0; i < 3; i++)
                {
                    prompts[i] = new int[lens[i]];
                    for (int j = 0; j < lens[i]; j++) prompts[i][j] = BatchTok(i + 3, j);
                }
                float[][][] want = new float[3][][];
                for (int i = 0; i < 3; i++)
                {
                    want[i] = new float[Rounds][];
                    using (LayerStream st = new LayerStream(g, c, true, 0))
                    {
                        Transformer tr = new Transformer(g, c, st, 512);
                        tr.Forward(prompts[i], 0, false, CancellationToken.None, null);
                        for (int k = 0; k < Rounds - joinAt[i]; k++)
                            want[i][k] = tr.Forward(new int[] { BatchTok(i, k) }, prompts[i].Length + k, false, CancellationToken.None, null);
                    }
                }
                using (LayerStream st = new LayerStream(g, c, true, 0))
                {
                    Transformer tr = new Transformer(g, c, st, 512);
                    Sequence[] seq = new Sequence[3];
                    int[] done = new int[3];
                    int rows = 0;
                    for (int r = 0; r < Rounds; r++)
                    {
                        List<int> on = new List<int>();
                        for (int i = 0; i < 3; i++)
                        {
                            if (r == joinAt[i]) { seq[i] = tr.NewSequence(); tr.Forward(seq[i], prompts[i], 0, false, CancellationToken.None, null); }
                            if (r >= joinAt[i] && r < leaveAt[i]) on.Add(i);
                        }
                        Sequence[] ss = new Sequence[on.Count];
                        int[] tk = new int[on.Count];
                        for (int a = 0; a < on.Count; a++) { ss[a] = seq[on[a]]; tk[a] = BatchTok(on[a], done[on[a]]); }
                        float[] got = tr.ForwardBatch(ss, tk, CancellationToken.None, null);
                        int vocab = got.Length / on.Count;
                        for (int a = 0; a < on.Count; a++)
                        {
                            int i = on[a];
                            Check(SameBits(want[i][done[i]], 0, got, (long)a * vocab, vocab), s.Key + ": round " + r + ", sequence " + i + " (" + on.Count + " in the batch) differs from running alone");
                            Equal(prompts[i].Length + done[i] + 1, seq[i].Length, s.Key + ": sequence " + i + " length");
                            done[i]++;
                            rows++;
                        }
                    }
                    Check(rows > 12, "the rounds covered batches of 3, 2 and 1 sequences");
                    Equal(0, tr.Length, "Forward's own cache is separate from the slots");
                }
            }
        }

        // The proof of the mechanism, not a speed claim: N sequences stepped together read the model's bytes about once
        // per step; stepped one by one they read them N times. Real-world throughput is unmeasured.
        static void BatchReadsModelOncePerStep()
        {
            string p = Model(new TinySpec { Arch = "qwen2", Type = GgmlType.Q8_0 });
            GgufModelFile g = GgufModelFile.Open(p);
            ModelConfig c = ModelConfig.From(g);
            const int N = 3, Steps = 20;
            using (LayerStream st = new LayerStream(g, c, true, 0))
            {
                Transformer tr = new Transformer(g, c, st, 512);
                // One pass reads every stage once: measured on a lone sequence.
                tr.Forward(new int[] { 1, 10, 50 }, 0, false, CancellationToken.None, null);
                long b0 = st.BytesRead;
                for (int k = 0; k < Steps; k++) tr.Forward(new int[] { BatchTok(0, k) }, 3 + k, false, CancellationToken.None, null);
                double pass = (st.BytesRead - b0) / (double)Steps;
                Check(pass > 0, "a pass reads bytes");

                Sequence[] seq = new Sequence[N];
                for (int i = 0; i < N; i++)
                {
                    seq[i] = tr.NewSequence();
                    tr.Forward(seq[i], new int[] { 1, 10, 50, 100 + i }, 0, false, CancellationToken.None, null);
                }
                long b1 = st.BytesRead;
                for (int k = 0; k < Steps; k++)
                {
                    int[] tk = new int[N];
                    for (int i = 0; i < N; i++) tk[i] = BatchTok(i, k);
                    tr.ForwardBatch(seq, tk, CancellationToken.None, null);
                }
                double together = (st.BytesRead - b1) / pass;

                Sequence[] alone = new Sequence[N];
                for (int i = 0; i < N; i++)
                {
                    alone[i] = tr.NewSequence();
                    tr.Forward(alone[i], new int[] { 1, 10, 50, 100 + i }, 0, false, CancellationToken.None, null);
                }
                long b2 = st.BytesRead;
                for (int k = 0; k < Steps; k++)
                    for (int i = 0; i < N; i++)
                        tr.Forward(alone[i], new int[] { BatchTok(i, k) }, 4 + k, false, CancellationToken.None, null);
                double apart = (st.BytesRead - b2) / pass;

                // A prefetch still in flight at the moment of counting can shift a reading by up to one stage (a third of a pass here).
                Check(Math.Abs(together - Steps) < 2, N + " sequences x " + Steps + " steps together read " + together.ToString("F1") + " passes' worth of bytes, expected about " + Steps);
                Check(Math.Abs(apart - N * Steps) < 2, "the same one by one read " + apart.ToString("F1") + " passes' worth, expected about " + N * Steps);
                Check(apart > 2.5 * together, "together reads under 40% of the bytes of one by one (" + together.ToString("F1") + " vs " + apart.ToString("F1") + ")");
            }
        }

        static GenerationRequest SeededReq(int maxTokens, string text, int seed)
        {
            GenerationRequest r = Req(maxTokens, "user", text);
            r.Temperature = 1.0; r.TopP = 0.9; r.Seed = seed;
            return r;
        }

        static string[] RunAll(params Func<string>[] work)
        {
            string[] outp = new string[work.Length];
            Exception[] errors = new Exception[work.Length];
            Thread[] ts = new Thread[work.Length];
            for (int i = 0; i < work.Length; i++)
            {
                int k = i;
                ts[i] = new Thread(delegate() { try { outp[k] = work[k](); } catch (Exception e) { errors[k] = e; } });
                ts[i].Start();
            }
            foreach (Thread t in ts) t.Join();
            foreach (Exception e in errors) if (e != null) throw new TestFailure("a concurrent request failed: " + e.Message);
            return outp;
        }

        static string Describe(GenerationResult r) { return r.FinishReason + "/" + r.PromptTokens + "/" + r.CompletionTokens; }

        // Direct calls: greedy, seeded (with top_p), stop sequence and max_tokens, run at the same time on --batch slots, give
        // the text, finish reason and token counts they give one at a time. A follow-up turn reuses its slot's cached prefix.
        static void BatchedBackendMatchesSingle()
        {
            foreach (TinySpec s in new TinySpec[] { new TinySpec { Arch = "qwen2", Type = GgmlType.Q8_0 }, new TinySpec { Arch = "llama", Type = GgmlType.Q4_K, Spm = true } })
            {
                LayeredBackend one = new LayeredBackend(new EngineOptions { ModelPath = Model(s) });
                one.Load();
                GenerationResult res;
                string full = Generate(one, Req(16, "user", "Hello"), out res);
                Check(full.Length >= 4, "need a few characters to cut at");
                string stop = full.Substring(2, 2);
                GenerationRequest[] reqs = new GenerationRequest[]
                {
                    Req(40, "user", "Hello world"),
                    SeededReq(40, "the anchor chain", 11),
                    Req(16, "user", "Hello"),
                    Req(3, "user", "in the world"),
                    SeededReq(30, "Hello", 5)
                };
                reqs[2].Stop.Add(stop);
                string[] text = new string[reqs.Length], meta = new string[reqs.Length];
                for (int i = 0; i < reqs.Length; i++) { text[i] = Generate(one, reqs[i], out res); meta[i] = Describe(res); }
                Equal("stop", meta[2].Split('/')[0], "the stop sequence ended request 2");
                Equal("length", meta[3].Split('/')[0], "max_tokens ended request 3");
                string turn2 = Generate(one, Req(8, "user", "Hello world", "assistant", text[0], "user", "the world"), out res);
                string turn2Meta = Describe(res);
                one.Dispose();

                EngineOptions o = new EngineOptions { ModelPath = Model(s), Batch = 3 };
                LayeredBackend b = new LayeredBackend(o);
                b.Load();
                Equal(3, b.MaxConcurrent, "batch 3 allows 3 at once");
                // Alone in a slot first: the follow-up turn must find and reuse that slot's prefix.
                Equal(text[0], Generate(b, reqs[0], out res), s.Key + ": request 0 alone in a batch backend");
                Equal(meta[0], Describe(res), s.Key + ": request 0 result alone");
                long reusedBefore = b.BatchReusedTokens;
                Equal(turn2, Generate(b, Req(8, "user", "Hello world", "assistant", text[0], "user", "the world"), out res), s.Key + ": follow-up turn after prefix reuse");
                Equal(turn2Meta, Describe(res), s.Key + ": follow-up result");
                Check(b.BatchReusedTokens > reusedBefore, s.Key + ": the follow-up reused its slot's cached prompt prefix");

                // Five requests at once on three slots: two wait for a slot.
                string[] got = new string[reqs.Length], gotMeta = new string[reqs.Length];
                List<Func<string>> work = new List<Func<string>>();
                for (int i = 0; i < reqs.Length; i++)
                {
                    int k = i;
                    work.Add(delegate() { GenerationResult r; got[k] = Generate(b, reqs[k], out r); gotMeta[k] = Describe(r); return got[k]; });
                }
                RunAll(work.ToArray());
                for (int i = 0; i < reqs.Length; i++)
                {
                    Equal(text[i], got[i], s.Key + ": request " + i + " batched with others");
                    Equal(meta[i], gotMeta[i], s.Key + ": request " + i + " result batched with others");
                }
                Check(b.BatchMaxRows >= 2, s.Key + ": requests really shared a step (most in one step: " + b.BatchMaxRows + ")");
                Check(b.BatchMaxRows <= 3, s.Key + ": never more rows than slots");
                b.Dispose();
            }

            // Batch 1 keeps the old path: one at a time, no batch loop, no batch counters.
            LayeredBackend plain = new LayeredBackend(new EngineOptions { ModelPath = Model(new TinySpec { Arch = "qwen2", Type = GgmlType.Q8_0 }) });
            plain.Load();
            GenerationResult pr;
            Generate(plain, Req(5, "user", "Hello"), out pr);
            Equal(1, plain.MaxConcurrent, "batch 1 runs one at a time");
            Equal(0, plain.BatchSteps, "batch 1 never takes a batch step");
            plain.Dispose();
        }

        static string ChatBody(string content, int maxTokens, bool greedy, bool stream)
        {
            return "{\"model\":\"tiny\",\"messages\":[{\"role\":\"user\",\"content\":\"" + content + "\"}],\"max_tokens\":" + maxTokens +
                (greedy ? ",\"temperature\":0" : ",\"temperature\":1.0,\"top_p\":0.9,\"seed\":7") +
                (stream ? ",\"stream\":true,\"stream_options\":{\"include_usage\":true}" : "") + "}";
        }

        static string PlainReply(string body, out int completion)
        {
            Resp r = Call("POST", "/v1/chat/completions", body);
            Equal(200, r.Status, "status (" + r.Body + ")");
            completion = (int)(double)Path_(r.Json, "usage", "completion_tokens");
            return (string)Path_(r.Json, "choices", 0, "message", "content");
        }

        static string StreamReply(string body, out int completion)
        {
            using (HttpWebResponse res = OpenStream(body))
            {
                List<string> ev = ReadSse(res.GetResponseStream(), 100000);
                Equal(": accepted", ev[0], "first comment");
                Equal("data: [DONE]", ev[ev.Count - 1], "ends with [DONE]");
                StringBuilder text = new StringBuilder();
                completion = -1;
                foreach (string e in ev)
                {
                    if (!e.StartsWith("data: {")) continue;
                    Dictionary<string, object> c = (Dictionary<string, object>)Json.Parse(e.Substring(6));
                    List<object> choices = (List<object>)c["choices"];
                    if (choices.Count == 0) { completion = (int)(double)((Dictionary<string, object>)c["usage"])["completion_tokens"]; continue; }
                    Dictionary<string, object> delta = (Dictionary<string, object>)Path_(c, "choices", 0, "delta");
                    if (delta.ContainsKey("content")) text.Append((string)delta["content"]);
                }
                return text.ToString();
            }
        }

        // Opens a streaming request on a raw socket and drops it once the first words arrive.
        static void StreamThenHangUp(string body)
        {
            using (TcpClient c = new TcpClient("127.0.0.1", TestPort))
            {
                byte[] req = Encoding.UTF8.GetBytes("POST /v1/chat/completions HTTP/1.1\r\nHost: 127.0.0.1:" + TestPort +
                    "\r\nContent-Type: application/json\r\nContent-Length: " + Encoding.UTF8.GetByteCount(body) + "\r\n\r\n" + body);
                NetworkStream ns = c.GetStream();
                ns.Write(req, 0, req.Length);
                byte[] buf = new byte[16384];
                int got = 0;
                System.Diagnostics.Stopwatch sw = System.Diagnostics.Stopwatch.StartNew();
                while (sw.ElapsedMilliseconds < 8000 && got < buf.Length)
                {
                    got += ns.Read(buf, got, buf.Length - got);
                    if (Regex.IsMatch(Encoding.UTF8.GetString(buf, 0, got), "\"content\":\"[^\"]")) break;
                }
                Check(Regex.IsMatch(Encoding.UTF8.GetString(buf, 0, got), "\"content\":\"[^\"]"), "the doomed request got its first words");
                c.Client.LingerState = new LingerOption(true, 0);
            }
        }

        // Through the real server with --batch: two and three requests at once give the replies they give one at a time,
        // a stream is still a stream, and a client that hangs up does not disturb the others.
        static void BatchApiConcurrency()
        {
            TinySpec spec = new TinySpec { Arch = "qwen2", Type = GgmlType.Q8_0 };
            string[] prompts = { "Hello world", "the anchor chain is heavy", "in the world is a thing" };
            string[] want = new string[3];
            int[] wantTokens = new int[3];
            EngineOptions single = new EngineOptions { ModelPath = Model(spec), ModelId = "tiny" };
            using (new TestServer(new LayeredBackend(single), new ServerSettings()))
            {
                for (int i = 0; i < 3; i++) want[i] = PlainReply(ChatBody(prompts[i], 120, i != 1, false), out wantTokens[i]);
            }
            Check(want[0].Length > 0 && want[1].Length > 0 && want[2].Length > 0, "baseline replies exist");
            Check(want[0] != want[1] && want[1] != want[2], "the three prompts give different replies");

            LayeredBackend backend = new LayeredBackend(new EngineOptions { ModelPath = Model(spec), ModelId = "tiny", Batch = 3 });
            using (new TestServer(backend, new ServerSettings { KeepaliveMs = 100 }))
            {
                // Two at once.
                string[] two = RunAll(
                    delegate() { int n; string t = PlainReply(ChatBody(prompts[0], 120, true, false), out n); Equal(wantTokens[0], n, "usage"); return t; },
                    delegate() { int n; string t = PlainReply(ChatBody(prompts[1], 120, false, false), out n); Equal(wantTokens[1], n, "usage"); return t; });
                Equal(want[0], two[0], "greedy request alongside another");
                Equal(want[1], two[1], "seeded request alongside another");

                // Three at once, one of them streamed.
                string[] three = RunAll(
                    delegate() { int n; return PlainReply(ChatBody(prompts[0], 120, true, false), out n); },
                    delegate() { int n; string t = StreamReply(ChatBody(prompts[1], 120, false, true), out n); Equal(wantTokens[1], n, "streamed usage"); return t; },
                    delegate() { int n; return PlainReply(ChatBody(prompts[2], 120, true, false), out n); });
                Equal(want[0], three[0], "greedy request in a batch of three");
                Equal(want[1], three[1], "streamed seeded request in a batch of three");
                Equal(want[2], three[2], "second greedy request in a batch of three");

                // Three at once, one hangs up after its first words.
                string[] cut = RunAll(
                    delegate() { int n; return PlainReply(ChatBody(prompts[0], 120, true, false), out n); },
                    delegate() { int n; return StreamReply(ChatBody(prompts[1], 120, false, true), out n); },
                    delegate() { StreamThenHangUp(ChatBody(prompts[2], 400, true, true)); return ""; });
                Equal(want[0], cut[0], "a request whose neighbour hung up");
                Equal(want[1], cut[1], "a stream whose neighbour hung up");

                bool idle = false;
                for (int i = 0; i < 100 && !idle; i++)
                {
                    idle = !(bool)Path_(Call("GET", "/health", null).Json, "busy");
                    if (!idle) Thread.Sleep(50);
                }
                Check(idle, "the turns of all three were released, including the cancelled one");
                int n2;
                Equal(want[2], PlainReply(ChatBody(prompts[2], 120, true, false), out n2), "the cancelled request's slot is usable again");
                Check(backend.BatchMaxRows >= 2, "requests really shared a step (most in one step: " + backend.BatchMaxRows + ")");
            }
        }

        // Three real requests at once read far fewer bytes than the same three one after another.
        static void BatchRequestsShareReads()
        {
            TinySpec spec = new TinySpec { Arch = "llama", Type = GgmlType.Q8_0 };
            string[] prompts = { "Hello world", "the anchor chain is heavy", "in the world is a thing" };
            LayeredBackend one = new LayeredBackend(new EngineOptions { ModelPath = Model(spec), Prefetch = false });
            one.Load();
            GenerationResult res;
            long b0 = one.Model.Stream.BytesRead;
            for (int i = 0; i < 3; i++) Generate(one, Req(150, "user", prompts[i]), out res);
            long sequential = one.Model.Stream.BytesRead - b0;
            one.Dispose();

            LayeredBackend b = new LayeredBackend(new EngineOptions { ModelPath = Model(spec), Prefetch = false, Batch = 3 });
            b.Load();
            long c0 = b.Model.Stream.BytesRead;
            List<Func<string>> work = new List<Func<string>>();
            for (int i = 0; i < 3; i++)
            {
                int k = i;
                work.Add(delegate() { GenerationResult r; return Generate(b, Req(150, "user", prompts[k]), out r); });
            }
            RunAll(work.ToArray());
            long batched = b.Model.Stream.BytesRead - c0;
            int maxRows = b.BatchMaxRows, steps = b.BatchSteps;
            b.Dispose();
            Check(maxRows == 3, "all three shared steps (most in one step: " + maxRows + ")");
            Check(batched < 0.6 * sequential, "batched read " + batched + " bytes, one after another " + sequential + " (ratio " + (batched / (double)sequential).ToString("F2") + ", " + steps + " batch steps)");
        }

        // --batch is refused when the KV caches cannot fit, validated on the command line, and the server grants N turns.
        static void BatchLimits()
        {
            string path = Model(new TinySpec { Arch = "qwen2", Type = GgmlType.Q8_0 });
            GgufModelFile g = GgufModelFile.Open(path);
            ModelConfig c = ModelConfig.From(g);
            MachineFacts big = MachineFacts.FromJson(null), tiny = MachineFacts.FromJson(null), mid = MachineFacts.FromJson(null);
            big.TotalRam = big.AvailableRam = 64000000000L;
            tiny.TotalRam = tiny.AvailableRam = 4000500000L;        // 4 GB reserve leaves 0.5 MB: a quarter is 125 KB
            mid.TotalRam = 4000000000L; mid.AvailableRam = 4050000000L; // leaves 50 MB: a quarter is 12.5 MB
            Equal(1024L, BatchPlan.KvBytes(c, 1, 1), "tiny model: 1 KB of KV per token");
            Check(BatchPlan.Check(c, 512, 4, big) == null, "plenty of RAM: fits");
            Check(BatchPlan.Check(c, 512, 1, tiny) == null, "batch 1 is never checked");
            string why = BatchPlan.Check(c, 512, 4, tiny);
            Check(why != null && why.Contains("--batch 4") && why.Contains("--context-length"), "too little RAM is refused with advice: " + why);
            Equal(2048, BatchPlan.FitContext(c, 8192, 4, mid, 2048), "context halves until the caches fit");
            Equal(8192, BatchPlan.FitContext(c, 8192, 4, tiny, 2048), "nothing fits: unchanged (Check then refuses)");

            try
            {
                new LayeredBackend(new EngineOptions { ModelPath = path, Batch = 4, Machine = tiny }).Load();
                throw new TestFailure("Load should refuse --batch 4 on a computer with no room");
            }
            catch (ArgumentException e) { Check(e.Message.Contains("--batch 4"), "the refusal names --batch: " + e.Message); }
            LayeredBackend ok = new LayeredBackend(new EngineOptions { ModelPath = path, Batch = 4, Machine = big });
            ok.Load();
            Equal(4, ok.MaxConcurrent, "batch 4 allows 4 at once");
            ok.Dispose();

            Equal(1, Program.Engine(Program.ParseArgs(new string[] { "--model", path }, 0), null).Batch, "the default is one at a time");
            Equal(3, Program.Engine(Program.ParseArgs(new string[] { "--model", path, "--batch", "3" }, 0), null).Batch, "--batch 3");
            foreach (string bad in new string[] { "0", "65", "x" })
            {
                try { Program.Engine(Program.ParseArgs(new string[] { "--model", path, "--batch", bad }, 0), null); throw new TestFailure("--batch " + bad + " should be refused"); }
                catch (ArgumentException e) { Check(e.Message.Contains("--batch"), "usage error names the option: " + e.Message); }
            }
            Check(!Program.Engine(Program.ParseArgs(new string[] { "--model", path, "--batch", "2", "--guess" }, 0), null).Speculate, "guess-ahead is off with --batch");

            Scheduler sch = new Scheduler(1, 2);
            int pos;
            Scheduler.Ticket a = sch.Reserve("a", out pos), b = sch.Reserve("b", out pos), q = sch.Reserve("c", out pos);
            Check(a.Granted && b.Granted && !q.Granted && pos == 1, "two turns granted, the third waits");
            try { sch.Reserve("d", out pos); throw new TestFailure("a full queue should refuse"); }
            catch (BackendException e) { Equal(503, e.Status, "queue full is 503"); }
            sch.Release();
            Check(q.Granted, "a finished request hands its turn to the one waiting");
            sch.Release(); sch.Release();
            Check(!sch.Busy, "idle after all finish");
            Check(new Scheduler(3).Reserve("x", out pos).Granted, "the one-argument scheduler is the old one-at-a-time scheduler");
        }
    }
}
