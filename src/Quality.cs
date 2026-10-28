// Quality score: how well the model predicts a fixed passage (perplexity; lower is better). C# 5 only.
// The passage is original and fixed. The same text and token count give scores that
// compare across quantizations of one model and across models that share a tokenizer (for example Llama 3.2 3B and
// Llama 3.3 70B). The pass also times prompt reading, the slow part on a CPU.
using System;
using System.Collections.Generic;

namespace Windlass
{
    public sealed class QualityResult
    {
        public int Tokens;
        public double Perplexity;
        public double Seconds;
        public double SecPerPromptToken { get { return Tokens > 0 ? Seconds / Tokens : 0; } }
    }

    public static class Quality
    {
        public const int DefaultTokens = 256;

        // Written for this test (not published anywhere), so no model has memorised it: a famous passage scores
        // near-perfect on every model and stops telling them apart.
        public const string Passage =
            "The relief crew came aboard at Port Halvard a little after four, carrying their sea bags and a crate of " +
            "spare shackles that nobody had asked for. Mate Ilse Brannock signed them in, checked each name against the " +
            "roster twice, and sent the two apprentices forward to help the bosun with the windlass. The brake band had " +
            "been slipping since the storm off Kessel Point, and the bosun suspected a glazed lining rather than a worn " +
            "drum. He had them back the brake off by three turns, clean the band with a rag and solvent, and inspect the " +
            "pawls for chips. One pawl spring was weak, so he swapped it for a spare from the crate, which pleased the " +
            "crew who had carried it. By six the chain ran out freely and held again on the first touch of the brake. " +
            "The captain logged the repair, the time and the names of everyone who worked on it, then asked the cook to " +
            "hold supper until the anchor watch was set. Weather for the night was light airs from the north-west, a " +
            "falling tide until two, and a short swell from the south that would roll the moored boats but not trouble " +
            "the ship. The apprentices took the first watch together, which was against the rules, and the mate let it " +
            "pass because they had earned it and because the harbour was quiet. At midnight the wind backed two points, " +
            "the chain drew taut, and the anchor held exactly where the bosun said it would.";

        // Mean negative log-likelihood of each next token, from logits for every position ([T][vocab] flattened).
        public static double Perplexity(float[] logits, IList<int> tokens)
        {
            int n = tokens.Count;
            if (n < 2) throw new ArgumentException("Need at least two tokens to score.");
            int vocab = logits.Length / n;
            double nll = 0;
            for (int i = 0; i < n - 1; i++)
            {
                long o = (long)i * vocab;
                float max = float.NegativeInfinity;
                for (int v = 0; v < vocab; v++) if (logits[o + v] > max) max = logits[o + v];
                double sum = 0;
                for (int v = 0; v < vocab; v++) sum += Math.Exp(logits[o + v] - max);
                nll += Math.Log(sum) + max - logits[o + tokens[i + 1]];
            }
            return Math.Exp(nll / (n - 1));
        }
    }
}
