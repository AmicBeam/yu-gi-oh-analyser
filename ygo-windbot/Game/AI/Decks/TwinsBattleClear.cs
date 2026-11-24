using System.Collections.Generic;
using System.Linq;
using YGOSharp.OCGWrapper.Enums;

namespace WindBot.Game.AI.Decks
{
    public partial class TwinsExecutor
    {
        private const int EngraverRemoval = 1;
        private const int TwinRemoval = 2;
        private const int AbaoRemoval = 4;

        private ClientCard _abaoRemovalTarget;
        private ClientCard _blueBattleRemovalTarget;
        private readonly HashSet<int> _battleClearEffectsUsed = new HashSet<int>();

        private sealed class BattleClear
        {
            public ClientCard Source;
            public int Description;
            public ClientCard Selection;
            public ClientCard Target;
            public ClientCard Equip;
            public ClientCard ReturnedCard;
            public ClientCard RevivedCard;
            public IList<ClientCard> Materials;
            public ClientCard Discard;
            public int Steps;
            public int RevivalZone;
        }

        private sealed class BattlePosition
        {
            public IList<ClientCard> Field;
            public IList<ClientCard> Grave;
            public IList<ClientCard> Defenders;
            public IList<ClientCard> Backrow;
            public IList<ClientCard> LostEquips;
        }

        private void RecordBattleClearEffects()
        {
            foreach (ChainInfo link in Duel.CurrentChainInfo.Where(link => link.ActivatePlayer == 0))
            {
                foreach (int code in new[] { KiSikil, LilLa, EvilKiSikil, EvilLilLa, Requiem, Engraver, Abao })
                {
                    if (!link.IsActivateCode(code))
                        continue;
                    // Alternate art uses its own string ID in some card scripts.
                    if (link.ActivateDescription / 16 == code || link.ActivateDescription / 16 == link.ActivateId)
                        _battleClearEffectsUsed.Add(Util.GetStringId(code, link.ActivateDescription % 16));
                }
            }
        }

        private bool BattleClearUnused(int code, int effect)
        {
            return !_battleClearEffectsUsed.Contains(Util.GetStringId(code, effect));
        }

        private bool OwnMainBeforeBattle()
        {
            return Duel.Player == 0 && Duel.Turn > 1 && Duel.Phase == DuelPhase.Main1;
        }

        private BattlePosition CurrentBattlePosition()
        {
            return new BattlePosition
            {
                Field = Bot.GetMonsters(),
                Grave = Bot.Graveyard.ToList(),
                Defenders = Enemy.GetMonsters(),
                Backrow = Enemy.GetSpells(),
                LostEquips = new List<ClientCard>(),
            };
        }

        private bool TryStartBattleClear()
        {
            if (!OwnMainBeforeBattle() || Card.IsDisabled())
                return false;

            var current = CurrentBattlePosition();
            if (current.Defenders.Count + current.Backrow.Count == 0 || CanFinishBattle(current))
                return false;
            var lines = BattleClears(current).Where(OfferedInMainPhase).ToList();
            BattleClear best = null;
            long bestScore = long.MinValue;
            foreach (BattleClear clear in lines)
            {
                long score = EvaluateBattleClear(current, clear, 0, 0);
                if (score > bestScore)
                {
                    best = clear;
                    bestScore = score;
                }
            }

            if (best == null || Card != best.Source)
                return false;
            if (best.Materials != null)
            {
                if (Type != ExecutorType.SpSummon)
                    return false;
                AI.SelectMaterials(best.Materials, HintMsg.LinkMaterial);
                AI.SelectPlace(RotationExtraZones(current.Field.Except(best.Materials).ToList()));
                return true;
            }
            if (Type != ExecutorType.Activate || !MatchesBattleClear(best, ActivateDescription))
                return false;
            if (best.Source.IsCode(Abao))
            {
                _abaoReviveTarget = null;
                _abaoRemovalTarget = best.Target;
            }
            AI.SelectCard(best.Selection);
            if (best.RevivalZone != 0)
                AI.SelectPlace(best.RevivalZone);
            if (best.Equip != null && best.Source.IsCode(Engraver) && best.Source.Location == CardLocation.MonsterZone)
                AI.SelectNextCard(best.Target);
            return true;
        }

