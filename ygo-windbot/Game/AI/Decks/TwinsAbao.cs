using System.Collections.Generic;
using System.Linq;
using YGOSharp.OCGWrapper.Enums;

namespace WindBot.Game.AI.Decks
{
    public partial class TwinsExecutor
    {
        private ClientCard _abaoReviveTarget;
        private ClientCard _returningAbao;
        private int _abaoReturnZone;
        private int _abaoRevivalPlace;
        private bool _twinTrapActivated;
        private bool _abaoReloadNightfall;

        private List<int> NightfallReloadTraps()
        {
            var missing = new[] { Disconnect, Challenge }.Where(id => !HasSetTrap(id)).ToList();
            if (Bot.SpellZone.Take(5).Count(card => card == null) < missing.Count
                || missing.Any(id => !Bot.HasInGraveyard(id) && !Bot.HasInDeck(id)))
                missing.Clear();
            return missing;
        }

        private bool TryAbaoReloadNightfall()
        {
            if (Duel.Player != 1 || (Duel.Phase != DuelPhase.Main1 && Duel.Phase != DuelPhase.Main2)
                || CurrentTiming < 0 || (CurrentTiming & MainPhaseEndTiming) == 0
                || !_twinTrapActivated || _nightfallEffectUsed || Bot.HasInMonstersZone(Nightfall)
                || Duel.CurrentChainInfo.Any(chain => chain.ActivatePlayer == 0 && chain.IsActivateCode(Nightfall))
                || NightfallReloadTraps().Count == 0)
                return false;

            var nightfall = Bot.Graveyard.FirstOrDefault(card => card.IsCode(Nightfall) && card.IsCanRevive());
            if (nightfall == null)
                return false;

            var remaining = Bot.GetMonsters().Where(card => card != Card).ToList();
            var opposing = Enemy.GetMonsters();
            int incoming = LinkedZones(remaining) | (LinkedZones(opposing) >> 16);
            int occupied = remaining.Aggregate(0, (zones, card) => zones | (1 << card.Sequence));
            occupied |= opposing.Aggregate(0, (zones, card) => zones | (1 << (card.Sequence + 16)));
            bool locked = remaining.Concat(opposing).Any(card => card.IsCode(Gryphon) && card.IsFaceup() && !card.IsDisabled());
            for (int zone = 0; zone < 5; zone++)
            {
                int place = 1 << zone;
                if ((occupied & place) != 0 || locked && (incoming & place) == 0 && (Arrows(Nightfall, zone) & occupied) == 0)
                    continue;

                _abaoReloadNightfall = true;
                _abaoReviveTarget = nightfall;
                _abaoRevivalPlace = place;
                AI.SelectCard(AbaoDiscards(Bot.Hand).ToList());
                AI.SelectNextCard(nightfall);
                return true;
            }
            return false;
        }

        private bool AbaoIsReturning()
        {
            return _returningAbao != null && Bot.Banished.Contains(_returningAbao);
        }

        private bool CanStartAbaoRoute()
        {
            return Duel.Player == 0 && !Bot.HasInMonstersZone(Nightfall)
                && Bot.HasInExtra(Abao) && Bot.HasInExtra(Nightfall)
                && Bot.Hand.Count > 0 && AbaoStarter(Bot.Graveyard) != null;
        }

        private ClientCard AbaoStarter(IEnumerable<ClientCard> cards)
        {
            if (BattleClearUnused(KiSikil, 0) && (Bot.HasInDeck(LilLa, Treat, Sweet)
                || Bot.Hand.Any(card => card.IsCode(LilLa, Treat, Sweet))))
            {
                ClientCard red = cards.FirstOrDefault(card => card.IsCode(KiSikil));
                if (red != null)
                    return red;
            }

            return BattleClearUnused(LilLa, 0) && (Bot.HasInDeck(KiSikil, Frost, NewYear)
                || Bot.Hand.Any(card => card.IsCode(KiSikil, Frost, NewYear)))
                ? cards.FirstOrDefault(card => card.IsCode(LilLa)) : null;
        }

