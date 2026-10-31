// Context fidelity: does the model still find a detail planted in a long log? C# 5 only.
// A short version of the stress test's long-conversation phase (docs/STRESS-TEST.md, phase 6). The test builds a
// synthetic ship's log (original text; every name, number and code is invented for it) with N planted facts hidden
// among filler entries, then asks one question per fact. A reply scores when it contains the planted value. The log is
// deterministic from a seed and grows to a length target, so the same check can run at 300 tokens or 6,000.
// Nothing here needs a model: Generate, Prompt and Score are plain functions the tests call directly, and Run drives
// any Backend through its normal chat path (greedy, small max_tokens).
using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;

namespace Windlass
{
    public sealed class FidelityFact
    {
        public string Kind;        // person, quantity, code or place
        public string Statement;   // the log entry that plants it
        public string Question;
        public string Answer;      // the planted value
    }

    public sealed class FidelityCase
    {
        public int Seed, Version;
        public string Passage;
        public readonly List<FidelityFact> Facts = new List<FidelityFact>();
    }

    public sealed class FidelityResult
    {
        public int Correct, Total, PromptTokens, Seed, MaxTokens;
        public double Seconds;
        public readonly List<string> Replies = new List<string>();
        public readonly List<bool> Recalled = new List<bool>();
        public readonly List<FidelityFact> Asked = new List<FidelityFact>();
        public double Score { get { return Total > 0 ? (double)Correct / Total : 0; } }
    }

    public static class Fidelity
    {
        // Bump when the text, the questions or the scoring change: scores of different versions do not compare.
        public const int Version = 1;
        public const int DefaultFacts = 4, MaxFacts = 40, DefaultTokens = 300, DefaultSeed = 1, ReplyTokens = 24, MaxChars = 60000;
        public const string SystemPrompt = "You answer questions about a ship's log. Reply with only the value that was asked for, nothing else.";

        // ---------- words the log is made from (original, invented) ----------

        static readonly string[] FirstNames = { "Odile", "Tamsin", "Corvin", "Bertrand", "Ysolde", "Halvard", "Mirela", "Joss", "Ambrose", "Wenna", "Fenwick", "Isaura", "Lorcan", "Petra", "Dagny", "Evander", "Sorrel", "Tobias", "Linnea", "Casimir", "Rhosyn", "Anselm", "Briony", "Matthias", "Oriel", "Gethin", "Saskia", "Roderic", "Nerys", "Quill", "Leopold", "Marit", "Dunstan", "Elowen", "Ivor", "Thessaly", "Kasper", "Perpetua", "Hollis", "Zenobia", "Alaric", "Maren", "Jory", "Sidonie" };
        static readonly string[] LastNames = { "Marrick", "Penhallow", "Vandersloot", "Quillfeather", "Oakhurst", "Trevanion", "Lindqvist", "Ashgrove", "Pellworth", "Hargreave", "Duskmoor", "Fairweather", "Corbett", "Yarrowby", "Stannard", "Brackwater", "Illingham", "Roskilly", "Tamberlay", "Wetherby", "Nightingale", "Kerrow", "Blackthorn", "Mossop", "Peverell", "Galloway", "Thistlewood", "Ravenscar", "Dunsford", "Eastlake", "Fenwright", "Holloway", "Ironside", "Jessop", "Kittering", "Langstaff", "Merriweather", "Northcote", "Overton", "Pargeter", "Redfern", "Saltmarsh", "Tidewell", "Underhay" };
        static readonly string[] Roles = { "second mate", "ship's cook", "radio operator", "chief engineer", "deck cadet", "boatswain", "surgeon", "purser", "lamp trimmer", "chart keeper", "sailmaker", "pilot" };
        static readonly string[] Items = { "diesel day tank", "fresh water tank", "ballast tank", "lube oil sump", "rope locker", "paint store", "spare anchor chain", "galley larder", "signal flag chest", "lifeboat fuel can", "sounding line drum", "bilge sump" };
        static readonly string[] Stores = { "spare parts store", "chart room cabinet", "medicine chest", "bosun's locker", "radio room safe", "navigation lamp box", "tool crib", "flare locker", "bond room", "cable locker", "paint locker", "rigging loft" };
        static readonly string[] Vessels = { "grey trawler", "red-funnelled coaster", "cable layer", "pilot cutter", "survey launch", "salvage tug", "mail packet", "timber barge", "fishing smack", "lightship tender", "dredger", "ice patrol sloop" };
        static readonly string[] Places = { "Skerrowick", "Thornquay", "Grimsby Deeps", "Pellmouth", "Corran Sound", "Whitlowe Bank", "Ostermere", "Halyard Ness", "Brineholm", "Low Tarrant", "Sealgate", "Wrackfleet" };
        static readonly string[] CodeLetters = { "KX", "QM", "VR", "TZ", "HB", "WJ", "DN", "FP", "LS", "GC", "YA", "ZE" };