        private static bool MatchesBattleClear(BattleClear clear, int description)
        {
            return clear.Description == description
                || description == clear.Source.Id * 16 + clear.Description % 16;
        }

        private bool OfferedInMainPhase(BattleClear clear)
        {
            var main = Duel.MainPhase;
            if (clear.Materials != null)
                return Type == ExecutorType.SpSummon && Card == clear.Source
                    || main.SpecialSummonableCards.Contains(clear.Source);
            if (Type == ExecutorType.Activate && Card == clear.Source && MatchesBattleClear(clear, ActivateDescription))
                return true;
            return main.ActivableCards.Where((card, index) => MatchesBattleClear(clear, main.ActivableDescs[index]))
                .Contains(clear.Source);
        }

        private long EvaluateBattleClear(BattlePosition position, BattleClear clear, int used, int steps)
        {
            BattlePosition after = ProjectClear(position, clear);
            used |= RemovalEngineMask(clear.Source);
            steps += clear.Steps;
            long score = BattleClearScore(after, steps);
            foreach (BattleClear next in BattleClears(after, used))
                score = System.Math.Max(score, EvaluateBattleClear(after, next, used, steps));
            return score;
        }

        private static int RemovalEngineMask(ClientCard card)
        {
            return card.IsCode(Abao) ? AbaoRemoval : card.IsCode(EvilKiSikil) ? TwinRemoval : EngraverRemoval;
        }

        private IEnumerable<BattleClear> BattleClears(BattlePosition position, int used = 0)
        {
            if (position.Defenders.Count + position.Backrow.Count == 0)
                yield break;
            if ((used & AbaoRemoval) == 0)
            {
                foreach (BattleClear clear in AbaoBattleClears(position))
                    yield return clear;
            }

            var targets = RankDesiraeTargets(position.Defenders.Concat(position.Backrow)).ToList();
            if (targets.Count == 0)
                yield break;

            int revivalZone = BattleRevivalZone(position.Field);
            var red = position.Field.FirstOrDefault(card => card.IsCode(EvilKiSikil) && RotationAllowed(card));
            var blue = position.Grave.FirstOrDefault(card => card.IsCode(EvilLilLa) && card.IsCanRevive());
            if ((used & TwinRemoval) == 0 && red != null && blue != null && revivalZone != 0
                && BattleClearUnused(EvilKiSikil, 1)
                && BattleClearUnused(EvilLilLa, 0)
                && !position.Field.Any(card => card.IsFaceup() && card.HasSetcode(LilLaSet)))
            {
                foreach (ClientCard target in targets.Where(card => !card.IsMonsterInvincible()))
                    yield return new BattleClear
                    {
                        Source = red,
                        Description = Util.GetStringId(EvilKiSikil, 1),
                        Selection = blue,
                        Target = target,
                        RevivedCard = blue,
                        RevivalZone = revivalZone,
                        Steps = 2,
                    };
            }

            if ((used & EngraverRemoval) != 0 || !BattleClearUnused(Engraver, 1))
                yield break;
            var engraver = position.Field.FirstOrDefault(card => card.IsCode(Engraver) && RotationAllowed(card));
            var equip = Bot.SpellZone.FirstOrDefault(card => card != null && card.IsFaceup()
                && card.IsCode(Requiem, Sequence) && !position.LostEquips.Contains(card));
            var requiem = position.Grave.FirstOrDefault(card => card.IsCode(Requiem));
            bool canEquip = requiem != null && BattleClearUnused(Requiem, 1) && CanEquipDesirae();
            if (engraver != null)
            {
                if (equip == null && !canEquip)
                    yield break;
                foreach (ClientCard target in targets.Where(card => card.Location == CardLocation.MonsterZone))
                    yield return new BattleClear
                    {
                        Source = equip != null ? engraver : requiem,
                        Description = Util.GetStringId(equip != null ? Engraver : Requiem, 1),
                        Selection = equip ?? engraver,
                        Equip = equip,
                        Target = target,
                        Steps = equip != null ? 1 : 2,
                    };
                yield break;
            }

            engraver = position.Grave.FirstOrDefault(card => card.IsCode(Engraver));
            if (engraver == null || revivalZone == 0 || !BattleClearUnused(Engraver, 2) || equip == null && !canEquip)
                yield break;
            foreach (ClientCard cost in position.Grave.Where(card => IsLightFiend(card) && !card.IsCode(Engraver)
                && (equip != null || !card.IsCode(Requiem))).OrderBy(card => card.IsCode(EvilKiSikil)))
            {
                foreach (ClientCard target in targets.Where(card => card.Location == CardLocation.MonsterZone))
                    yield return new BattleClear
                    {
                        Source = engraver,
                        Description = Util.GetStringId(Engraver, 2),
                        Selection = cost,
                        ReturnedCard = cost,
                        Equip = equip,
                        Target = target,
                        RevivedCard = engraver,
                        RevivalZone = revivalZone,
                        Steps = equip != null ? 2 : 3,
                    };
            }
        }

