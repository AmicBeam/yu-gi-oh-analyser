using System.Collections.Generic;
using System.Linq;
using YGOSharp.OCGWrapper.Enums;

namespace WindBot.Game.AI.Decks
{
    public partial class TwinsExecutor
    {
        private const int MainPhaseEndTiming = 0x4;
        private const int ForbiddenDroplet = 24299458;
        private const int DarkRulerNoMore = 54693926;
        private bool _gryphonAtTurnStart;

        private void RegisterGryphonResponses()
        {
            // Resolve immediate threats to the lock before spending any unrelated hand trap.
            AddExecutor(ExecutorType.Activate, Crossout, () => GryphonThreat() && CrossoutActivate());
            AddExecutor(ExecutorType.Activate, Desirae, () => GryphonThreat() && CanDesiraeNegateCurrentCard());
            AddExecutor(ExecutorType.Activate, Disconnect, () => GryphonThreat() && HasDisconnectPair());
            AddExecutor(ExecutorType.Activate, Caesar, GryphonThreat);
            AddExecutor(ExecutorType.Activate, _CardId.AshBlossom,
                () => GryphonThreat() && DefaultAshBlossomAndJoyousSpring());
            AddExecutor(ExecutorType.Activate, _CardId.EffectVeiler, NegateGryphonThreatMonster);
            AddExecutor(ExecutorType.Activate, _CardId.InfiniteImpermanence, NegateGryphonThreatMonster);
            AddExecutor(ExecutorType.Activate, Disconnect, GryphonThreat);

            AddExecutor(ExecutorType.Activate, Fuwalos, () => Duel.Player == 1);
            AddExecutor(ExecutorType.Activate, _CardId.MaxxC, DefaultMaxxC);
            AddExecutor(ExecutorType.Activate, Desirae, () => Duel.Player == 1
                && CanDesiraeNegateCurrentCard() && IsSpell(Duel.GetCurrentChainCard()));
            AddExecutor(ExecutorType.Activate, Crossout, () => UseGryphonResponses() && CrossoutActivate());
            AddExecutor(ExecutorType.Activate, Droll, UseGryphonResponses);
            AddExecutor(ExecutorType.Activate, Disconnect,
                () => UseGryphonResponses() && EnemyChained() && HasDisconnectPair());
        }

        private static bool IsSpell(ClientCard card)
        {
            return card.HasType(CardType.Spell) || card.Data?.HasType(CardType.Spell) == true;
        }

        private bool UseGryphonResponses()
        {
            return Duel.Player == 1 && (_gryphonAtTurnStart || Bot.HasInMonstersZone(Gryphon));
        }

        private bool GryphonThreat()
        {
            if (Duel.Player != 1 || !EnemyChained())
                return false;

            ClientCard gryphon = Bot.GetMonsters().FirstOrDefault(card => card.IsCode(Gryphon)
                && card.IsFaceup() && !card.IsDisabled());
            ChainInfo chain = Duel.CurrentChainInfo.LastOrDefault();
            return gryphon != null && chain != null && (chain.Targets.Contains(gryphon)
                || chain.IsActivateCode(_CardId.Raigeki, _CardId.DarkHole, _CardId.LightningStorm,
                    ForbiddenDroplet, DarkRulerNoMore));
        }

        private bool HasFaceupTwin(int setcode)
        {
            return Bot.GetMonsters().Any(card => card.IsFaceup() && card.HasSetcode(setcode));
        }

        private bool HasDisconnectPair()
        {
            return HasFaceupTwin(KiSikilSet) && HasFaceupTwin(LilLaSet);
        }

        private bool HoldDisconnectForTwins()
        {
            if (Duel.Player != 1)
                return false;
            bool red = HasFaceupTwin(KiSikilSet);
            bool blue = HasFaceupTwin(LilLaSet);
            if (red && blue)
                return false;

            var chain = Duel.CurrentChainInfo.LastOrDefault();
            if (chain != null && chain.IsActivateCode(_CardId.Raigeki, _CardId.DarkHole, _CardId.LightningStorm))
                return false;
            if (CanStartNewYearTwinCycle() || _waitingForNewYearCycle && !BluePopUsed()
                || Duel.CurrentChainInfo.Any(link => link.ActivatePlayer == 0 && (link.IsActivateCode(Sunny)
                    || link.IsActivateCode(Nightfall) && _opponentNightfallTarget == Sunny
                    || link.IsActivateCode(Challenge) && link.Targets.Any(card =>
                        red && card.HasSetcode(LilLaSet) || blue && card.HasSetcode(KiSikilSet)))))
                return true;

            var field = Bot.MonsterZone.Select(PlanCode).ToArray();
            int sunny = System.Array.IndexOf(field, Sunny);
            if (sunny >= 0 && !_sunnyFieldEffectUsed && HasRevivableTwin(EvilKiSikil) && HasRevivableTwin(EvilLilLa)
                && RotationAllowed(Bot.MonsterZone[sunny]))
            {
                field[sunny] = 0;
                return CanRevivePair(field);
            }

            int nightfall = System.Array.IndexOf(field, Nightfall);
            if (nightfall >= 0 && !_nightfallEffectUsed && CanUseSunny() && RotationAllowed(Bot.MonsterZone[nightfall]))
            {
                field[nightfall] = 0;
                int blocked = (Enemy.MonsterZone[5] != null ? 1 << 6 : 0)
                    | (Enemy.MonsterZone[6] != null ? 1 << 5 : 0);
                if (CanRevivePair(field) && OpponentSummons(field, Sunny, true, 0, blocked).Any(option => option.Sunny))
                    return true;
            }

            return HasSetTrap(Challenge) &&
                (red && HasRevivableTwin(EvilLilLa) || blue && HasRevivableTwin(EvilKiSikil));
        }

