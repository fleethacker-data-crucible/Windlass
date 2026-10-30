// Batched generation for LayeredBackend (--batch N): up to N requests generate together. C# 5 only.
// One batch loop thread owns the model. A request thread (Backend.Generate) encodes its prompt, hands a job to the loop and
// blocks until the job is done; text reaches its onText callback from the loop thread as it is produced. Each loop
// round admits waiting jobs into free slots (their prompt runs as its own pass, on the job's own KV cache), then takes ONE
// decode step for every active job: Transformer.ForwardBatch stacks one token from each into a single pass over the
// layers, so the model file is read once per step for all of them. A job's tokens, sampling (own Random), stop scanner,
// max_tokens and cancel token are its own, so its text is the same as when it runs alone (the matrices use the
// single-token kernels for every row; see Transformer.EachToken). Guess-ahead is not used in a batch.
// With --batch 1 (the default) none of this runs: Generate takes the original path in Engine.cs.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Threading;

namespace Windlass
{
    public sealed partial class LayeredBackend
    {
        sealed class BatchJob
        {
            public GenerationRequest Req;
            public Action<string> OnText;
            public Action<int, int> OnStage;
            public CancellationToken Cancel;
            public List<int> Ids;
            public int MaxTokens, Produced, NextId;
            public Random Rng;
            public Decoder Utf8;
            public StopScanner Scanner;
            public bool First = true;
            public Sequence Slot;
            public readonly ManualResetEvent Done = new ManualResetEvent(false);
            public GenerationResult Result;
            public Exception Error;
            public int Common;
            public Stopwatch Clock;
        }

        Sequence[] _slots;                                       // one KV cache per concurrent request (Batch > 1)
        readonly object _mail = new object();
        readonly List<BatchJob> _incoming = new List<BatchJob>();
        readonly object _encode = new object();                  // the tokenizer keeps a cache and is not thread safe
        Thread _loop;
        bool _stop;
        CancellationTokenSource _loopCts;

        // Counters for tests and logs (written by the loop thread only).
        public int BatchSteps, BatchMaxRows;
        public long BatchRowTotal, BatchReusedTokens;

        public override int MaxConcurrent { get { return _o.Batch > 1 ? _o.Batch : 1; } }

        // RAM the slots can grow to, for the load log.
        public long BatchKvBytes { get { return BatchPlan.KvBytes(Config, ContextLength, _o.Batch); } }

        void StartBatching()
        {
            _slots = new Sequence[_o.Batch];
            for (int i = 0; i < _slots.Length; i++) _slots[i] = Model.NewSequence();
            _loopCts = new CancellationTokenSource();
            _loop = new Thread(BatchLoop) { IsBackground = true, Name = "windlass-batch" };
            _loop.Start();
        }

        void StopBatching()
        {
            if (_loop == null) return;
            lock (_mail) { _stop = true; Monitor.PulseAll(_mail); }
            _loopCts.Cancel();
            _loop.Join(10000);
            _loop = null;
        }

        GenerationResult GenerateBatched(GenerationRequest req, Action<string> onText, Action<int, int> onStage, CancellationToken cancel)
        {
            List<int> ids;
            lock (_encode) ids = EncodePrompt(req.Messages);
            if (ids.Count >= ContextLength)
                throw new BackendException("The prompt is " + ids.Count + " tokens; this server's context is " + ContextLength + " tokens.", 400, "context_length_exceeded");
            BatchJob j = new BatchJob();
            j.Req = req; j.OnText = onText; j.OnStage = onStage; j.Cancel = cancel; j.Ids = ids;
            j.MaxTokens = Math.Min(req.MaxTokens, ContextLength - ids.Count);
            j.Rng = req.Seed.HasValue ? new Random(req.Seed.Value) : new Random();
            j.Utf8 = new UTF8Encoding(false).GetDecoder();
            j.Scanner = new StopScanner(req.Stop);
            j.Clock = Stopwatch.StartNew();
            lock (_mail)
            {
                if (_stop || _loop == null) throw new BackendException("Windlass is shutting down", 503, "server_busy");
                _incoming.Add(j);
                Monitor.PulseAll(_mail);
            }
            j.Done.WaitOne();
            if (j.Error != null) throw j.Error;
            return j.Result;
        }

