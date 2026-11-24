using System.Collections.Generic;
using System.Linq;
using YGOSharp.OCGWrapper.Enums;

namespace WindBot.Game.AI.Decks
{
    public partial class TwinsExecutor
    {
        private ClientCard[] _challengeMaterials;
        private int _challengePlaces;

        private void ClearChallengeLink()
        {
            _challengeLink = _challengePlaces = 0;
            _challengeMaterials = null;
        }

        private IEnumerable<ClientCard> RankDesiraeTargets(IEnumerable<ClientCard> cards)
        {
            return cards.Where(card => card.Controller == 1 && !card.IsShouldNotBeTarget()
                    && !card.IsShouldNotBeMonsterTarget() && !card.HasType(CardType.Token))
                .OrderByDescending(card => card.IsFaceup() && !card.IsDisabled() && card.IsFloodgate())
                .ThenByDescending(card => card.IsFaceup() && !card.IsDisabled() && card.IsMonsterDangerous())
                .ThenByDescending(card => card == Duel.GetCurrentChainCard())
                .ThenByDescending(card => card.Attack);
        }

        private IEnumerable<ClientCard> DesiraeCosts()
        {
            return Bot.Graveyard.Where(card => !card.IsCode(Desirae) && IsLightFiend(card))
                .OrderBy(card => card.IsCode(EvilKiSikil))
                .ThenBy(card => card.IsCode(Engraver));
        }

        private bool CanTradeDesirae(ClientCard card)
        {
            if (!card.IsCode(Desirae) || !card.IsFaceup() || !_desiraeNegateUsed || _desiraeRemovalUsed
                || !RankDesiraeTargets(Enemy.GetMonsters().Concat(Enemy.GetSpells())).Any())
                return false;

            // An equipped LIGHT Fiend goes to the graveyard with Desirae and can pay the trigger's cost.
            return DesiraeCosts().Any() || card.EquipCards.Any(equip => equip.Data != null
                && equip.Data.Race == (int)CardRace.Fiend && equip.Data.Attribute == (int)CardAttribute.Light);
        }

        private bool CanTradeDesiraeAfterDisconnect(ClientCard card)
        {
            var negated = Duel.CurrentChainInfo.LastOrDefault(link => link.ActivatePlayer == 1)?.RelatedCard;
            return CanTradeDesirae(card)
                && RankDesiraeTargets(Enemy.GetMonsters().Concat(Enemy.GetSpells())).Any(target => target != negated);
        }

        private bool DesiraeActivate()
        {
            if (Card.Location == CardLocation.MonsterZone)
                return !UseGryphonResponses() && EnemyChained() && HasVisibleEnemyCard();
            if (Card.Location != CardLocation.Grave || _desiraeRemovalUsed)
                return false;

            var costs = DesiraeCosts().ToList();
            var targets = RankDesiraeTargets(Enemy.GetMonsters().Concat(Enemy.GetSpells())).ToList();
            if (costs.Count == 0 || targets.Count == 0)
                return false;
            AI.SelectCard(costs);
            AI.SelectNextCard(targets);
            return true;
        }

        private IEnumerable<ClientCard> ChallengeDesiraePartners()
        {
            ClientCard desirae = Bot.GetMonsters().FirstOrDefault(CanTradeDesirae);
            return desirae == null ? Enumerable.Empty<ClientCard>() : ChallengeDesiraePartners(desirae);
        }

        private bool CanHoldChallengeForDesirae()
        {
            if (_desiraeNegateUsed || _desiraeRemovalUsed || !Bot.HasInExtra(Heist))
                return false;
            var field = Bot.MonsterZone.Take(7).Select(PlanCode).ToArray();
            return Bot.GetMonsters().Any(card => card.IsCode(Desirae) && card.IsFaceup()
                && !card.IsDisabled() && HasDesiraeEquip(card) && CanActivateOnField(field, card.Sequence)
                && ChallengeDesiraePartners(card).Any());
        }

        private IEnumerable<ClientCard> ChallengeDesiraePartners(ClientCard desirae)
        {
            return Bot.Graveyard.Where(card => card.IsCanRevive() && !card.IsCode(EvilKiSikil, EvilLilLa)
                && (card.HasSetcode(KiSikilSet) || card.HasSetcode(LilLaSet)))
                .Where(card =>
                {
                    int link = ChallengeLinkFor(card);
                    return link != 0 && ChallengeDesiraeZones(desirae, null, link) != 0;
                });
        }

