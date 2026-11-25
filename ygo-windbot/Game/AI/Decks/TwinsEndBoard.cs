using System;
using System.Collections.Generic;
using System.Linq;
using YGOSharp.OCGWrapper.Enums;

namespace WindBot.Game.AI.Decks
{
    public partial class TwinsExecutor
    {
        private readonly HashSet<ClientCard> _opponentActivatedCards = new HashSet<ClientCard>();
        private ClientCard _challengeRevivalTarget;
        private int _challengeRevivalPlace;
        private bool _challengeRevivalForRemoval;
        private int _challengeBattleStarter;

        private const int EvilTwinSet = 0x2151;
        private const int OpponentMainMonsterZones = Zones.MainMonsterZones << 16;

        // Leave spaces the Link monsters can point at once the materials leave the field.
        private static int LinkedZones(IEnumerable<ClientCard> monsters)
        {
            return monsters.Aggregate(0, (zones, card) => zones | card.GetLinkedZones());
        }

        private static int FreeLinkedMainZones(IEnumerable<ClientCard> monsters)
        {
            int occupied = monsters.Aggregate(0, (zones, card) => zones | (1 << card.Sequence));
            return LinkedZones(monsters) & Zones.MainMonsterZones & ~occupied;
        }

        private static bool HasTwoZones(int zones)
        {
            return (zones & (zones - 1)) != 0;
        }

        private bool HasSetTrap(int id)
        {
            return Bot.SpellZone.Any(card => card != null && card.IsCode(id) && card.IsFacedown());
        }

        private bool HasLiveDisconnect()
        {
            return Bot.HasInMonstersZone(Nightfall, faceUp: true) && HasSetTrap(Disconnect);
        }

        private bool CanUseSunny()
        {
            return Bot.HasInMonstersZone(Nightfall, notDisabled: true, faceUp: true)
                && Bot.HasInExtra(Sunny)
                && HasRevivableTwin(EvilKiSikil) && HasRevivableTwin(EvilLilLa)
                && NightfallCosts().Any();
        }

        private IEnumerable<ClientCard> NightfallCosts()
        {
            return Bot.Graveyard.Where(card => card.HasSetcode(EvilTwinSet) && !card.IsCode(EvilKiSikil, EvilLilLa))
                .OrderBy(card => card.IsCode(Challenge, Disconnect));
        }

        private bool HasRevivableTwin(int id)
        {
            return Bot.Graveyard.Any(card => card.IsCode(id) && card.IsCanRevive());
        }

        private bool ChallengeActivate()
        {
            if (Duel.CurrentChainInfo.Any(chain => chain.ActivatePlayer == 0
                && (chain.IsActivateCode(Nightfall) || chain.IsActivateCode(Sunny)
                    || chain.IsActivateCode(Abao) && _abaoReviveTarget != null
                        && (IsRedTwin(PlanCode(_abaoReviveTarget)) || IsBlueTwin(PlanCode(_abaoReviveTarget))))))
                return false;
            if (Duel.Player == 1 && InBattle() && Bot.GetMonsters().Count == 0 && PrepareChallengeBattleDefense())
                return true;
            bool uncoveredTarget = HasUncoveredChallengeRemoval();
            var desiraePartners = Duel.Player == 0 || uncoveredTarget
                ? ChallengeDesiraePartners().ToList() : new List<ClientCard>();
            var targets = Duel.Player == 1 ? Bot.Graveyard.Where(card => card.IsCanRevive()
                && (card.HasSetcode(KiSikilSet) || card.HasSetcode(LilLaSet))).ToList() : new List<ClientCard>();
            bool canStartTwinCycle = Duel.Player == 1 && CanStartNewYearTwinCycle();
            if (Duel.Player == 1 && uncoveredTarget && !BluePopUsed() && !canStartTwinCycle
                && !CanUseSunny() && !Bot.HasInMonstersZone(Sunny))
            {
                bool tradeAndNegate = desiraePartners.Any(card => ChallengeLinkFor(card) == Heist)
                    && HasFreshHeistTarget() && UncoveredChallengeRemovalTargets().Take(2).Count() == 2;
                if (!tradeAndNegate && PrepareBeneficialChallengeRevival(targets, true, false, removalOnly: true))
                    return true;
            }
            if (desiraePartners.Count > 0)
            {
                if (Bot.HasInExtra(Heist) && !Duel.ChainTargets.Contains(Card)
                    && (!HasFreshHeistTarget() || CanUseSunny() && !BluePopUsed()))
                    return false;
                _challengeRevivalTarget = desiraePartners[0];
                AI.SelectCard(desiraePartners);
                return true;
            }
            if (Duel.Player != 1)
                return false;
            bool threatened = Duel.ChainTargets.Contains(Card);
            if (!threatened && CanHoldChallengeForDesirae())
                return false;
            if (canStartTwinCycle)
            {
                if (uncoveredTarget && HasVisibleEnemyCard())
                {
                    _waitingForNewYearCycle = true;
                    AI.SelectCard(EvilLilLa);
                    return true;
                }
                if (!threatened)
                    return false;
            }
            var blue = Bot.GetMonsters().FirstOrDefault(card => card.IsCode(EvilLilLa) && card.IsFaceup());
            var partners = new List<ClientCard>();
            if (blue != null && CanUseBlueForChallenge() && ChallengeHeistZones(blue, null) != 0)
            {
                partners = targets.Where(ChallengeHeistPartner).ToList();
                if (partners.Count != 0 && HasFreshHeistTarget())
                {
                    AI.SelectCard(partners);
                    return true;
                }
            }
            if (!threatened && Bot.HasInExtra(Heist)
                && (CanUseSunny() || Bot.HasInMonstersZone(Sunny)
                    || Duel.CurrentChainInfo.Any(link => link.ActivatePlayer == 0 && link.IsActivateCode(EvilLilLa))))
                return false;

            bool holdForHeist = !threatened && partners.Count != 0;
            return PrepareBeneficialChallengeRevival(targets, uncoveredTarget, holdForHeist)
                || threatened && targets.Count > 0;
        }