        private IEnumerable<BattleClear> AbaoBattleClears(BattlePosition position)
        {
            if (!BattleClearUnused(Abao, 0))
                yield break;
            ClientCard discard = AbaoDiscards(Bot.Hand.Where(card => !position.Grave.Contains(card))).FirstOrDefault();
            if (discard == null)
                yield break;
            var targets = position.Defenders.Concat(position.Backrow)
                .Where(card => !card.IsMonsterInvincible())
                .OrderByDescending(card => card.IsFaceup() && card.IsFloodgate())
                .ThenByDescending(card => card.IsFaceup() && card.IsMonsterDangerous()).ToList();
            ClientCard abao = position.Field.FirstOrDefault(card => card.IsCode(Abao) && RotationAllowed(card));
            if (abao != null)
            {
                foreach (ClientCard target in targets)
                    yield return new BattleClear { Source = abao, Description = Util.GetStringId(Abao, 0),
                        Selection = discard, Discard = discard, Target = target, Steps = 1 };
                yield break;
            }

            abao = Bot.ExtraDeck.FirstOrDefault(card => card.IsCode(Abao));
            if (abao == null)
                yield break;
            var available = position.Field.Where(card => card.IsFaceup()
                && !card.IsCode(Nightfall, Sunny, Desirae, Gryphon, Caesar)).ToList();
            foreach (var materials in Util.GetLinkMaterials(available, 4, 2))
            {
                if (!materials.Any(card => card.Race == (int)CardRace.Fiend)
                    || RotationExtraZones(position.Field.Except(materials).ToList()) == 0)
                    continue;
                foreach (ClientCard target in targets)
                    yield return new BattleClear { Source = abao, Materials = materials, Discard = discard,
                        Target = target, Steps = 2 };
            }
        }

        private int BattleRevivalZone(IList<ClientCard> field)
        {
            int occupied = field.Aggregate(0, (zones, card) => zones | (1 << card.Sequence));
            int free = Zones.MainMonsterZones & ~occupied;
            if (Bot.HasInMonstersZone(Gryphon, notDisabled: true, faceUp: true)
                || Enemy.HasInMonstersZone(Gryphon, notDisabled: true, faceUp: true))
                free &= LinkedZones(field);
            return free & -free;
        }

        private BattlePosition ProjectClear(BattlePosition before, BattleClear clear)
        {
            var field = before.Field.ToList();
            var grave = before.Grave.Where(card => card != clear.ReturnedCard && card != clear.RevivedCard).ToList();
            var lostEquips = before.LostEquips.ToList();
            if (clear.Discard != null)
                grave.Add(clear.Discard);
            if (clear.Materials != null)
            {
                field.RemoveAll(card => clear.Materials.Contains(card));
                grave.AddRange(clear.Materials);
                int zones = RotationExtraZones(field);
                int sequence = Enumerable.Range(0, 7).First(i => (zones & (1 << i)) != 0);
                field.Add(new ClientCard(Abao, CardLocation.MonsterZone, sequence, (int)CardPosition.FaceUpAttack));
            }
            if (clear.RevivedCard != null)
            {
                int sequence = System.Array.IndexOf(new[] { Zones.z0, Zones.z1, Zones.z2, Zones.z3, Zones.z4 }, clear.RevivalZone);
                field.Add(new ClientCard(clear.RevivedCard.Id, CardLocation.MonsterZone, sequence, (int)CardPosition.FaceUpAttack));
            }
            if (clear.Equip != null)
            {
                lostEquips.Add(clear.Equip);
                grave.Add(clear.Equip);
            }
            return new BattlePosition
            {
                Field = field,
                Grave = grave,
                Defenders = before.Defenders.Where(card => card != clear.Target).ToList(),
                Backrow = before.Backrow.Where(card => card != clear.Target).ToList(),
                LostEquips = lostEquips,
            };
        }