        private bool PrepareLilLaForAbao()
        {
            if (!CanStartAbaoRoute() || !Bot.HasInExtra(Yama))
                return false;

            var field = Bot.GetMonsters();
            if (field.Count != 4 || field.Any(card => !card.IsFaceup()))
                return false;
            ClientCard engraver = field.FirstOrDefault(card => card.IsCode(Engraver));
            ClientCard twinLink = field.FirstOrDefault(card => card.IsCode(Scout, Deal));
            if (engraver == null || twinLink == null)
                return false;

            var materials = field.Where(card => card != engraver && card != twinLink).ToList();
            if (materials.Any(card => card.IsCode(Gryphon, Sunny, Caesar, Desirae, EvilKiSikil, EvilLilLa))
                || !materials.Any(card => card.HasSetcode(LilLaSet)))
                return false;

            AI.SelectMaterials(materials, HintMsg.LinkMaterial);
            return true;
        }

        private IList<ClientCard> YamaMaterialsForAbao()
        {
            if (!CanStartAbaoRoute())
                return null;

            var field = Bot.GetMonsters();
            if (field.Count != 3 || !field.Any(card => card.IsFaceup() && card.IsCode(EvilLilLa)))
                return null;
            ClientCard engraver = field.FirstOrDefault(card => card.IsFaceup() && card.IsCode(Engraver));
            ClientCard twinLink = field.FirstOrDefault(card => card.IsFaceup() && card.IsCode(Scout, Deal));
            if (engraver == null || twinLink == null)
                return null;

            return new[] { engraver, twinLink };
        }

        private bool PrepareYamaForAbao()
        {
            var materials = YamaMaterialsForAbao();
            if (materials == null)
                return false;
            AI.SelectMaterials(materials, HintMsg.LinkMaterial);
            return true;
        }

        private bool PrepareAbaoRestart()
        {
            if (!CanStartAbaoRoute())
                return false;

            var field = Bot.GetMonsters();
            if (field.Count != 2 || !field.Any(card => card.IsFaceup() && card.IsCode(EvilLilLa))
                || !field.Any(card => card.IsFaceup() && card.IsCode(Yama)))
                return false;

            AI.SelectMaterials(field, HintMsg.LinkMaterial);
            return true;
        }

        private bool AbaoActivate()
        {
            if (MatchesEffect(Abao, 1))
                return Duel.Player == 0;
            if (!MatchesEffect(Abao, 0) || DefaultCheckWhetherCardIsNegated(Card) || Bot.Hand.Count == 0)
                return false;
            if (TryAbaoReloadNightfall())
                return true;

            bool newYearCycle = Duel.Player == 1 && (_waitingForNewYearCycle || CanStartNewYearTwinCycle());
            bool passwordPending = !_searchLocked && (Bot.HasInDeck(Password) || Bot.HasInGraveyard(Password))
                && (!_newYearSearched || Duel.CurrentChainInfo.Any(link => link.ActivatePlayer == 0
                    && link.IsActivateCode(NewYear)));
            if (newYearCycle && passwordPending && !Duel.ChainTargets.Contains(Card))
                return false;
            if (newYearCycle && !Duel.ChainTargets.Contains(Card) && CanWaitForGryphonRotation())
                return false;
            bool armedDesirae = Duel.Player == 1 && Bot.GetMonsters().Any(card =>
                card.IsCode(Desirae) && card.IsFaceup() && HasDesiraeEquip(card));
            if (armedDesirae && !Duel.ChainTargets.Contains(Card)
                && Bot.Graveyard.Any(card => card.IsCode(Gryphon) && card.IsCanRevive())
                && (CanWaitForGryphonRotation() || CanUseSunny()))
                return false;
            var discards = AbaoDiscards(Bot.Hand).ToList();
            var removals = Enemy.GetMonsters().Concat(Enemy.GetSpells()).OrderByDescending(AbaoRemovalValue).ToList();
            var revivals = Bot.MonsterZone.Take(5).Any(card => card == null || card == Card)
                ? Bot.Graveyard.Where(card => card.IsCanRevive()
                    && ((CardAttribute)card.Attribute & (CardAttribute.Light | CardAttribute.Dark)) != 0).ToList()
                : new List<ClientCard>();
            ClientCard starter = Duel.Player == 0 && Bot.GetMonsters().Count == 1 ? AbaoStarter(revivals) : null;
            ClientCard revive = starter ?? revivals.OrderByDescending(AbaoRevivalValue).FirstOrDefault();
            ClientCard remove = removals.FirstOrDefault();
            int reviveValue = starter != null ? 8 : AbaoRevivalValue(revive);
            int removeValue = AbaoRemovalValue(remove);
            if (newYearCycle && !_nightfallEffectUsed && Bot.HasInMonstersZone(Nightfall) && Bot.HasInExtra(Heist)
                && HasFreshHeistTarget()
                && removals.Count(card => card.IsFaceup() && !card.IsDisabled()) == 1 && !GryphonThreat())
                removeValue = 0;
            if (reviveValue > 0 && EnemyChained() && Duel.CurrentChainInfo.LastOrDefault()?.Targets.Contains(Card) == true)
                reviveValue += 3;

            if (System.Math.Max(reviveValue, removeValue) <= AbaoHandValue(discards[0]))
                return false;

            _abaoReviveTarget = reviveValue >= removeValue ? revive : null;
            if (_abaoReviveTarget?.IsCode(Gryphon) == true)
                _abaoRevivalPlace = 1 << GryphonRevivalZone(Bot.MonsterZone.Select(PlanCode).ToArray());
            AI.SelectCard(discards);
            if (_abaoReviveTarget != null)
                AI.SelectNextCard(_abaoReviveTarget);
            _abaoRemovalTarget = _abaoReviveTarget == null ? remove : null;
            return true;
        }