        private bool PrepareChallengeBattleDefense()
        {
            foreach (int starter in new[] { KiSikil, LilLa })
            {
                bool red = starter == KiSikil;
                PlanEffect summon = red ? PlanEffect.SmallRed : PlanEffect.SmallBlue;
                if ((_planUsed & Flag(summon)) != 0)
                    continue;
                var twin = Bot.Graveyard.FirstOrDefault(card => card.IsCode(starter) && card.IsCanRevive());
                if (twin == null)
                    continue;
                int[] partners = red ? new[] { LilLa, Treat, Sweet } : new[] { KiSikil, Frost, NewYear };
                if (!Bot.HasInDeck(partners) && !Bot.Hand.Any(card => card.IsCode(partners)))
                    continue;
                int zone = Enumerable.Range(0, 5)
                    .Where(i => ChallengeRevivalUnderEnemyGryphon(starter, i)).DefaultIfEmpty(-1).First();
                if (zone < 0)
                    continue;

                _challengeBattleStarter = starter;
                _challengeRevivalTarget = twin;
                _challengeRevivalPlace = 1 << zone;
                AI.SelectCard(twin);
                return true;
            }
            return false;
        }

        private bool PrepareBeneficialChallengeRevival(IList<ClientCard> targets, bool uncoveredTarget, bool holdForHeist,
            bool removalOnly = false)
        {
            if (targets.Count == 0)
                return false;

            var field = Bot.MonsterZone.Select(PlanCode).ToArray();
            bool redOnField = HasFaceupTwin(KiSikilSet);
            bool blueOnField = HasFaceupTwin(LilLaSet);
            bool canPop = !BluePopUsed() && uncoveredTarget;
            bool canDraw = !_searchLocked && (_planUsed & Flag(PlanEffect.RedDraw)) == 0;
            bool canSearch = !holdForHeist && targets.Any(card => card.IsCode(NewYear)) && !_searchLocked && !_newYearSearched
                && (Bot.HasInDeck(Password) || Bot.HasInGraveyard(Password));
            bool enablesDisconnect = HasSetTrap(Disconnect)
                && !Bot.GetMonsters().Any(card => card.IsFaceup() && card.HasSetcode(EvilTwinSet));
            bool preservePair = CanUseSunny();

            foreach (ClientCard target in targets.OrderBy(card => card.IsCode(EvilKiSikil) ? 0 : card.IsCode(EvilLilLa) ? 1 : 2))
            {
                if (preservePair && target.IsCode(EvilKiSikil, EvilLilLa))
                    continue;
                int id = PlanCode(target);
                foreach (var placement in OpponentSummons(field, id, false, 0))
                {
                    int zone = Enumerable.Range(0, 5).First(i => field[i] != placement.Field[i]);
                    bool works = CanActivateOnField(placement.Field, zone) && ChallengeRevivalUnderEnemyGryphon(id, zone);
                    bool canReviveThenPop = target.IsCode(EvilKiSikil) && !blueOnField && HasVisibleEnemyCard() && canPop
                        && (_planUsed & Flag(PlanEffect.RedRevive)) == 0 && HasRevivableTwin(EvilLilLa)
                        && !Enemy.HasInMonstersZone(Gryphon, notDisabled: true, faceUp: true)
                        && CanReviveBlue(placement.Field);
                    bool removesCard = works && (canReviveThenPop || target.IsCode(EvilLilLa) && redOnField && canPop);
                    bool benefit = removesCard || !removalOnly && (enablesDisconnect && target.HasSetcode(EvilTwinSet)
                        || works && (target.IsCode(EvilKiSikil) && blueOnField && canDraw
                            || target.IsCode(NewYear) && canSearch));
                    if (!benefit)
                        continue;
                    _challengeRevivalTarget = target;
                    _challengeRevivalPlace = 1 << zone;
                    _challengeRevivalForRemoval = removesCard;
                    AI.SelectCard(target);
                    return true;
                }
            }
            return false;
        }

