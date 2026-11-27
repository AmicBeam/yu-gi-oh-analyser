using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace WindBot.Game.AI.Decks
{
    public partial class TwinsExecutor
    {
        private const string RecoveryModelVariable = "TWINS_PLAN_RECOVERY_MODEL";
        private static readonly Lazy<BranchRanker> TrainedBranchRanker = new Lazy<BranchRanker>(
            () => BranchRanker.Load(Environment.GetEnvironmentVariable("TWINS_PLAN_BRANCH_MODEL"), "branch-model"));
        private static readonly Lazy<BranchRanker> Twins2OpeningRanker = new Lazy<BranchRanker>(() =>
            BranchRanker.Load(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Models", "Twins2.first-turn-ranker.tsv"),
                "Twins2 first-turn ranker", announce: true));
        private static readonly Lazy<BranchRanker> UnifiedFrontier = new Lazy<BranchRanker>(
            () => BranchRanker.FromWeights(UnifiedTurnModel.Value?.Frontier));
        private static readonly Lazy<BranchRanker> TerminalTieRanker = new Lazy<BranchRanker>(
            () => BranchRanker.FromWeights(UnifiedTurnModel.Value?.TieFrontier));
        private static readonly Lazy<BranchRanker> RecoveryBranchRanker = new Lazy<BranchRanker>(
            () => BranchRanker.Load(Environment.GetEnvironmentVariable(RecoveryModelVariable), "recovery-model"));
        private static readonly bool RecoveryModelRequested =
            !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(RecoveryModelVariable));
        private int _trainedFrontiers, _recoveryFrontiers;
        private BranchRanker OpeningBranchRanker => TrainedBranchRanker.Value
            ?? (UsesSecondTurnModel ? UnifiedFrontier.Value ?? Twins2OpeningRanker.Value : null);

        private sealed class BranchRanker
        {
            private readonly Dictionary<int, double[]> _weights;
            private readonly double[] _scalars;

            private BranchRanker(Dictionary<int, double[]> weights)
            {
                _weights = weights;
                _scalars = weights[0];
            }

            public static BranchRanker FromWeights(Dictionary<string, double[]> source)
            {
                if (source == null || source.Count < 2 || !source.ContainsKey("0"))
                    return null;
                var weights = new Dictionary<int, double[]>();
                foreach (var row in source)
                {
                    if (!int.TryParse(row.Key, NumberStyles.None, CultureInfo.InvariantCulture, out int code)
                        || weights.ContainsKey(code) || row.Value == null || row.Value.Length != 4
                        || Array.Exists(row.Value, value => double.IsNaN(value) || Math.Abs(value) > 16))
                        return null;
                    weights.Add(code, row.Value);
                }
                return new BranchRanker(weights);
            }

            public static BranchRanker Load(string path, string trace, bool announce = false)
            {
                if (string.IsNullOrWhiteSpace(path))
                    return null;

                try
                {
                    using (var input = File.OpenRead(path))
                    using (var reader = new StreamReader(input))
                    {
                        var weights = new Dictionary<int, double[]>();
                        if (input.Length > 32768 || reader.ReadLine() != "twins-branch-ranker-v1")
                            throw new FormatException("unsupported model header or size");
                        for (string line; (line = reader.ReadLine()) != null;)
                        {
                            string[] fields = line.Split('\t');
                            if (fields.Length != 5 || !int.TryParse(fields[0], NumberStyles.None,
                                CultureInfo.InvariantCulture, out int card) || weights.ContainsKey(card))
                                throw new FormatException("invalid model row");
                            var values = new double[4];
                            for (int slot = 0; slot < values.Length; slot++)
                            {
                                if (!double.TryParse(fields[slot + 1], NumberStyles.Float, CultureInfo.InvariantCulture,
                                    out double value) || double.IsNaN(value) || Math.Abs(value) > 16)
                                    throw new FormatException("invalid model weight");
                                values[slot] = value;
                            }
                            weights.Add(card, values);
                        }
                        if (!weights.ContainsKey(0) || weights.Count < 2)
                            throw new FormatException("empty model");
                        TracePlanner(trace, "loaded");
                        if (announce)
                            Logger.WriteLine(trace + " loaded (" + weights.Count + " rows).");
                        return new BranchRanker(weights);
                    }
                }
                catch (Exception e) when (e is IOException || e is UnauthorizedAccessException
                    || e is FormatException || e is ArgumentException || e is NotSupportedException)
                {
                    TracePlanner(trace, "fallback " + e.GetType().Name);
                    if (announce)
                        Logger.WriteErrorLine(trace + " fallback: " + e.GetType().Name);
                    return null;
                }
            }

            private double ZoneScore(IList<int> cards, int slot, bool unique = false)
            {
                double score = 0;
                for (int index = 0; index < cards.Count; index++)
                {
                    int card = cards[index];
                    if (card != 0 && (!unique || cards.IndexOf(card) == index)
                        && _weights.TryGetValue(card, out var values))
                        score += values[slot];
                }
                return score;
            }

            public double Score(PlanPosition p)
            {
                return _scalars[0] * Math.Min(NibiruThreshold, p.OwnSummons)
                    + _scalars[1] * p.Draws
                    + _scalars[2] * (((p.Set & SetDisconnect) != 0 ? 1 : 0) + ((p.Set & SetChallenge) != 0 ? 1 : 0))
                    + _scalars[3] * (p.SearchLocked ? 1 : 0)
                    + ZoneScore(p.Field, 0) + ZoneScore(p.Hand, 1) + ZoneScore(p.Grave, 2) + ZoneScore(p.Extra, 3, true);
            }

            public double Priority(PlanPosition p)
            {
                // Keep the learned signal within the range of one setup heuristic.
                return p.Potential + Math.Max(-16, Math.Min(16, 8 * Score(p)));
            }
        }
    }
}
