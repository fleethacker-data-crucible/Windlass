// Tokenizers read from GGUF. C# 5 only. Ported from llm_tokenizer_bpe / llm_tokenizer_spm in llama-vocab.cpp (MIT,
// llama.cpp); the byte-level BPE part is adapted from Phoenix LAI's Tokenizer.cs (MIT).
//  - "gpt2": byte-level BPE after a regex pre-tokenizer (Llama 3 "llama-bpe", Qwen2/3 "qwen2", Mistral "tekken").
//  - "llama": SentencePiece (Llama 2, Mistral 7B / Mixtral / Mistral Large v1-v3): spaces become U+2581, pairs merge by
//    score, unknown characters fall back to <0xXX> byte tokens.
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace Windlass
{
    public sealed class Tokenizer
    {
        // tokenizer.ggml.token_type values (llama_token_type).
        const int TypeNormal = 1, TypeUnknown = 2, TypeControl = 3, TypeUserDefined = 4, TypeUnused = 5, TypeByte = 6;

        const string Llama3Pattern =
            @"(?:'[sS]|'[tT]|'[rR][eE]|'[vV][eE]|'[mM]|'[lL][lL]|'[dD])|[^\r\n\p{L}\p{N}]?\p{L}+|\p{N}{1,3}| ?[^\s\p{L}\p{N}]+[\r\n]*|\s*[\r\n]+|\s+(?!\S)|\s+";
        const string Qwen2Pattern =
            @"(?:'[sS]|'[tT]|'[rR][eE]|'[vV][eE]|'[mM]|'[lL][lL]|'[dD])|[^\r\n\p{L}\p{N}]?\p{L}+|\p{N}| ?[^\s\p{L}\p{N}]+[\r\n]*|\s*[\r\n]+|\s+(?!\S)|\s+";
        const string TekkenPattern =
            @"[^\r\n\p{L}\p{N}]?[\p{Lu}\p{Lt}\p{Lm}\p{Lo}\p{M}]*[\p{Ll}\p{Lm}\p{Lo}\p{M}]+|[^\r\n\p{L}\p{N}]?[\p{Lu}\p{Lt}\p{Lm}\p{Lo}\p{M}]+[\p{Ll}\p{Lm}\p{Lo}\p{M}]*|\p{N}| ?[^\s\p{L}\p{N}]+[\r\n/]*|\s*[\r\n]+|\s+(?!\S)|\s+";
        const string Gpt2Pattern =
            @"'s|'t|'re|'ve|'m|'ll|'d| ?\p{L}+| ?\p{N}+| ?[^\s\p{L}\p{N}]+|\s+(?!\S)";

        public readonly string[] Tokens;
        public readonly int[] TokenTypes;
        public readonly int BosId = -1, EosId = -1;
        public readonly bool AddBos;
        public readonly bool Spm;
        public readonly string Warning;

        readonly float[] _scores;
        readonly bool _addSpacePrefix;
        readonly Dictionary<string, int> _vocab;
        readonly Dictionary<string, int> _mergeRank;
        readonly List<KeyValuePair<string, int>> _specials;   // longest first
        readonly Regex _pre;
        readonly int[] _byteTokens;            // <0xXX> ids (SentencePiece), -1 if missing
        readonly int _unkId = -1;
        readonly Dictionary<string, int[]> _cache = new Dictionary<string, int[]>();
        readonly HashSet<int> _eog = new HashSet<int>();

        static readonly char[] ByteToChar = new char[256];
        static readonly Dictionary<char, byte> CharToByte = new Dictionary<char, byte>();

        static Tokenizer()
        {
            // GPT-2 bytes_to_unicode(): printable bytes map to themselves, the rest to U+0100 upward.
            int n = 0;
            for (int b = 0; b < 256; b++)
            {
                bool printable = (b >= 0x21 && b <= 0x7E) || (b >= 0xA1 && b <= 0xAC) || (b >= 0xAE && b <= 0xFF);
                char c = printable ? (char)b : (char)(256 + n++);
                ByteToChar[b] = c;
                CharToByte[c] = (byte)b;
            }
        }

        public Tokenizer(GgufModelFile g)
        {
            string model = g.GetString("tokenizer.ggml.model", "");
            Tokens = g.GetArray<string>("tokenizer.ggml.tokens");
            TokenTypes = g.Has("tokenizer.ggml.token_type") ? g.GetArray<int>("tokenizer.ggml.token_type") : null;
            if (g.Has("tokenizer.ggml.bos_token_id")) BosId = (int)g.GetInt("tokenizer.ggml.bos_token_id");
            if (g.Has("tokenizer.ggml.eos_token_id")) EosId = (int)g.GetInt("tokenizer.ggml.eos_token_id");
            if (g.Has("tokenizer.ggml.unknown_token_id")) _unkId = (int)g.GetInt("tokenizer.ggml.unknown_token_id");

            _vocab = new Dictionary<string, int>(Tokens.Length, StringComparer.Ordinal);
            for (int i = 0; i < Tokens.Length; i++)
                if (!_vocab.ContainsKey(Tokens[i])) _vocab.Add(Tokens[i], i);

            if (model == "llama")
            {
                Spm = true;
                AddBos = true;
                _addSpacePrefix = true;
                _scores = g.Has("tokenizer.ggml.scores") ? g.GetArray<float>("tokenizer.ggml.scores") : new float[Tokens.Length];
                _byteTokens = new int[256];
                for (int b = 0; b < 256; b++)
                {
                    int id;
                    _byteTokens[b] = _vocab.TryGetValue("<0x" + b.ToString("X2") + ">", out id) ? id : -1;
                }
            }
            else if (model == "gpt2")
            {
                string pre = g.GetString("tokenizer.ggml.pre", "default");
                string pattern;
                if (pre == "llama-bpe" || pre == "llama3" || pre == "smaug-bpe") pattern = Llama3Pattern;
                else if (pre == "qwen2") pattern = Qwen2Pattern;
                else if (pre == "tekken") pattern = TekkenPattern;
                else
                {
                    pattern = Gpt2Pattern;
                    if (pre != "default" && pre != "gpt2") Warning = "Pre-tokenizer '" + pre + "' is not known; using the GPT-2 rules, so some text may tokenize differently from llama.cpp.";
                }
                _pre = new Regex(pattern, RegexOptions.CultureInvariant);
                string[] merges = g.GetArray<string>("tokenizer.ggml.merges");
                _mergeRank = new Dictionary<string, int>(merges.Length, StringComparer.Ordinal);
                for (int i = 0; i < merges.Length; i++)
                    if (!_mergeRank.ContainsKey(merges[i])) _mergeRank.Add(merges[i], i);
                AddBos = false;
            }
            else throw new NotSupportedException("Tokenizer model '" + model + "' is not supported (Windlass reads 'gpt2' and 'llama').");

            if (g.Has("tokenizer.ggml.add_bos_token")) AddBos = (bool)g.Metadata["tokenizer.ggml.add_bos_token"].Value;
            if (g.Has("tokenizer.ggml.add_space_prefix")) _addSpacePrefix = (bool)g.Metadata["tokenizer.ggml.add_space_prefix"].Value;

            _specials = new List<KeyValuePair<string, int>>();
            if (TokenTypes != null)
                for (int i = 0; i < Tokens.Length; i++)
                    if (TokenTypes[i] == TypeControl || TokenTypes[i] == TypeUserDefined)
                        _specials.Add(new KeyValuePair<string, int>(Tokens[i], i));
            _specials.Sort(delegate(KeyValuePair<string, int> a, KeyValuePair<string, int> b) { return b.Key.Length.CompareTo(a.Key.Length); });

            // End-of-generation tokens, as llama.cpp collects them.
            if (EosId >= 0) _eog.Add(EosId);
            if (g.Has("tokenizer.ggml.eot_token_id")) _eog.Add((int)g.GetInt("tokenizer.ggml.eot_token_id"));
            if (g.Has("tokenizer.ggml.eom_token_id")) _eog.Add((int)g.GetInt("tokenizer.ggml.eom_token_id"));
            foreach (string t in new string[] { "<|eot_id|>", "<|eom_id|>", "<|im_end|>", "<|end|>", "<|endoftext|>", "<end_of_turn>", "</s>" })
            {
                int id;
                if (_vocab.TryGetValue(t, out id) && IsSpecial(id)) _eog.Add(id);
            }
        }

        public int VocabSize { get { return Tokens.Length; } }

        public bool IsEndOfGeneration(int id) { return _eog.Contains(id); }

        public bool IsSpecial(int id)
        {
            return TokenTypes != null && id >= 0 && id < TokenTypes.Length && (TokenTypes[id] == TypeControl || TokenTypes[id] == TypeUserDefined);
        }

        public bool HasToken(string text) { return _vocab.ContainsKey(text); }

        // parseSpecial: treat special-token text such as "<|eot_id|>" as that token (llama.cpp default for templates).
        public List<int> Encode(string text, bool addBos, bool parseSpecial)
        {
            List<int> ids = new List<int>();
            if (addBos && AddBos && BosId >= 0) ids.Add(BosId);
            int start = 0;
            while (start < text.Length)
            {
                int specialAt = text.Length, specialId = -1, specialLen = 0;
                if (parseSpecial) FindSpecial(text, start, out specialAt, out specialId, out specialLen);
                if (specialAt > start) EncodeRaw(text.Substring(start, specialAt - start), ids);
                if (specialId < 0) break;
                ids.Add(specialId);
                start = specialAt + specialLen;
            }
            return ids;
        }

        void FindSpecial(string text, int from, out int at, out int id, out int len)
        {
            at = text.Length; id = -1; len = 0;
            for (int i = 0; i < _specials.Count; i++)
            {
                int p = text.IndexOf(_specials[i].Key, from, StringComparison.Ordinal);
                if (p >= 0 && p < at) { at = p; id = _specials[i].Value; len = _specials[i].Key.Length; }
            }
        }

        void EncodeRaw(string text, List<int> ids)
        {
            if (Spm) { EncodeSpm(text, ids); return; }
            foreach (Match m in _pre.Matches(text))
            {
                int[] word;
                if (!_cache.TryGetValue(m.Value, out word))
                {
                    word = EncodeWord(m.Value);
                    if (_cache.Count < 100000) _cache[m.Value] = word;
                }
                ids.AddRange(word);
            }
        }

        // SentencePiece: llama.cpp prefixes a space to every text fragment (each one starts the text or follows a
        // special token).
        void EncodeSpm(string text, List<int> ids)
        {
            if (_addSpacePrefix) text = " " + text;
            text = text.Replace(' ', '\u2581');
            List<string> sym = new List<string>(text.Length);
            for (int k = 0; k < text.Length; k++)
            {
                if (char.IsHighSurrogate(text[k]) && k + 1 < text.Length) { sym.Add(text.Substring(k, 2)); k++; }
                else sym.Add(text[k].ToString());
            }
            int n = sym.Count;
            int[] prev = new int[n], next = new int[n];
            for (int k = 0; k < n; k++) { prev[k] = k - 1; next[k] = k + 1 < n ? k + 1 : -1; }
            // Highest score first, then leftmost (llm_bigram_spm ordering).
            SortedSet<Bigram> queue = new SortedSet<Bigram>();
            for (int k = 0; k + 1 < n; k++) TryAdd(queue, sym, k, k + 1);
            while (queue.Count > 0)
            {
                Bigram top = queue.Min;
                queue.Remove(top);
                int left = top.Left, right = next[left];
                if (sym[left] == null || right != top.Right || sym[right] == null || sym[left] + sym[right] != top.Text) continue;   // stale
                sym[left] = top.Text;
                sym[right] = null;
                next[left] = next[right];
                if (next[right] >= 0) prev[next[right]] = left;
                if (prev[left] >= 0) TryAdd(queue, sym, prev[left], left);
                if (next[left] >= 0) TryAdd(queue, sym, left, next[left]);
            }
            for (int k = 0; k >= 0 && k < n; k = next[k])
            {
                int id;
                if (_vocab.TryGetValue(sym[k], out id)) { ids.Add(id); continue; }
                foreach (byte b in Encoding.UTF8.GetBytes(sym[k]))
                {
                    if (_byteTokens[b] >= 0) ids.Add(_byteTokens[b]);
                    else if (_unkId >= 0) ids.Add(_unkId);
                    else throw new InvalidDataException("No token for byte 0x" + b.ToString("X2") + ".");
                }
            }
        }

        sealed class Bigram : IComparable<Bigram>
        {
            public float Score; public int Left, Right; public string Text; public long Seq;
            public int CompareTo(Bigram o)
            {
                int c = o.Score.CompareTo(Score);
                if (c != 0) return c;
                c = Left.CompareTo(o.Left);
                return c != 0 ? c : Seq.CompareTo(o.Seq);
            }
        }

        long _seq;

        void TryAdd(SortedSet<Bigram> queue, List<string> sym, int left, int right)
        {
            string text = sym[left] + sym[right];
            int id;
            if (!_vocab.TryGetValue(text, out id)) return;
            queue.Add(new Bigram { Score = _scores[id], Left = left, Right = right, Text = text, Seq = _seq++ });
        }

        int[] EncodeWord(string word)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(word);
            StringBuilder sb = new StringBuilder(bytes.Length);
            for (int i = 0; i < bytes.Length; i++) sb.Append(ByteToChar[bytes[i]]);
            string mapped = sb.ToString();

            int whole;
            if (_vocab.TryGetValue(mapped, out whole)) return new int[] { whole };

            List<string> sym = new List<string>(mapped.Length);
            for (int i = 0; i < mapped.Length; i++) sym.Add(mapped[i].ToString());

            // Repeatedly merge the adjacent pair with the lowest rank (leftmost on ties).
            while (sym.Count > 1)
            {
                int best = -1, bestRank = int.MaxValue;
                for (int i = 0; i + 1 < sym.Count; i++)
                {
                    int r;
                    if (_mergeRank.TryGetValue(sym[i] + " " + sym[i + 1], out r) && r < bestRank) { bestRank = r; best = i; }
                }
                if (best < 0) break;
                sym[best] = sym[best] + sym[best + 1];
                sym.RemoveAt(best + 1);
            }

            List<int> outIds = new List<int>(sym.Count);
            foreach (string s in sym)
            {
                int id;
                if (_vocab.TryGetValue(s, out id)) { outIds.Add(id); continue; }
                foreach (char c in s)
                {
                    if (_vocab.TryGetValue(c.ToString(), out id)) outIds.Add(id);
                    else throw new InvalidDataException("No token for byte 0x" + ((int)CharToByte[c]).ToString("X2") + ".");
                }
            }
            return outIds.ToArray();
        }

        public string Decode(IList<int> ids, bool renderSpecial)
        {
            List<byte> bytes = new List<byte>();
            foreach (int id in ids) bytes.AddRange(TokenBytes(id, renderSpecial));
            return Encoding.UTF8.GetString(bytes.ToArray());
        }

        // Raw bytes of one token (a streamed token may end in the middle of a UTF-8 character).
        public byte[] TokenBytes(int id, bool renderSpecial)
        {
            if (id < 0 || id >= Tokens.Length) throw new ArgumentOutOfRangeException("id");
            string t = Tokens[id];
            if (IsSpecial(id) || (TokenTypes != null && (TokenTypes[id] == TypeUnknown || TokenTypes[id] == TypeUnused)))
                return renderSpecial ? Encoding.UTF8.GetBytes(t) : new byte[0];
            if (Spm)
            {
                if (TokenTypes != null && TokenTypes[id] == TypeByte && t.Length == 6) return new byte[] { Convert.ToByte(t.Substring(3, 2), 16) };
                return Encoding.UTF8.GetBytes(t.Replace('\u2581', ' '));
            }
            List<byte> bytes = new List<byte>(t.Length);
            foreach (char c in t)
            {
                byte b;
                if (CharToByte.TryGetValue(c, out b)) bytes.Add(b);
                else bytes.AddRange(Encoding.UTF8.GetBytes(c.ToString()));
            }
            return bytes.ToArray();
        }

        // SentencePiece models start a reply with the space that stood for "beginning of word".
        public bool StripLeadingSpace { get { return Spm && _addSpacePrefix; } }
    }
}