        private bool HasUncoveredChallengeRemoval() => UncoveredChallengeRemovalTargets().Any();

        private IEnumerable<ClientCard> UncoveredChallengeRemovalTargets()
        {
            return RankDesiraeTargets(Enemy.GetMonsters().Concat(Enemy.GetSpells())).Where(card => !HasPendingRemoval(card)
                && (card.Location == CardLocation.MonsterZone || card.HasType(CardType.Continuous)
                    || card.HasType(CardType.Field) || card.HasType(CardType.Equip) || card.HasType(CardType.Pendulum)
                    || !Duel.CurrentChainInfo.Any(link => link.ActivatePlayer == 1 && link.RelatedCard == card)));
        }

        private bool ChallengeRevivalUnderEnemyGryphon(int card, int zone)
        {
            if (!Enemy.HasInMonstersZone(Gryphon, notDisabled: true, faceUp: true))
                return true;
            var own = Bot.GetMonsters();
            var enemy = Enemy.GetMonsters();
            int incoming = LinkedZones(own) | LinkedZones(enemy) >> 16;
            int occupied = own.Aggregate(0, (zones, monster) => zones | 1 << monster.Sequence)
                | enemy.Aggregate(0, (zones, monster) => zones | 1 << (monster.Sequence + 16));
            return (incoming & 1 << zone) != 0 || (Arrows(card, zone) & occupied) != 0;
        }


        private bool BluePopUsed()
        {
            return (_planUsed & Flag(PlanEffect.BluePop)) != 0;
        }

        private bool HasFreshHeistTarget()
        {
            return Enemy.GetMonsters().Concat(Enemy.GetSpells()).Any(IsFreshHeistTarget);
        }

        private bool IsFreshHeistTarget(ClientCard card)
        {
            if (card.Controller != 1 || !card.IsFaceup() || card.IsDisabled()
                || _opponentActivatedCards.Contains(card) || card.IsShouldNotBeTarget()
                || card.IsShouldNotBeMonsterTarget() || HasPendingRemoval(card))
                return false;
            if (card.Location == CardLocation.MonsterZone)
                return card.HasType(CardType.Effect) && RotationAllowed(card);
            return card.HasType(CardType.Field) || card.HasType(CardType.Continuous);
        }

        private bool HasPendingRemoval(ClientCard card)
        {
            int lastPending = Duel.SolvingChainIndex == 0 ? Duel.CurrentChainInfo.Count : Duel.SolvingChainIndex - 1;
            for (int i = 0; i < lastPending; i++)
            {
                var link = Duel.CurrentChainInfo[i];
                if (link.ActivatePlayer != 0 || Duel.NegatedChainIndexList.Contains(i + 1))
                    continue;
                // If the protocol has not supplied Blue's target yet, wait for its removal to resolve.
                if (link.IsActivateCode(EvilLilLa) && LivePlanEffect(link, EvilLilLa) == 0
                    && (link.Targets.Count == 0 || link.Targets.Contains(card)))
                    return true;
                if (link.IsActivateCode(Abao) && card == _abaoRemovalTarget)
                    return true;
                if (link.IsActivateCode(Desirae) && link.ActivateLocation == CardLocation.Grave
                    && link.Targets.Contains(card))
                    return true;
            }
            return HasPendingBlueSummon(lastPending);
        }