        void BatchLoop()
        {
            List<BatchJob> active = new List<BatchJob>(), waiting = new List<BatchJob>();
            while (true)
            {
                lock (_mail)
                {
                    while (!_stop && _incoming.Count == 0 && waiting.Count == 0 && active.Count == 0) Monitor.Wait(_mail);
                    if (_stop) break;
                    waiting.AddRange(_incoming);
                    _incoming.Clear();
                }
                try { lock (_run) BatchRound(active, waiting); }
                catch (Exception e)
                {
                    Log.Error("batch round failed: " + e);
                    foreach (BatchJob j in active) { if (j.Slot != null) ResetSlot(j.Slot); Fail(j, e); }
                    foreach (BatchJob j in waiting) Fail(j, e);
                    active.Clear(); waiting.Clear();
                }
            }
            BackendException bye = new BackendException("Windlass is shutting down", 503, "server_busy");
            foreach (BatchJob j in active) Fail(j, bye);
            foreach (BatchJob j in waiting) Fail(j, bye);
            lock (_mail) { foreach (BatchJob j in _incoming) Fail(j, bye); _incoming.Clear(); }
        }

        // One round: admit what fits, drop cancelled jobs, then one stacked decode step for every active job.
        void BatchRound(List<BatchJob> active, List<BatchJob> waiting)
        {
            while (waiting.Count > 0 && active.Count < _slots.Length)
            {
                BatchJob j = waiting[0];
                waiting.RemoveAt(0);
                if (Admit(j)) active.Add(j);
            }
            List<BatchJob> step = new List<BatchJob>();
            foreach (BatchJob j in active)
            {
                if (j.Done.WaitOne(0)) continue;
                if (j.Cancel.IsCancellationRequested) { Complete(j, "cancelled"); continue; }
                bool go;
                try { go = Advance(j); }
                catch (Exception e) { Fail(j, e); continue; }
                if (go) step.Add(j);
            }
            active.RemoveAll(delegate(BatchJob j) { return j.Done.WaitOne(0); });
            if (step.Count == 0) return;

            Sequence[] seqs = new Sequence[step.Count];
            int[] toks = new int[step.Count];
            for (int i = 0; i < step.Count; i++) { seqs[i] = step[i].Slot; toks[i] = step[i].NextId; }
            Action<int, int> stage = delegate(int done, int total) { foreach (BatchJob j in step) if (j.OnStage != null) j.OnStage(done, total); };
            float[] logits;
            try { logits = Model.ForwardBatch(seqs, toks, _loopCts.Token, stage); }
            catch (Exception e)
            {
                foreach (BatchJob j in step) { ResetSlot(j.Slot); Fail(j, e); }
                active.RemoveAll(delegate(BatchJob j) { return j.Done.WaitOne(0); });
                if (_loopCts.IsCancellationRequested) return;
                Log.Error("batch step failed: " + e);
                return;
            }
            BatchSteps++; BatchRowTotal += step.Count; if (step.Count > BatchMaxRows) BatchMaxRows = step.Count;
            if (Log.Verbose) Log.Debug("batch step: " + step.Count + " sequence(s)");
            int vocab = logits.Length / step.Count;
            for (int i = 0; i < step.Count; i++)
            {
                BatchJob j = step[i];
                j.Slot.Tokens.Add(toks[i]);
                float[] row = new float[vocab];
                Array.Copy(logits, (long)i * vocab, row, 0, vocab);
                j.NextId = Sampler.Sample(row, j.Req.Temperature, j.Req.TopP, j.Rng);
            }
        }

