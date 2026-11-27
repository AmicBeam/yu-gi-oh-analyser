using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using YGOSharp.OCGWrapper.Enums;

namespace WindBot.Game.AI.Decks
{
    // Named-only experimental deck. The ordinary WindBot fallback only draws Normal decks.
    [Deck("Twins2", "AI_Twins2", "Experimental")]
    public sealed class Twins2Executor : TwinsExecutor
    {
        private bool _interrupted;

        public Twins2Executor(GameAI ai, Duel duel) : base(ai, duel)
        {
            WarmTurnModel();
        }

        protected override bool UsesSecondTurnModel => true;
        protected override bool RoomClockOnlyForBoardSearch => true;
        protected override bool SecondTurnInterrupted => _interrupted;

        public override bool OnSelectHand() => true;

        public override void OnNewTurn()
        {
            _interrupted = false;
            base.OnNewTurn();
        }

        public override void OnChainSolved(int chainIndex)
        {
            var link = Duel.GetCurrentSolvingChainInfo();
            if (Duel.Player == 0 && Duel.Turn > 1 && link?.ActivatePlayer == 1
                && !Duel.IsCurrentSolvingChainNegated() && link.IsActivateCode(
                    14558128, 94145021, 94145022, 27204311, 59438930, 10045474, 73642296, 24508238, 33854624))
                _interrupted = true;
            base.OnChainSolved(chainIndex);
        }
    }

    public partial class TwinsExecutor
    {
        protected virtual bool UsesSecondTurnModel => false;
        protected virtual bool SecondTurnInterrupted => false;

        private static readonly Lazy<TurnWeights> UnifiedTurnModel = new Lazy<TurnWeights>(LoadTurnModel);

        [DataContract]
        private sealed class TurnWeights
        {
            [DataMember(Name = "version")] public int Version { get; private set; }
            [DataMember(Name = "weights")] public Dictionary<string, double> Weights { get; private set; }
            [DataMember(Name = "frontier")] public Dictionary<string, double[]> Frontier { get; private set; }
            [DataMember(Name = "tieFrontier")] public Dictionary<string, double[]> TieFrontier { get; private set; }
        }

        protected void WarmTurnModel()
        {
            // Evaluate the lazy loader at named-bot creation so a missing asset is visible.
            if (UnifiedTurnModel.Value == null)
                Logger.WriteLine("Twins2 using rule fallback.");
        }

        private static TurnWeights LoadTurnModel()
        {
            try
            {
                string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Models", "Twins2.turn-ranker.json");
                if (!File.Exists(path))
                    path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Models", "Twins2.second-turn-ranker.json");
                using (var stream = File.OpenRead(path))
                {
                    if (stream.Length > 8000000)
                        throw new FormatException("model exceeds size limit");
                    var serializer = new DataContractJsonSerializer(typeof(TurnWeights),
                        new DataContractJsonSerializerSettings {
                            UseSimpleDictionaryFormat = true,
                            MaxItemsInObjectGraph = 200000,
                        });
                    var model = (TurnWeights)serializer.ReadObject(stream);
                    if (model == null || model.Version != 1 && model.Version != 2 || model.Weights == null || model.Weights.Count == 0
                        || model.Weights.Values.Any(value => double.IsInfinity(value) || double.IsNaN(value)) ||
                        model.Version == 2 && BranchRanker.FromWeights(model.Frontier) == null ||
                        model.TieFrontier != null && BranchRanker.FromWeights(model.TieFrontier) == null)
                        throw new FormatException("invalid model version or weights");
                    Logger.WriteLine("Twins2 turn ranker loaded (" + model.Weights.Count + " weights).");
                    return model;
                }
            }
            catch (Exception error) when (error is IOException || error is UnauthorizedAccessException
                || error is SerializationException || error is FormatException)
            {
                Logger.WriteErrorLine("Twins2 turn ranker: " + error.Message);
                return null;
            }
        }

        private bool SecondTurnRankingActive =>
            UsesSecondTurnModel && Duel.Player == 0 && UnifiedTurnModel.Value != null;

        private double SecondTurnPreference(string prompt, string action, DuelPhase phase)
        {
            var weights = UnifiedTurnModel.Value.Weights;
            if (!weights.TryGetValue("a:" + prompt + ":" + action, out double score))
                return 0;
            var own = Bot.GetMonsters();
            var enemy = Enemy.GetMonsters();
            var backrow = Enemy.GetSpells();
            int Cap(int value, int maximum) => Math.Min(maximum, Math.Max(0, value));
            double Weight(string context) => weights.TryGetValue(context + ":" + action, out double found) ? found : 0;
            score += Weight("phase:" + (int)phase);
            score += Weight("bodies:" + Cap(own.Count, 4));
            score += Weight("enemy:" + Cap(enemy.Count, 4));
            score += Weight("backrow:" + Cap(backrow.Count, 4));
            score += Weight("atk:" + Cap(own.Sum(card => Math.Max(0, card.Attack)) / 1000, 8));
            score += Weight("their-lp:" + Cap((Enemy.LifePoints + 999) / 1000, 8));
            score += Weight("our-lp:" + Cap((Bot.LifePoints + 999) / 1000, 8));
            score += Weight("post-hit:" + (SecondTurnInterrupted ? "true" : "false"));
            score += Weight("normal:" + (_normalSummoned ? "true" : "false"));
            foreach (var card in enemy.Concat(backrow).Where(card => card.IsFaceup()))
                score += Weight("visible:" + card.Id);
            return score;
        }