        private bool HasPendingBlueSummon(int lastPending)
        {
            if (BluePopUsed() || Duel.CurrentChainInfo.Any(link => link.ActivatePlayer == 0
                && link.IsActivateCode(EvilLilLa) && LivePlanEffect(link, EvilLilLa) == 0))
                return false;

            bool redOnField = HasFaceupTwin(KiSikilSet);
            if (redOnField && Duel.LastSummonPlayer == 0 && Duel.LastSummonedCards.Any(card => card != null
                && card.Controller == 0 && card.Location == CardLocation.MonsterZone && card.IsCode(EvilLilLa)
                && card.IsFaceup() && RotationAllowed(card)))
                return true;

            for (int i = 0; i < lastPending; i++)
            {
                var link = Duel.CurrentChainInfo[i];
                if (link.ActivatePlayer != 0 || Duel.NegatedChainIndexList.Contains(i + 1))
                    continue;

                if (link.IsActivateCode(Nightfall) && LivePlanEffect(link, Nightfall) == 1
                    && _opponentNightfallTarget == EvilLilLa && redOnField)
                    return true;

                bool pullsPair = link.IsActivateCode(Sunny) && link.ActivateLocation == CardLocation.MonsterZone
                    || link.IsActivateCode(Nightfall) && LivePlanEffect(link, Nightfall) == 1
                        && _opponentNightfallTarget == Sunny;
                if (pullsPair && HasRevivableTwin(EvilLilLa))
                {
                    var field = BlueRevivalField(link.RelatedCard);
                    if (redOnField ? CanReviveBlue(field) : HasRevivableTwin(EvilKiSikil) && CanRevivePair(field))
                        return true;
                }

                bool targetsBlue = link.IsActivateCode(Challenge, EvilKiSikil, Heist)
                    && link.Targets.Any(target => target.IsCode(EvilLilLa) && target.Location == CardLocation.Grave);
                if (!redOnField)
                {
                    if (_waitingForNewYearCycle && targetsBlue && link.IsActivateCode(Challenge)
                        && HasRevivableTwin(EvilKiSikil) && CanRevivePair(BlueRevivalField(null)))
                        return true;
                    continue;
                }

                bool mayReviveBlue = link.Targets.Count == 0 && HasRevivableTwin(EvilLilLa)
                    && (link.IsActivateCode(Challenge)
                        || link.IsActivateCode(EvilKiSikil) && LivePlanEffect(link, EvilKiSikil) == 1
                        || link.IsActivateCode(NightPhoto) && LivePlanEffect(link, NightPhoto) == 1);
                bool abaoRevivesBlue = link.IsActivateCode(Abao) && _abaoReviveTarget?.IsCode(EvilLilLa) == true;
                if ((targetsBlue || mayReviveBlue || abaoRevivesBlue)
                    && CanReviveBlue(BlueRevivalField(abaoRevivesBlue ? link.RelatedCard : null)))
                    return true;
            }
            return false;
        }

        private int[] BlueRevivalField(ClientCard leaving)
        {
            return Bot.MonsterZone.Select(card => card != leaving ? PlanCode(card) : 0).ToArray();
        }

        private bool CanStartNewYearTwinCycle()
        {
            if (Duel.Player != 1 || Bot.HasInMonstersZone(Desirae) || !Bot.HasInMonstersZone(Nightfall)
                || !HasSetTrap(Challenge)
                || !HasRevivableTwin(EvilKiSikil) || !HasRevivableTwin(EvilLilLa)
                || (_planUsed & Flag(PlanEffect.NewYearJump)) != 0
                || !Bot.HasInGraveyard(NewYear) && !Bot.Hand.Any(card => card.IsCode(NewYear)))
                return false;
            var field = Bot.MonsterZone.Select(PlanCode).ToArray();
            return !field.Any(id => IsRedTwin(id) || IsBlueTwin(id))
                && CanRevivePair(field);
        }

        private int NewYearTwinCyclePlace(int[] field, int blue)
        {
            int red = Array.IndexOf(field, EvilKiSikil);
            if (red < 0 || blue < 0)
                return 0;
            var released = (int[])field.Clone();
            released[blue] = 0;
            foreach (var option in OpponentSummons(released, NewYear, false, 0))
            {
                var placed = option.Field;
                int zone = Array.IndexOf(placed, NewYear);
                if (CanActivateOnField(placed, red) && CanActivateOnField(placed, zone)
                    && CanReviveBlue(placed))
                    return 1 << zone;
            }
            return 0;
        }