        private int ProjectedAttack(ClientCard card, IList<ClientCard> lostEquips)
        {
            if (!card.IsFaceup() || !card.IsAttack() || card.Attacked)
                return 0;
            bool onLiveField = card.Sequence >= 0 && card.Sequence < Bot.MonsterZone.Length
                && Bot.MonsterZone[card.Sequence] == card;
            int attack = onLiveField ? card.GetAttackPower() : PrintedAttack(card.Id);
            attack -= 600 * card.EquipCards.Count(equip => equip.IsCode(Requiem)
                && !equip.IsDisabled() && lostEquips.Contains(equip));
            return System.Math.Max(0, attack);
        }

        private IEnumerable<List<ClientCard>> ProjectedNightfallMaterials(IList<ClientCard> field)
        {
            if (!Bot.HasInExtra(Nightfall))
                yield break;
            var links = field.Where(card => card.IsFaceup() && card.Data != null
                && card.Data.HasType(CardType.Link) && card.Data.Level == 2).ToList();
            for (int i = 0; i < links.Count; ++i)
            {
                for (int j = i + 1; j < links.Count; ++j)
                {
                    var pair = new List<ClientCard> { links[i], links[j] };
                    if (pair.Any(card => card.HasSetcode(KiSikilSet) || card.HasSetcode(LilLaSet))
                        && RotationExtraZones(field.Except(pair).ToList()) != 0)
                        yield return pair;
                }
            }
        }

        private bool EnterBattleForCurrentBoard()
        {
            if (!OwnMainBeforeBattle())
                return false;
            var field = Bot.GetMonsters();
            int burn = CanPlanLacrimaBurn(CurrentBattlePosition()) ? LacrimaBurnDamage : 0;
            return field.Any(attacker => TryLeadAttack(ReadyAttackDamage(new[] { attacker }),
                ReadyAttackDamage(field.Where(card => card != attacker)) + burn, out _));
        }

        private bool CanFinishBattle(BattlePosition position)
        {
            return Enemy.LifePoints > 0 && ProjectedBattleDamage(position) >= Enemy.LifePoints;
        }

        private int ProjectedBattleDamage(BattlePosition position)
        {
            int best = 0;
            int burn = CanPlanLacrimaBurn(position) ? LacrimaBurnDamage : 0;
            foreach (ClientCard attacker in position.Field)
            {
                int tail = position.Field.Where(card => card != attacker)
                    .Sum(card => ProjectedAttack(card, position.LostEquips));
                int attack = ProjectedAttack(attacker, position.LostEquips);
                int damage = LeadDamage(attack, tail, position.Defenders);
                if (damage > 0 && damage < Enemy.LifePoints && damage + burn >= Enemy.LifePoints)
                    damage += burn;
                best = System.Math.Max(best, damage);
                if (!attacker.IsCode(Nightfall) || !RotationAllowed(attacker))
                    continue;
                var remaining = position.Field.Where(card => card != attacker).ToList();
                if (ReadyForSunny(remaining, position.Grave))
                    best = System.Math.Max(best, LeadDamage(attack, tail + PrintedAttack(Sunny)
                        + TwinAttackFromGrave(position.Grave, OpenMainZonesAfter(remaining)), position.Defenders));
            }
            foreach (var materials in ProjectedNightfallMaterials(position.Field))
            {
                var remaining = position.Field.Except(materials).ToList();
                var grave = position.Grave.Concat(materials).ToList();
                if (!ReadyForSunny(remaining, grave))
                    continue;
                int tail = remaining.Sum(card => ProjectedAttack(card, position.LostEquips)) + PrintedAttack(Sunny)
                    + TwinAttackFromGrave(grave, OpenMainZonesAfter(remaining));
                best = System.Math.Max(best, LeadDamage(PrintedAttack(Nightfall), tail, position.Defenders));
            }
            return best;
        }