        // Filler entries: ordinary log lines with no numbers, names or codes that could be mistaken for a planted value.
        static readonly string[] Weather = { "light airs from the north-west", "a steady breeze on the beam", "a long swell from the south", "low cloud and a falling glass", "fog lifting by mid-morning", "a clear sky and a flat sea", "squalls passing to the east", "a freshening wind and a short chop" };
        static readonly string[] Jobs = { "scraped and primed the foredeck rails", "oiled the winch gears", "mended the starboard awning", "checked every lifejacket in the rack", "swept the engine room bilges", "tested the steering chain", "polished the binnacle brass", "restowed the mooring lines", "cleaned out the galley stove", "greased the davit blocks", "painted the boat numbers afresh", "overhauled the hand pump" };
        static readonly string[] Meals = { "salt beef and ship's biscuit", "a thin fish stew", "bean soup with fresh bread", "boiled potatoes and onions", "porridge and tinned plums", "pea soup and cheese" };
        static readonly string[] Watches = { "the forenoon watch", "the afternoon watch", "the first watch", "the middle watch", "the morning watch", "the dog watches" };
        static readonly string[] Moods = { "was quiet", "passed without incident", "kept everyone busy", "dragged a little", "ended early", "was cold but calm" };

        // ---------- the generator ----------

        // Small deterministic generator (xorshift32): the same seed gives the same log on every machine and runtime.
        sealed class Rng
        {
            uint _s;
            public Rng(int seed) { _s = (uint)seed * 2654435761u + 0x9E3779B9u; if (_s == 0) _s = 1; for (int i = 0; i < 4; i++) Next(); }
            public int Next() { _s ^= _s << 13; _s ^= _s >> 17; _s ^= _s << 5; return (int)(_s & 0x7fffffff); }
            public int Next(int n) { return Next() % n; }
        }

        static string[] Shuffled(string[] pool, Rng r)
        {
            string[] a = (string[])pool.Clone();
            for (int i = a.Length - 1; i > 0; i--) { int j = r.Next(i + 1); string t = a[i]; a[i] = a[j]; a[j] = t; }
            return a;
        }