        private static bool HasDesiraeEquip(ClientCard card)
        {
            return card.EquipCards.Any(equip => equip.IsFaceup()
                && equip.Data != null && equip.Data.HasType(CardType.Link));
        }

        private bool NeedsDesiraeEquip()
        {
            return Bot.GetMonsters().Any(card => card.IsCode(Desirae) && card.IsFaceup()
                && !card.IsDisabled() && !HasDesiraeEquip(card));
        }

        private static bool IsLightFiend(ClientCard card)
        {
            return card.Race == (int)CardRace.Fiend && card.Attribute == (int)CardAttribute.Light;
        }

        private static IOrderedEnumerable<ClientCard> PhotoReturnTargets(IEnumerable<ClientCard> candidates)
        {
            return candidates.OrderBy(card => card.IsCode(EvilKiSikil, EvilLilLa))
                .ThenBy(card => IsLightFiend(card))
                .ThenBy(card => card.IsCode(Deal, Scout));
        }

        private bool CanEquipDesirae()
        {
            return Bot.SpellZone.Take(5).Any(card => card == null);
        }

        private int FreeFusionMaterials()
        {
            // The red Evil Twin is a LIGHT Fiend too, but it is reserved for Sunny.
            return Bot.Graveyard.Count(card => IsLightFiend(card) && !card.IsCode(Engraver, EvilKiSikil));
        }

        private bool SequenceActivate()
        {
            if (Card.Location != CardLocation.MonsterZone || !MatchesEffect(Sequence, 0))
                return false;
            if (Bot.HasInExtra(Desirae) && CanEquipDesirae()
                && Bot.HasInGraveyard(Engraver) && FreeFusionMaterials() >= 2)
            {
                _sequenceFusion = Desirae;
                return true;
            }
            if (Bot.HasInExtra(LacrimaFusion) && (!Bot.HasInExtra(Gryphon) || HasLiveDisconnect()))
            {
                _sequenceFusion = LacrimaFusion;
                return true;
            }
            return false;
        }

        private bool PrepareSequence()
        {
            if (!Bot.HasInExtra(Gryphon))
                return (Bot.HasInGraveyard(Engraver) || Bot.HasInGraveyard(Lacrima))
                    && PrepareLink(Requiem, Lacrima, Engraver, Scout);

            if (_sequenceFusionUsed || !HasLiveDisconnect())
                return false;

            var available = Bot.GetMonsters().Where(card => card.IsFaceup()).ToList();
            ClientCard engraver = available.FirstOrDefault(card => card.IsCode(Engraver));
            if (engraver == null)
                return false;
            var otherMaterials = available.Where(card => !card.IsCode(Engraver, Nightfall, Gryphon,
                Sunny, Desirae, Caesar, EvilKiSikil, EvilLilLa)).OrderBy(card => card.IsCode(NightPhoto));
            int graveMaterials = FreeFusionMaterials();
            bool canMakeDesirae = Bot.HasInExtra(Desirae) && CanEquipDesirae();
            foreach (ClientCard other in otherMaterials)
            {
                if (graveMaterials + (IsLightFiend(other) ? 1 : 0) < 2)
                    continue;
                var remaining = available.Where(card => card != engraver && card != other).ToList();
                // Desirae can use Sequence itself as its Link equip.
                if (!canMakeDesirae && (!Bot.HasInExtra(LacrimaFusion)
                    || (SafeGryphonZone(remaining, out _) == 0 && !Bot.HasInExtra(Caesar))))
                    continue;
                AI.SelectMaterials(new[] { engraver, other }, HintMsg.LinkMaterial);
                return true;
            }
            return false;
        }

        private bool PrepareCaesar()
        {
            var field = Bot.GetMonsters();
            ClientCard lacrima = field.FirstOrDefault(card => card.IsFaceup() && card.IsCode(LacrimaFusion));
            ClientCard engraver = field.FirstOrDefault(card => card.IsFaceup() && card.IsCode(Engraver));
            if (lacrima == null || engraver == null)
                return false;

            var materials = new[] { lacrima, engraver };
            if (Bot.HasInMonstersZone(Gryphon, notDisabled: true, faceUp: true))
            {
                var remaining = field.Except(materials).ToList();
                int linked = FreeLinkedMainZones(remaining);
                if (Duel.IsNewRule && !remaining.Any(card => card.Sequence >= 5))
                {
                    int freeExtra = (Enemy.MonsterZone[6] == null ? Zones.z5 : 0)
                        | (Enemy.MonsterZone[5] == null ? Zones.z6 : 0);
                    linked |= LinkedZones(remaining) & freeExtra;
                }
                if (linked == 0)
                    return false;
                AI.SelectPlace(linked);
            }
            AI.SelectMaterials(materials, HintMsg.XyzMaterial);
            return true;
        }