        private static int LeadDamage(int attack, int tail, IList<ClientCard> defenders)
        {
            if (attack <= 0)
                return 0;
            if (defenders.Count == 0)
                return attack + tail;
            if (defenders.Count != 1)
                return 0;
            ClientCard blocker = defenders[0];
            if (!blocker.IsFaceup() || blocker.IsMonsterInvincible() || blocker.IsMonsterDangerous()
                || attack <= blocker.GetDefensePower())
                return 0;
            return (blocker.IsAttack() ? attack - blocker.GetDefensePower() : 0) + tail;
        }

        private long BattleClearScore(BattlePosition position, int steps)
        {
            int interactions = position.Field.Count(card => card.IsFaceup() && !card.IsDisabled()
                && (card.IsCode(Caesar) && card.HasXyzMaterial()
                    || card.IsCode(Desirae) && card.EquipCards.Any(equip => !position.LostEquips.Contains(equip))
                    || card.IsCode(Abao) && Bot.Hand.Any(hand => !position.Grave.Contains(hand))));
            if (position.Field.Any(card => card.IsCode(Gryphon) && card.IsFaceup() && !card.IsDisabled())
                && (LinkedZones(position.Field) & OpponentMainMonsterZones) == 0)
                interactions += 2;
            var nightfallMaterials = ProjectedNightfallMaterials(position.Field).ToList();
            if (position.Field.Any(card => card.IsCode(Nightfall) && card.IsFaceup() && !card.IsDisabled()))
                nightfallMaterials.Add(new List<ClientCard>());
            int nightfallInteractions = 0;
            foreach (var materials in nightfallMaterials)
            {
                var grave = position.Grave.Concat(materials).ToList();
                bool canRevive = grave.Any(card => card.IsCanRevive()
                    && (card.HasSetcode(KiSikilSet) || card.HasSetcode(LilLaSet)));
                int count = ProjectedTwinTrap(Disconnect, grave) ? 1 : 0;
                if (canRevive && ProjectedTwinTrap(Challenge, grave))
                    count++;
                bool readyTwins = Bot.HasInExtra(Sunny)
                    && grave.Any(card => card.IsCode(EvilKiSikil) && card.IsCanRevive())
                    && grave.Any(card => card.IsCode(EvilLilLa) && card.IsCanRevive())
                    && grave.Any(card => card.HasSetcode(EvilTwinSet) && !card.IsCode(EvilKiSikil, EvilLilLa));
                nightfallInteractions = System.Math.Max(nightfallInteractions, count + (readyTwins ? 1 : 0));
            }
            interactions += nightfallInteractions;
            int damage = ProjectedBattleDamage(position);
            int removed = Enemy.GetMonsters().Count + Enemy.GetSpells().Count
                - position.Defenders.Count - position.Backrow.Count;
            // Compare lethal, interactions, cards cleared, damage, then the number of actions.
            return (damage >= Enemy.LifePoints && Enemy.LifePoints > 0 ? 100000000000L : 0)
                + interactions * 100000000L + removed * 1000000L + System.Math.Min(damage, 99999) * 10L - steps;
        }

        private bool ProjectedTwinTrap(int id, IList<ClientCard> grave)
        {
            return HasSetTrap(id) || Bot.Hand.Any(card => card.IsCode(id) && !grave.Contains(card))
                || Bot.SpellZone.Take(5).Any(card => card == null)
                    && (Bot.HasInDeck(id) || grave.Any(card => card.IsCode(id)));
        }

        private bool ActivateBlueBattleRemoval()
        {
            if (!OwnMainBeforeBattle()
                || !MatchesEffect(EvilLilLa, 0) && ActivateDescription != Card.Id * 16)
                return false;
            var position = CurrentBattlePosition();
            ClientCard target = RankDesiraeTargets(position.Defenders.Concat(position.Backrow)).Where(card => !card.IsMonsterInvincible())
                .OrderByDescending(card => BattleClearScore(ProjectClear(position, new BattleClear { Target = card }), 1))
                .FirstOrDefault();
            if (target == null)
                return false;
            _blueBattleRemovalTarget = target;
            return true;
        }
    }
}