        private bool PrepareChallengeDesiraeLink()
        {
            ClientCard desirae = Bot.GetMonsters().FirstOrDefault(CanTradeDesirae);
            if (desirae == null)
                return false;
            foreach (ClientCard partner in Bot.GetMonsters().Where(card => card != desirae && card.IsFaceup()
                && !card.IsCode(Nightfall, Gryphon, Sunny, EvilKiSikil, EvilLilLa)))
            {
                int link = ChallengeLinkFor(partner);
                if (link == Heist && Duel.Player == 1 && !HasFreshHeistTarget()
                    && Enemy.GetMonsters().Concat(Enemy.GetSpells()).Any(HasPendingRemoval))
                    continue;
                if (link == 0)
                    continue;
                int zones = ChallengeDesiraeZones(desirae, partner, link);
                if (zones == 0)
                    continue;
                SelectChallengeLink(link, desirae, partner, zones);
                return true;
            }
            return false;
        }

        private void SelectChallengeLink(int link, ClientCard first, ClientCard partner, int zones)
        {
            _challengeLink = link;
            _challengeMaterials = new[] { first, partner };
            _challengePlaces = zones;
            AI.SelectCard(link);
            AI.SelectMaterials(_challengeMaterials, HintMsg.LinkMaterial);
            AI.SelectPlace(zones);
        }

        private bool PrepareChallengeHeist()
        {
            if (!CanUseBlueForChallenge() || !HasFreshHeistTarget())
                return false;
            ClientCard blue = Bot.GetMonsters().FirstOrDefault(card => card.IsCode(EvilLilLa) && card.IsFaceup());
            if (blue == null)
                return false;
            foreach (ClientCard partner in Bot.GetMonsters().Where(card => card.IsFaceup() && ChallengeHeistPartner(card)))
            {
                int zones = ChallengeDesiraeZones(blue, partner, Heist);
                int mainZones = zones & Zones.MainMonsterZones;
                if (zones == 0 || mainZones == 0 && Bot.HasInMonstersZone(Gryphon, notDisabled: true, faceUp: true))
                    continue;
                SelectChallengeLink(Heist, blue, partner, mainZones != 0 ? mainZones : zones);
                return true;
            }
            return false;
        }

        private bool CanUseBlueForChallenge() => Bot.HasInExtra(Heist) && (_planUsed & Flag(PlanEffect.BluePop)) != 0;

        private static bool ChallengeHeistPartner(ClientCard card) => LinkRating(PlanCode(card)) == 0
            && (card.HasSetcode(KiSikilSet) || card.HasSetcode(LilLaSet));

        private int ChallengeLinkFor(ClientCard partner)
        {
            if (Bot.HasInExtra(Heist) && (partner.HasSetcode(0x1151) || partner.HasSetcode(EvilTwinSet)))
                return Heist;
            if (Bot.HasInExtra(EvilKiSikil) && partner.HasSetcode(KiSikilSet))
                return EvilKiSikil;
            if (Bot.HasInExtra(EvilLilLa) && partner.HasSetcode(LilLaSet))
                return EvilLilLa;
            return 0;
        }

        private int ChallengeDesiraeZones(ClientCard desirae, ClientCard partner, int link)
        {
            var remaining = Bot.GetMonsters().Where(card => card != desirae && card != partner).ToList();
            if (!Duel.IsNewRule)
                return Zones.MainMonsterZones & ~remaining.Aggregate(0, (occupied, card) => occupied | (1 << card.Sequence));
            int zones = FreeLinkedMainZones(remaining);
            if (remaining.Any(card => card.Sequence >= 5))
                return zones;

            // Desirae can release an Extra Monster Zone as material. Gryphon still
            // requires the new Link to be linked before it can use its trigger.
            var field = new int[7];
            bool locked = remaining.Any(card => card.IsCode(Gryphon) && card.IsFaceup() && !card.IsDisabled());
            foreach (ClientCard card in remaining)
                if (card.IsFaceup())
                    field[card.Sequence] = PlanCode(card);
            for (int zone = 5; zone < 7; zone++)
            {
                if (Enemy.MonsterZone[11 - zone] != null)
                    continue;
                field[zone] = link;
                if (!locked || CanActivateOnField(field, zone))
                    zones |= 1 << zone;
                field[zone] = 0;
            }
            return zones;
        }
    }
}