        private bool PrepareNightfallPlacement(IList<ClientCard> available)
        {
            foreach (var materials in Util.GetLinkMaterials(available, 4, 2, 2)
                .OrderByDescending(group => group.Count(card => card.IsCode(EvilKiSikil, EvilLilLa))))
            {
                if (!materials.Any(card => card.HasSetcode(KiSikilSet) || card.HasSetcode(LilLaSet)))
                    continue;
                int positions = FreeLinkedMainZones(Bot.GetMonsters().Except(materials));
                if (positions == 0)
                    continue;
                AI.SelectMaterials(materials, HintMsg.LinkMaterial);
                AI.SelectPlace(positions);
                return true;
            }
            return false;
        }

        private bool PrepareGryphon()
        {
            var field = Bot.GetMonsters();
            var eligible = field.Where(card => card.IsFaceup()
                && !card.IsCode(Nightfall, Sunny, EvilKiSikil, EvilLilLa, Desirae, Caesar)).ToList();
            foreach (var materials in Util.GetLinkMaterials(eligible, 4, 2)
                .Where(group => group.Select(card => card.Alias != 0 ? card.Alias : card.Id).Distinct().Count() == group.Count)
                .OrderBy(group => group.Any(card => card.IsCode(NightPhoto))).ThenBy(group => group.Count))
            {
                int zone = SafeGryphonZone(field.Except(materials).ToList(), out int abaoZone);
                if (zone == 0)
                    continue;
                _abaoReturnZone = abaoZone;
                AI.SelectMaterials(materials, HintMsg.LinkMaterial);
                AI.SelectPlace(zone);
                return true;
            }
            return false;
        }

        private int SafeGryphonZone(IList<ClientCard> remaining, out int abaoReturnZone)
        {
            abaoReturnZone = 0;
            if (!HasSetTrap(Disconnect) || !HasSetTrap(Challenge) || !CanUseSunny()
                || (LinkedZones(remaining) & OpponentMainMonsterZones) != 0)
                return 0;

            ClientCard nightfall = remaining.FirstOrDefault(card => card.IsCode(Nightfall));
            if (nightfall == null || nightfall.Sequence < 0 || nightfall.Sequence >= 5)
                return 0;

            var afterRelease = remaining.Where(card => card != nightfall).ToList();
            int freeZones = FreeLinkedMainZones(remaining);
            int nightfallZone = 1 << nightfall.Sequence;
            int occupiedAfterRelease = afterRelease.Aggregate(0, (zones, card) => zones | (1 << card.Sequence));
            foreach (int sequence in new[] { 2, 1, 3, 0, 4 })
            {
                int zone = 1 << sequence;
                if ((freeZones & zone) == 0)
                    continue;

                // Gryphon in a Main Monster Zone points left and right in its own row.
                int gryphonArrows = HorizontalMainZoneArrows(zone);
                int linksAfterRelease = LinkedZones(afterRelease) | gryphonArrows;
                int occupied = occupiedAfterRelease | zone;
                if (!AbaoIsReturning())
                {
                    if (SupportsGryphonTwins(afterRelease, nightfallZone, linksAfterRelease, occupied))
                        return zone;
                    continue;
                }

                int abaoOptions = linksAfterRelease & Zones.MainMonsterZones & ~(occupied | nightfallZone);
                for (int option = 1; option <= Zones.z4; option <<= 1)
                {
                    if ((abaoOptions & option) == 0 || !SupportsGryphonTwins(afterRelease, nightfallZone,
                        linksAfterRelease | HorizontalMainZoneArrows(option), occupied | option))
                        continue;
                    abaoReturnZone = option;
                    return zone;
                }
            }
            return 0;
        }

        private static int HorizontalMainZoneArrows(int zone)
        {
            return ((zone << 1) | (zone >> 1)) & Zones.MainMonsterZones;
        }

        private static bool SupportsGryphonTwins(IList<ClientCard> afterRelease, int nightfallZone, int links, int occupied)
        {
            return (links & nightfallZone) != 0 && HasTwoZones(links & Zones.MainMonsterZones & ~occupied)
                && afterRelease.All(card => !card.IsCode(Desirae) || (!card.IsDisabled() && HasDesiraeEquip(card)
                    && (links & (1 << card.Sequence)) != 0));
        }