        private bool CanWaitForGryphonRotation()
        {
            if (!Bot.Graveyard.Any(card => card.IsCode(Gryphon) && card.IsCanRevive()))
                return false;
            if (Duel.CurrentChainInfo.Any(link => link.ActivatePlayer == 0 && link.IsActivateCode(Nightfall)))
                return true;
            if (_nightfallEffectUsed || !Bot.HasInExtra(Deal) || !NightfallCosts().Any())
                return false;
            var field = Bot.MonsterZone.Select(PlanCode).ToArray();
            int nightfall = System.Array.IndexOf(field, Nightfall);
            if (nightfall < 5 || !CanActivateOnField(field, nightfall))
                return false;
            field[nightfall] = Deal;
            return GryphonRevivalZone(field) >= 0;
        }

        private int AbaoRemovalValue(ClientCard card)
        {
            if (card == null || !card.IsFaceup() || card.IsDisabled())
                return 0;
            // Destroying a Normal or Quick-Play Spell won't stop its effect on the chain.
            if (card == Duel.GetCurrentChainCard() && card.IsOnField()
                && (card.HasType(CardType.Continuous) || card.HasType(CardType.Field) || card.HasType(CardType.Equip)))
                return GryphonThreat() ? 8 : 5;
            if (card.IsFloodgate())
                return 6;
            if (card.Location == CardLocation.MonsterZone && !card.IsMonsterInvincible()
                && (card.IsMonsterDangerous() || card.Attack >= 2500))
                return 4;
            return 0;
        }

        private int AbaoRevivalValue(ClientCard card)
        {
            if (card == null || (Bot.HasInMonstersZone(Gryphon, notDisabled: true, faceUp: true)
                && FreeLinkedMainZones(Bot.GetMonsters().Where(monster => monster != Card)) == 0))
                return 0;
            if (card.IsCode(Gryphon) && Duel.Player == 1)
            {
                bool waitingForTwins = CanUseSunny() || _waitingForNewYearCycle && (_planUsed & Flag(PlanEffect.BluePop)) == 0
                    || Duel.CurrentChainInfo.Any(chain => chain.ActivatePlayer == 0
                    && (chain.IsActivateCode(Nightfall) || chain.IsActivateCode(Sunny)));
                if (waitingForTwins || GryphonRevivalZone(Bot.MonsterZone.Select(PlanCode).ToArray()) < 0)
                    return 0;
                // Challenge can revive a Twin independently of Abao's lock.
                return HasSetTrap(Challenge) ? 7 : 6;
            }
            if (card.IsCode(EvilLilLa) && HasVisibleEnemyCard()
                && Bot.GetMonsters().Any(monster => monster.IsFaceup() && monster.HasSetcode(KiSikilSet)))
                return 6;
            if (card.IsCode(Nightfall) && Duel.Player == 0 && !_nightfallEffectUsed
                && (_planUsed & (Flag(PlanEffect.Nightfall) | Flag(PlanEffect.NightfallRotate))) == 0
                && CanSetTwinTrap() && !Bot.HasInMonstersZone(Nightfall))
                return 6;
            if (card.IsCode(Heist) && (Duel.Player == 1 ? HasFreshHeistTarget() : HasVisibleEnemyCard()))
            {
                // Spend the revival while Heist has a target; Sunny and Challenge
                // can handle the other two Links without Abao.
                return Duel.Player == 1 && CanUseSunny() && HasSetTrap(Challenge) ? 7 : 5;
            }
            if (card.IsCode(EvilKiSikil)
                && Bot.GetMonsters().Any(monster => monster.IsFaceup() && monster.HasSetcode(LilLaSet)))
                return 4;
            return 0;
        }