        private string SecondTurnTargetKey(ClientCard card) =>
            (card.Controller == 0 || card.IsFaceup() ? card.Id : 0) + ":" +
            (1 - card.Controller) + ":" + (int)card.Location + ":" + card.Sequence;

        private double SecondTurnClearPreference(BattleClear line)
        {
            if (!SecondTurnRankingActive || line == null)
                return 0;
            int command = line.Materials == null ? 5 : 1; // OCGCore ACTIVATE / SPSUMMON
            int desc = line.Materials == null ? line.Description : 0;
            string action = command + "/" + SecondTurnTargetKey(line.Source) + "/" + desc;
            double score = SecondTurnPreference("SelectIdleCmd", action, Duel.Phase);
            if (line.Target != null)
                score += SecondTurnPreference("SelectCard", SecondTurnTargetKey(line.Target), Duel.Phase);
            return score;
        }

        private readonly struct BattleForecast
        {
            public int Clears { get; }
            public int Damage { get; }
            public int MaxDamage { get; }

            public BattleForecast(int clears, int damage, int maxDamage)
            {
                Clears = clears;
                Damage = damage;
                MaxDamage = maxDamage;
            }

            public BattleForecast Add(int clear, int damage) =>
                new BattleForecast(Clears + clear, Damage + damage, MaxDamage + damage);

            public BattleForecast Merge(BattleForecast other)
            {
                var best = other.Clears > Clears || (other.Clears == Clears && other.Damage > Damage) ? other : this;
                return new BattleForecast(best.Clears, best.Damage, Math.Max(MaxDamage, other.MaxDamage));
            }
        }

        private BattlePhaseAction TrySecondTurnAttack(IList<ClientCard> attackers, IList<ClientCard> defenders)
        {
            if (!SecondTurnRankingActive || attackers.Count == 0 || attackers.Count > 7 || defenders.Count > 7
                || defenders.Any(card => !card.IsFaceup()))
                return null;

            // Exclude uncertain or losing exchanges using the existing battle checks.
            var damage = new int?[attackers.Count, defenders.Count];
            for (int i = 0; i < attackers.Count; i++)
            for (int j = 0; j < defenders.Count; j++)
            {
                var attacker = attackers[i];
                var defender = defenders[j];
                attacker.RealPower = attacker.GetAttackPower();
                defender.RealPower = defender.GetDefensePower();
                if (OnPreBattleBetween(attacker, defender) && !defender.IsMonsterInvincible()
                    && attacker.RealPower > defender.RealPower)
                    damage[i, j] = defender.IsAttack() ? attacker.RealPower - defender.RealPower : 0;
            }

            var cache = new Dictionary<int, BattleForecast>();
            BattleForecast Project(int remaining, int blocks)
            {
                int key = (remaining << 7) | blocks;
                if (cache.TryGetValue(key, out var cached))
                    return cached;
                var best = new BattleForecast();
                for (int i = 0; i < attackers.Count; i++)
                {
                    if ((remaining & (1 << i)) == 0)
                        continue;
                    int next = remaining & ~(1 << i);
                    if (attackers[i].CanDirectAttack || blocks == 0 && defenders.Count != 0)
                        best = best.Merge(Project(next, blocks).Add(0, Math.Max(0, attackers[i].GetAttackPower())));
                    for (int j = 0; j < defenders.Count; j++)
                        if ((blocks & (1 << j)) != 0 && damage[i, j].HasValue)
                            best = best.Merge(Project(next, blocks & ~(1 << j)).Add(1, damage[i, j].Value));
                }
                cache.Add(key, best);
                return best;
            }

            int available = (1 << attackers.Count) - 1;
            int blockers = (1 << defenders.Count) - 1;
            ClientCard chosen = null;
            ClientCard target = null;
            int bestLethal = -1, bestClears = -1, bestDamage = -1;
            double bestPreference = double.NegativeInfinity;
            void Consider(int i, ClientCard defender, BattleForecast forecast)
            {
                int lethal = forecast.MaxDamage >= Enemy.LifePoints && Enemy.LifePoints > 0 ? 1 : 0;
                int clears = lethal == 1 ? 0 : forecast.Clears;
                int hit = lethal == 1 ? 0 : forecast.Damage;
                var attacker = attackers[i];
                string action = "attack:" + attacker.Id + ":" + attacker.Sequence + ":" + (attacker.CanDirectAttack ? 1 : 0);
                double preference = SecondTurnPreference("SelectBattleCmd", action, DuelPhase.BattleStart);
                if (defender != null)
                    preference += SecondTurnPreference("SelectCard", SecondTurnTargetKey(defender), DuelPhase.BattleStart);
                if ((lethal, clears, hit, preference).CompareTo((bestLethal, bestClears, bestDamage, bestPreference)) <= 0)
                    return;
                (bestLethal, bestClears, bestDamage, bestPreference) = (lethal, clears, hit, preference);
                chosen = attacker;
                target = defender;
            }

            for (int i = 0; i < attackers.Count; i++)
            {
                int next = available & ~(1 << i);
                if (attackers[i].CanDirectAttack)
                    Consider(i, null, Project(next, blockers).Add(0, Math.Max(0, attackers[i].GetAttackPower())));
                for (int j = 0; j < defenders.Count; j++)
                    if (damage[i, j].HasValue)
                        Consider(i, defenders[j], Project(next, blockers & ~(1 << j)).Add(1, damage[i, j].Value));
            }
            if (chosen == null)
                return null;
            TracePlanner("twins2-battle", chosen.Id + " " + (target?.Id ?? 0)
                + " " + bestPreference.ToString("0.000", CultureInfo.InvariantCulture));
            return AI.Attack(chosen, target);
        }
    }
}