        private int _opponentNightfallTarget;
        private int _opponentNightfallPlace;

        private bool PrepareDrawPhaseNightfall()
        {
            if (Duel.Phase != DuelPhase.Draw)
                return false;
            var field = Bot.MonsterZone.Take(7).Select(PlanCode).ToArray();
            if (!NeedsEarlyNightfallRotation(field) || !NightfallCosts().Any())
                return false;
            field[Array.IndexOf(field, Nightfall)] = 0;
            int blocked = (Enemy.MonsterZone[5] != null ? 1 << 6 : 0)
                | (Enemy.MonsterZone[6] != null ? 1 << 5 : 0);
            var rotation = EarlyNightfallRotation(field, id => Bot.HasInExtra(id), CanUseSunny(), 0, blocked);
            if (rotation == null)
                return false;
            _opponentNightfallTarget = rotation.Summoned;
            _opponentNightfallPlace = 1 << Array.IndexOf(rotation.Field, rotation.Summoned);
            AI.SelectCard(NightfallCosts().ToList());
            return true;
        }

        private int SunnyTwinPlace(int cardId, int available)
        {
            var field = Bot.MonsterZone.Take(7).Select(PlanCode).ToArray();
            if (!field.Contains(Gryphon))
                return 0;
            bool redOnField = field.Contains(EvilKiSikil);
            bool blueOnField = field.Contains(EvilLilLa);
            if (redOnField && blueOnField)
                return 0;
            if (cardId == 0 && (redOnField || blueOnField))
                cardId = redOnField ? EvilLilLa : EvilKiSikil;
            int redZones = 0, blueZones = 0;
            foreach (var pair in LinkedTwinPairs(field))
            {
                redZones |= 1 << Array.IndexOf(pair, EvilKiSikil);
                blueZones |= 1 << Array.IndexOf(pair, EvilLilLa);
            }
            // With no card hint, pick a square that can work for either Twin.
            int zones = available & (cardId == EvilKiSikil || cardId == EvilKiSikil + 1 ? redZones
                : cardId == EvilLilLa || cardId == EvilLilLa + 1 ? blueZones : redZones & blueZones);
            return zones & -zones;
        }

        private bool PrepareOpponentNightfall()
        {
            var field = Bot.MonsterZone.Take(7).Select(PlanCode).ToArray();
            int source = Array.IndexOf(field, Nightfall);
            if (source < 0)
                return false;
            field[source] = 0;
            int blocked = (Enemy.MonsterZone[5] != null ? 1 << 6 : 0)
                | (Enemy.MonsterZone[6] != null ? 1 << 5 : 0);
            if (BluePopUsed() && Bot.HasInExtra(Heist) && HasFreshHeistTarget())
            {
                var heist = OpponentSummons(field, Heist, true, 0, blocked).FirstOrDefault(option =>
                    option.Heist && (!field.Contains(Gryphon) || GryphonPoints(option.Field) != 0));
                if (heist != null)
                {
                    _opponentNightfallTarget = Heist;
                    _opponentNightfallPlace = 1 << Array.IndexOf(heist.Field, Heist);
                    return true;
                }
            }
            if (CanUseSunny() && HasSetTrap(Challenge) && Bot.HasInExtra(Heist) && CanRevivePair(field))
            {
                var sunny = OpponentSummons(field, Sunny, true, 0, blocked).FirstOrDefault(option =>
                {
                    int zone = Array.IndexOf(option.Field, Sunny);
                    return zone < 5 && CanActivateOnField(option.Field, zone)
                        && (!field.Contains(Gryphon) || GryphonPoints(option.Field) != 0);
                });
                if (sunny != null)
                {
                    _opponentNightfallTarget = Sunny;
                    _opponentNightfallPlace = 1 << Array.IndexOf(sunny.Field, Sunny);
                    return true;
                }
            }
            int dealPlace = 0;
            if (source >= 5 && Bot.HasInExtra(Deal) && NightfallCosts().Any())
            {
                bool canReviveGryphon = Bot.Hand.Count > 0 && field.Contains(Abao)
                    && Bot.Graveyard.Any(card => card.IsCode(Gryphon) && card.IsCanRevive());
                var rotation = OpponentSummons(field, Deal, true, 0, blocked).FirstOrDefault(option =>
                    (option.Field[5] == Deal || option.Field[6] == Deal)
                    && (GryphonPoints(option.Field) != 0 || canReviveGryphon && GryphonRevivalZone(option.Field) >= 0));
                if (rotation != null)
                {
                    dealPlace = 1 << Array.IndexOf(rotation.Field, Deal);
                    if (field.Contains(Gryphon))
                    {
                        _opponentNightfallTarget = Deal;
                        _opponentNightfallPlace = dealPlace;
                        return true;
                    }
                }
            }
            var legal = new[] { Sunny, EvilLilLa, Heist, EvilKiSikil, Scout }.Where(id => Bot.HasInExtra(id));
            foreach (int id in legal)
            {
                if (id == Sunny && !CanUseSunny() || id == Heist && !HasFreshHeistTarget())
                    continue;
                foreach (var placement in OpponentSummons(field, id, true, 0, blocked))
                {
                    int zone = Array.IndexOf(placement.Field, id);
                    if (zone < 0 || !CanActivateOnField(placement.Field, zone))
                        continue;
                    if (id == Sunny)
                    {
                        var released = (int[])field.Clone();
                        if (!CanRevivePair(released))
                            continue;
                    }
                    if (id == EvilLilLa && (BluePopUsed() || !placement.Blue) && Bot.HasInExtra(Heist))
                        continue;
                    if (id == Heist && field.Contains(Gryphon) && GryphonPoints(placement.Field) == 0)
                        continue;
                    _opponentNightfallTarget = id;
                    _opponentNightfallPlace = 1 << zone;
                    return true;
                }
            }
            if (dealPlace == 0)
                return false;
            _opponentNightfallTarget = Deal;
            _opponentNightfallPlace = dealPlace;
            return true;
        }