        private static bool IsSpareTwinForAbao(int id, int copies, bool inGrave, ulong used, bool opponentTurn)
        {
            if (id == Password)
                return copies > 1 || opponentTurn || (used & Flag(PlanEffect.Password)) != 0;
            if (id == Trouble)
                return copies > 1 || (used & Flag(PlanEffect.Trouble)) != 0;
            return IsTwin(id) && (copies > 1 || id == Frost && inGrave);
        }

        private static int AbaoDiscardPriority(int id, bool spareTwin, bool opponentTurn = false)
        {
            switch (id)
            {
                case Fuwalos: return 2;
                case _CardId.InfiniteImpermanence: return opponentTurn ? 3 : 10;
                case Crossout: return 5;
                case Droll:
                case 94145022: return 6;
                case Nibiru: return 7;
                case _CardId.EffectVeiler:
                case 97268401: return 8;
                case _CardId.GhostOgreAndSnowRabbit: return 9;
                case _CardId.AshBlossom:
                case 14558128: return 11;
                case _CardId.MaxxC: return 12;
            }
            var card = YGOSharp.OCGWrapper.NamedCard.Get(id);
            if (card?.HasSetcode(0x1b0) == true)
                return 0;
            if (card?.HasType(CardType.Trap) == true)
                return 1;
            if (spareTwin)
                return 4;
            return 13;
        }

        private IOrderedEnumerable<ClientCard> AbaoDiscards(IEnumerable<ClientCard> cards)
        {
            return cards.OrderBy(LiveAbaoDiscardPriority);
        }

        private int LiveAbaoDiscardPriority(ClientCard card)
        {
            int id = PlanCode(card);
            bool opponentTurn = Duel.Player == 1;
            return AbaoDiscardPriority(id, IsSpareTwinForAbao(id, Bot.Hand.Count(c => PlanCode(c) == id),
                Bot.HasInGraveyard(id), _planUsed, opponentTurn), opponentTurn);
        }

        private ClientCard PlannedAbaoDiscard(IEnumerable<ClientCard> cards, int id)
        {
            var best = AbaoDiscards(cards).FirstOrDefault();
            if (best == null || id == 0)
                return best;
            int priority = LiveAbaoDiscardPriority(best);
            return cards.FirstOrDefault(card => LiveAbaoDiscardPriority(card) == priority
                && (id == TableInertDiscard ? IsTableInert(PlanCode(card)) : PlanCode(card) == id));
        }

        private static IEnumerable<int> AbaoDiscards(PlanPosition p)
        {
            return p.Hand.Distinct().OrderBy(id => AbaoDiscardPriority(id,
                IsSpareTwinForAbao(id, p.Hand.Count(card => card == id), p.Grave.Contains(id), p.Used, false)));
        }

        private int AbaoHandValue(ClientCard card)
        {
            if (Bot.Hand.Count(other => other.IsCode(card.Id)) > 1)
                return 1;
            if (card.IsCode(Password) && Duel.Player == 1)
                return 2;
            if (card.IsCode(Engraver, Sweet, Frost) && !Bot.HasInGraveyard(card.Id))
                return 2;
            if (card.IsCode(_CardId.MaxxC, Fuwalos, Crossout, Droll, Nibiru, _CardId.AshBlossom,
                _CardId.EffectVeiler, _CardId.GhostOgreAndSnowRabbit, _CardId.InfiniteImpermanence,
                Disconnect, Challenge, NightPhoto, NewYear))
                return 5;
            return 3;
        }
    }
}