        // The log: `facts` planted facts (1 to MaxFacts) among filler entries until it is about `targetChars` long.
        // Deterministic from the seed. Every planted value is distinct, shares no word with another, appears exactly
        // once, and is never in a question.
        public static FidelityCase Generate(int seed, int facts, int targetChars)
        {
            if (facts < 1 || facts > MaxFacts) throw new ArgumentException("Facts must be from 1 to " + MaxFacts + ".");
            // Entry numbers stay under 1000 so they can never equal a planted 4-digit quantity.
            if (targetChars > MaxChars) throw new ArgumentException("The log can be at most " + MaxChars + " characters.");
            Rng r = new Rng(seed);
            FidelityCase c = new FidelityCase();
            c.Seed = seed; c.Version = Version;
            string[] first = Shuffled(FirstNames, r), last = Shuffled(LastNames, r), roles = Shuffled(Roles, r), items = Shuffled(Items, r);
            string[] stores = Shuffled(Stores, r), vessels = Shuffled(Vessels, r), places = Shuffled(Places, r), letters = Shuffled(CodeLetters, r);
            HashSet<string> used = new HashSet<string>();
            int[] nextOf = new int[4];
            for (int i = 0; i < facts; i++)
            {
                int kind = i % 4, k = nextOf[kind]++;
                FidelityFact f = new FidelityFact();
                switch (kind)
                {
                    case 0:
                        f.Kind = "person"; f.Answer = first[k] + " " + last[k];
                        f.Statement = "The " + roles[k] + " signed on today: " + f.Answer + ", who had come down from the north by rail.";
                        f.Question = "Who signed on as the " + roles[k] + "?";
                        break;
                    case 1:
                    {
                        // 4 digits that never start with 0 and are never a multiple of 1000 (so "3,000" and "3000" differ little).
                        string n;
                        do { n = (1000 + r.Next(9000)).ToString(); } while (used.Contains(n) || n.EndsWith("000", StringComparison.Ordinal));
                        f.Kind = "quantity"; f.Answer = n;
                        f.Statement = "The " + items[k] + " was sounded and held " + n + " litres by the engineer's count.";
                        f.Question = "How many litres did the " + items[k] + " hold when it was sounded?";
                        break;
                    }
                    case 2:
                    {
                        string code;
                        do { code = letters[k % letters.Length] + (1000 + r.Next(9000)).ToString(); } while (used.Contains(code.ToLowerInvariant()));
                        f.Kind = "code"; f.Answer = code;
                        f.Statement = "The tag on the key to the " + stores[k] + " was re-stamped " + code + " and hung back on its hook.";
                        f.Question = "What code is stamped on the tag for the key to the " + stores[k] + "?";
                        break;
                    }
                    default:
                        f.Kind = "place"; f.Answer = places[k];
                        f.Statement = "A " + vessels[k] + " lay at anchor off " + places[k] + " and dipped her ensign as we passed.";
                        f.Question = "Off which place did the " + vessels[k] + " lie at anchor?";
                        break;
                }
                foreach (string w in Words(f.Answer)) used.Add(w);
                if (kind == 2) used.Add(f.Answer.ToLowerInvariant());
                c.Facts.Add(f);
            }

            // Filler entries, with the facts spread evenly through them in a shuffled order.
            int[] pick = new int[facts];
            for (int i = 0; i < facts; i++) pick[i] = i;
            for (int i = facts - 1; i > 0; i--) { int j = r.Next(i + 1); int t = pick[i]; pick[i] = pick[j]; pick[j] = t; }
            List<string> entries = new List<string>();
            int per = targetChars / facts;
            for (int slot = 0; slot < facts; slot++)
            {
                int chars = 0;
                while (chars < per - 160)   // 160: about one planted entry and its label
                {
                    string e = Filler(r);
                    entries.Add(e); chars += e.Length + 16;
                }
                entries.Add(c.Facts[pick[slot]].Statement);
            }
            // Re-order the facts to the order they appear in the log (the questions follow it).
            List<FidelityFact> inLog = new List<FidelityFact>();
            for (int slot = 0; slot < facts; slot++) inLog.Add(c.Facts[pick[slot]]);
            c.Facts.Clear();
            c.Facts.AddRange(inLog);

            StringBuilder sb = new StringBuilder();
            sb.Append("Log of the survey tender Dunmore Reach. Every name, number and code in this log is invented for a memory test.\n\n");
            for (int i = 0; i < entries.Count; i++)
                sb.Append("Day ").Append(i / 3 + 1).Append(", entry ").Append(i + 1).Append(": ").Append(entries[i]).Append('\n');
            c.Passage = sb.ToString().TrimEnd('\n');
            return c;
        }

        static string Filler(Rng r)
        {
            string weather = Weather[r.Next(Weather.Length)], job = Jobs[r.Next(Jobs.Length)], meal = Meals[r.Next(Meals.Length)];
            string watch = Watches[r.Next(Watches.Length)], mood = Moods[r.Next(Moods.Length)];
            switch (r.Next(5))
            {
                case 0: return "Wind and sea: " + weather + ". The crew " + job + " and " + watch + " " + mood + ".";
                case 1: return "The deck hands " + job + ", and the cook served " + meal + " at the usual hour.";
                case 2: return "Under way with " + weather + "; " + watch + " " + mood + " and the lookouts reported nothing worth noting.";
                case 3: return "The mate reported that the crew " + job + ". Supper was " + meal + ", and " + watch + " " + mood + ".";
                default: return "Routine day aboard: the crew " + job + ", the weather was " + weather + ", and there was " + meal + " to eat.";
            }
        }

        // Rough length of a log for a token budget: about 4 characters per token for English text. A backend with a
        // tokenizer corrects this against the real count (LayeredBackend.CheckFidelity).
        public static int CharsForTokens(int tokens) { return Math.Max(200, tokens * 4); }

        // ---------- the questions ----------

        // One question as a chat: the whole log goes in the user message with the question after it, so the engine's
        // prompt-prefix cache reuses the log for every later question.
        public static List<ChatMessage> Prompt(FidelityCase c, FidelityFact f)
        {
            List<ChatMessage> m = new List<ChatMessage>();
            m.Add(new ChatMessage("system", SystemPrompt));
            m.Add(new ChatMessage("user", c.Passage + "\n\nQuestion: " + f.Question));
            return m;
        }

        // ---------- scoring ----------

        // Lower case, every run of other characters as one space, and thousands separators dropped ("4,172" -> "4172").
        public static string Normalise(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            StringBuilder sb = new StringBuilder(s.Length);
            for (int i = 0; i < s.Length; i++)
            {
                char ch = s[i];
                if (ch == ',' && i > 0 && i + 1 < s.Length && char.IsDigit(s[i - 1]) && char.IsDigit(s[i + 1])) continue;
                if (char.IsLetterOrDigit(ch)) sb.Append(char.ToLowerInvariant(ch));
                else if (sb.Length > 0 && sb[sb.Length - 1] != ' ') sb.Append(' ');
            }
            return sb.ToString().Trim();
        }