        public override int OnSelectPlace(int cardId, int player, CardLocation location, int available)
        {
            if (player == 0 && location == CardLocation.MonsterZone && (available & _challengeRevivalPlace) != 0
                && Duel.GetCurrentSolvingChainInfo()?.IsActivateCode(Challenge) == true
                && (cardId == 0 || _challengeRevivalTarget?.IsCode(cardId) == true))
                return _challengeRevivalPlace;
            if (player == 0 && location == CardLocation.MonsterZone && _challengePlaces != 0
                && (cardId == 0 || cardId == _challengeLink) && (available & _challengePlaces) != 0)
                return available & _challengePlaces;
            int planned = BoardPlanPlace(cardId, player, location, available);
            if (planned != 0)
                return planned;
            if (player == 0 && location == CardLocation.MonsterZone && _abaoRevivalPlace != 0
                && (cardId == 0 || _abaoReviveTarget?.IsCode(cardId) == true) && (available & _abaoRevivalPlace) != 0)
                return _abaoRevivalPlace;
            if (player == 0 && location == CardLocation.MonsterZone && _opponentNewYearPlace != 0
                && (cardId == 0 || cardId == NewYear) && (available & _opponentNewYearPlace) != 0)
                return _opponentNewYearPlace;
            if (player == 0 && location == CardLocation.MonsterZone && Duel.Player == 1 && _opponentNightfallPlace != 0)
            {
                ChainInfo active = Duel.GetCurrentSolvingChainInfo();
                if (active != null && active.ActivatePlayer == 0 && active.IsActivateCode(Nightfall)
                    && (available & _opponentNightfallPlace) != 0)
                    return _opponentNightfallPlace;
            }
            if (player == 0 && location == CardLocation.MonsterZone && Bot.HasInMonstersZone(Gryphon))
            {
                if (Duel.Player == 0 && Duel.Phase == DuelPhase.End && (cardId == 0 || cardId == Abao)
                    && (available & _abaoReturnZone) != 0 && AbaoIsReturning())
                    return _abaoReturnZone;
                ChainInfo chain = Duel.GetCurrentSolvingChainInfo();
                if (chain != null && chain.ActivatePlayer == 0 && chain.IsActivateCode(Sunny))
                {
                    int place = SunnyTwinPlace(cardId, available);
                    if (place != 0)
                        return place;
                }
                if (chain != null && chain.ActivatePlayer == 0
                    && chain.IsActivateCode(new[] { Sunny, Nightfall, Abao, EvilKiSikil, EvilLilLa, Challenge, NightPhoto, Heist }))
                {
                    int linked = available & FreeLinkedMainZones(Bot.GetMonsters());
                    if (linked != 0)
                        return linked;
                }
            }
            return base.OnSelectPlace(cardId, player, location, available);
        }
    }
}