        private bool CanDesiraeNegateCurrentCard()
        {
            ClientCard source = Duel.GetCurrentChainCard();
            return Card.Location == CardLocation.MonsterZone && !_desiraeNegateUsed
                && !DefaultCheckWhetherCardIsNegated(Card) && HasDesiraeEquip(Card)
                && EnemyChained() && source != null && source.Controller == 1
                && source.IsOnField() && source.IsFaceup() && !source.IsDisabled();
        }

        private bool NegateGryphonThreatMonster()
        {
            if (!GryphonThreat() || DefaultCheckWhetherCardIsNegated(Card))
                return false;
            ClientCard source = Duel.GetCurrentChainCard();
            if (source == null || source.Controller != 1 || source.Location != CardLocation.MonsterZone
                || !source.IsFaceup() || source.IsDisabled() || source.IsShouldNotBeTarget()
                || (Card.IsCode(_CardId.InfiniteImpermanence)
                    ? source.IsShouldNotBeSpellTrapTarget() : source.IsShouldNotBeMonsterTarget()))
                return false;

            AI.SelectCard(source);
            return true;
        }

        private IList<ClientCard> SelectGryphonDesiraeTargets(ChainInfo desirae, IList<ClientCard> cards, int min, int max)
        {
            ChainInfo response = Duel.CurrentChainInfo.TakeWhile(link => link != desirae).LastOrDefault();
            if (response == null || response.ActivatePlayer != 1 || !cards.Contains(response.RelatedCard))
                return null;

            var targets = cards.Where(card => card.Controller == 1 && card.IsFaceup() && !card.IsDisabled())
                .OrderByDescending(card => card == response.RelatedCard)
                .ThenByDescending(IsSpell)
                .ThenByDescending(card => card.IsFloodgate())
                .ThenByDescending(card => card.IsMonsterDangerous())
                .ThenByDescending(card => card.Attack)
                .Take(max).ToList();
            return targets.Count >= min ? targets : null;
        }

        private bool NibiruActivate()
        {
            if (Duel.Player != 1 || _enemySummoned < 5)
                return false;

            if (Bot.HasInMonstersZone(Gryphon, notDisabled: true, faceUp: true))
                return Duel.Phase == DuelPhase.Main1 && CurrentTiming >= 0
                    && (CurrentTiming & MainPhaseEndTiming) != 0 && EnemyCanWinByBattle();

            return Enemy.GetMonsters().Count > Bot.GetMonsters().Count;
        }

        private bool EnemyCanWinByBattle()
        {
            if (Duel.Turn <= 1 || Util.GetTotalAttackingMonsterAttack(1) < Bot.LifePoints)
                return false;

            var attacks = Enemy.GetMonsters().Where(card => card.IsFaceup()
                    && (card.IsAttack() || card.IsMonsterAttackWhileInDefPos()))
                .Select(card => card.GetAttackPower()).Where(attack => attack > 0).ToArray();
            var defenders = Bot.GetMonsters();
            return CanDealLethal(attacks, defenders, 0, 0, Bot.LifePoints, true);
        }

        private static bool CanDealLethal(int[] attacks, IList<ClientCard> defenders, int index,
            int usedAttacks, int remainingLife, bool allDefendersCleared)
        {
            if (remainingLife <= 0)
                return true;
            if (index == defenders.Count)
            {
                if (!allDefendersCleared)
                    return false;
                for (int i = 0; i < attacks.Length; i++)
                    if ((usedAttacks & (1 << i)) == 0)
                        remainingLife -= attacks[i];
                return remainingLife <= 0;
            }

            ClientCard defender = defenders[index];
            if (defender.IsFaceup() && !defender.IsMonsterInvincible())
            {
                bool attackPosition = defender.IsAttack();
                int strength = defender.GetDefensePower();
                for (int i = 0; i < attacks.Length; i++)
                {
                    if ((usedAttacks & (1 << i)) != 0 || attacks[i] < strength
                        || !attackPosition && attacks[i] == strength)
                        continue;
                    int damage = attackPosition ? attacks[i] - strength : 0;
                    if (CanDealLethal(attacks, defenders, index + 1, usedAttacks | (1 << i),
                        remainingLife - damage, allDefendersCleared))
                        return true;
                }
            }

            return CanDealLethal(attacks, defenders, index + 1, usedAttacks, remainingLife, false);
        }
    }
}