        static IEnumerable<string> Words(string s)
        {
            return Normalise(s).Split(new char[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
        }

        // True when the reply contains the planted value as whole words, ignoring case, punctuation and thousands
        // separators. "Odile" alone does not recall "Odile Marrick", and "41720" does not recall "4172". A code may be
        // written with or without its separator ("KX-4471" or "KX4471").
        public static bool Recalled(string reply, string answer)
        {
            string a = Normalise(answer), r = Normalise(reply);
            if (a.Length == 0 || r.Length == 0) return false;
            if ((" " + r + " ").IndexOf(" " + a + " ", StringComparison.Ordinal) >= 0) return true;
            // Written with different spacing ("kx 4471" for KX4471): some run of whole reply words spells the value.
            string target = a.Replace(" ", "");
            string[] rw = r.Split(' ');
            for (int i = 0; i < rw.Length; i++)
            {
                StringBuilder run = new StringBuilder();
                for (int j = i; j < rw.Length && run.Length < target.Length; j++) run.Append(rw[j]);
                if (run.Length == target.Length && run.ToString() == target) return true;
            }
            return false;
        }

        // Scores replies against the facts in the order they were asked. A missing, null or empty reply is wrong.
        public static FidelityResult Score(IList<FidelityFact> asked, IList<string> replies)
        {
            FidelityResult res = new FidelityResult();
            for (int i = 0; i < asked.Count; i++)
            {
                string reply = replies != null && i < replies.Count ? replies[i] : null;
                bool ok = Recalled(reply, asked[i].Answer);
                res.Asked.Add(asked[i]); res.Replies.Add(reply ?? ""); res.Recalled.Add(ok);
                if (ok) res.Correct++;
            }
            res.Total = asked.Count;
            return res;
        }

        // Checks the promises of Generate; returns a description of the first one broken, or null.
        public static string Validate(FidelityCase c)
        {
            HashSet<string> words = new HashSet<string>();
            string passage = " " + Normalise(c.Passage) + " ";
            foreach (FidelityFact f in c.Facts)
            {
                if (string.IsNullOrEmpty(f.Answer)) return "empty answer";
                string a = Normalise(f.Answer);
                foreach (string w in Words(f.Answer)) if (!words.Add(w)) return "the word '" + w + "' is in two answers";
                int at = passage.IndexOf(" " + a + " ", StringComparison.Ordinal);
                if (at < 0) return "'" + f.Answer + "' is not in the log";
                if (passage.IndexOf(" " + a + " ", at + 1, StringComparison.Ordinal) >= 0) return "'" + f.Answer + "' is in the log twice";
                if ((" " + Normalise(f.Question) + " ").IndexOf(" " + a + " ", StringComparison.Ordinal) >= 0) return "'" + f.Answer + "' is in its own question";
                foreach (FidelityFact g in c.Facts)
                    if (!ReferenceEquals(f, g) && (" " + Normalise(g.Question) + " ").IndexOf(" " + a + " ", StringComparison.Ordinal) >= 0) return "'" + f.Answer + "' is in another question";
            }
            return null;
        }

        // ---------- running it ----------

        // Asks `questions` of the case's facts (all by default) through b.Generate, greedy with a small max_tokens, one
        // chat request each. Throws OperationCanceledException when cancel is signalled.
        public static FidelityResult Run(Backend b, FidelityCase c, int questions, CancellationToken cancel)
        {
            int n = questions <= 0 ? c.Facts.Count : Math.Min(questions, c.Facts.Count);
            List<FidelityFact> asked = new List<FidelityFact>();
            List<string> replies = new List<string>();
            System.Diagnostics.Stopwatch sw = System.Diagnostics.Stopwatch.StartNew();
            for (int i = 0; i < n; i++)
            {
                cancel.ThrowIfCancellationRequested();
                FidelityFact f = c.Facts[i];
                GenerationRequest req = new GenerationRequest();
                req.Messages = Prompt(c, f);
                req.MaxTokens = ReplyTokens; req.Temperature = 0;
                StringBuilder text = new StringBuilder();
                GenerationResult g = b.Generate(req, delegate(string t) { text.Append(t); }, null, cancel);
                if (g.FinishReason == "cancelled") throw new OperationCanceledException(cancel);
                asked.Add(f); replies.Add(text.ToString());
            }
            FidelityResult res = Score(asked, replies);
            res.Seed = c.Seed; res.MaxTokens = ReplyTokens; res.Seconds = sw.Elapsed.TotalSeconds;
            return res;
        }
    }
}