        // Gives a job a slot and runs its prompt (an own pass; the prefix the slot already holds is skipped).
        // True when the job goes on to decode.
        bool Admit(BatchJob j)
        {
            if (j.Cancel.IsCancellationRequested) { Complete(j, "cancelled"); return false; }
            List<int> ids = j.Ids;
            Sequence best = null;
            int bestCommon = -1;
            foreach (Sequence s in _slots)
            {
                if (s.Busy) continue;
                int common = 0;
                while (common < s.Tokens.Count && common < ids.Count - 1 && s.Tokens[common] == ids[common]) common++;
                if (common > bestCommon || (common == bestCommon && s.Tokens.Count < best.Tokens.Count)) { best = s; bestCommon = common; }
            }
            if (best == null) { Fail(j, new BackendException("no free batch slot", 503, "server_busy")); return false; }
            j.Slot = best; best.Busy = true; j.Common = bestCommon;
            BatchReusedTokens += bestCommon;
            best.Truncate(bestCommon);
            best.Tokens.RemoveRange(bestCommon, best.Tokens.Count - bestCommon);
            try
            {
                int[] rest = ids.GetRange(bestCommon, ids.Count - bestCommon).ToArray();
                if (bestCommon > 0) Log.Debug("reusing " + bestCommon + " cached prompt tokens");
                float[] logits = Model.Forward(best, rest, bestCommon, false, j.Cancel, j.OnStage);
                best.Tokens.AddRange(rest);
                j.NextId = j.MaxTokens > 0 ? Sampler.Sample(logits, j.Req.Temperature, j.Req.TopP, j.Rng) : -1;
                return true;
            }
            catch (OperationCanceledException)
            {
                best.Tokens.RemoveRange(best.Length, best.Tokens.Count - best.Length);
                Complete(j, "cancelled");
                return false;
            }
            catch (Exception e)
            {
                ResetSlot(best);
                Fail(j, e);
                return false;
            }
        }

        // What the single-request loop does at the top of each turn: the sampled token is either the end (done),
        // or is sent to the client and then needs a pass. False when the job finished.
        bool Advance(BatchJob j)
        {
            if (j.Produced >= j.MaxTokens) { Complete(j, "length"); return false; }
            if (Tokenizer.IsEndOfGeneration(j.NextId)) { Complete(j, "stop"); return false; }
            j.Produced++;
            if (EmitToken(j, j.NextId)) { Complete(j, "stop"); return false; }
            if (j.Produced >= j.MaxTokens) { Complete(j, "length"); return false; }
            return true;
        }

        // Sends one token's text to the client. True when a stop sequence ends the reply.
        bool EmitToken(BatchJob j, int token)
        {
            byte[] bytes = Tokenizer.TokenBytes(token, false);
            char[] chars = new char[j.Utf8.GetCharCount(bytes, 0, bytes.Length, false)];
            j.Utf8.GetChars(bytes, 0, bytes.Length, chars, 0, false);
            string text = new string(chars);
            if (j.First && text.Length > 0)
            {
                if (Tokenizer.StripLeadingSpace && text[0] == ' ') text = text.Substring(1);
                j.First = false;
            }
            bool stopped;
            string safe = j.Scanner.Feed(text, out stopped);
            if (safe.Length > 0) j.OnText(safe);
            return stopped;
        }

        void Complete(BatchJob j, string finish)
        {
            if (j.Done.WaitOne(0)) return;
            try
            {
                if (finish != "cancelled")
                {
                    string tail = j.Scanner.Flush();   // empty after a stop sequence
                    if (tail.Length > 0) j.OnText(tail);
                }
                j.Result = new GenerationResult(finish, j.Ids.Count, j.Produced);
            }
            catch (Exception e) { j.Error = e; }
            Release(j);
        }

        void Fail(BatchJob j, Exception e)
        {
            if (j.Done.WaitOne(0)) return;
            j.Error = e;
            Release(j);
        }

        void Release(BatchJob j)
        {
            if (j.Slot != null) j.Slot.Busy = false;
            j.Done.Set();
        }

        static void ResetSlot(Sequence s) { s.Truncate(0); s.Tokens.Clear(); }
    }
}
