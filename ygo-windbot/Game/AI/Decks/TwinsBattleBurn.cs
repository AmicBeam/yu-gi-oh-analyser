using System.Collections.Generic;
using System.Linq;
using YGOSharp.OCGWrapper.Enums;

namespace WindBot.Game.AI.Decks
{
    public partial class TwinsExecutor
    {
        private const int LacrimaBurnDamage = 1200;
        private bool _requiemSummoned;
        private bool _lacrimaBurnUsed;
        private IList<ClientCard> _burnFusionMaterials;

        private bool BurnWindow()
        {
            return Duel.Player == 0 && (Duel.Phase == DuelPhase.Main1 || Duel.Phase == DuelPhase.Main2)
                && Enemy.LifePoints > 0 && Enemy.LifePoints <= LacrimaBurnDamage && !_lacrimaBurnUsed
                && !(Duel.Phase == DuelPhase.Main1 && Duel.MainPhase.CanBattlePhase && EnterBattleForCurrentBoard());
        }

        private static bool BurnLightFiend(ClientCard card)
        {
            return card.Data != null && card.Data.Race == (int)CardRace.Fiend
                && card.Data.Attribute == (int)CardAttribute.Light;
        }

        private bool ActivateLacrimaBurn()
        {
            if (Card.Location != CardLocation.Grave || _lacrimaBurnUsed
                || Enemy.LifePoints <= 0 || Enemy.LifePoints > LacrimaBurnDamage)
                return false;
            var costs = Bot.Graveyard.Where(card => card != Card && BurnLightFiend(card)).ToList();
            if (costs.Count == 0)
                return false;
            AI.SelectCard(costs);
            return true;
        }

        private bool LacrimaFusionActivate()
        {
            if (Card.Location != CardLocation.MonsterZone)
                return Duel.Player == 1;
            if (BurnWindow() && CanPlanLacrimaBurn(CurrentBattlePosition()))
                return false;
            AI.SelectCard(Engraver, Deal, EvilKiSikil, Requiem);
            return true;
        }

        private IList<ClientCard> BurnLinkMaterials(int link, IList<ClientCard> field, IList<ClientCard> grave,
            ClientCard lacrima)
        {
            if (!Bot.HasInExtra(link))
                return null;
            bool graveCost = grave.Any(card => !card.IsCode(LacrimaFusion) && BurnLightFiend(card));
            if (link == Requiem)
                return !_requiemSummoned && graveCost && RotationExtraZones(field.Where(card => card != lacrima).ToList()) != 0
                    ? new[] { lacrima } : null;

            foreach (ClientCard partner in field.Where(card => card != lacrima && card.IsFaceup()))
            {
                bool validPartner = link == Heist
                    ? partner.HasSetcode(0x1151) || partner.HasSetcode(EvilTwinSet)
                    : partner.HasSetcode(link == EvilKiSikil ? KiSikilSet : LilLaSet);
                if (validPartner && (graveCost || BurnLightFiend(partner))
                    && RotationExtraZones(field.Where(card => card != lacrima && card != partner).ToList()) != 0)
                    return new[] { lacrima, partner };
            }
            return null;
        }

        private ClientCard BurnEngraverEquip(IList<ClientCard> field, IList<ClientCard> lostEquips)
        {
            if (!BattleClearUnused(Engraver, 1) || !field.Any(card => card.IsCode(Engraver) && RotationAllowed(card)))
                return null;
            return Bot.SpellZone.FirstOrDefault(card => card != null && card.IsFaceup()
                && card.IsCode(Requiem, Sequence) && !lostEquips.Contains(card));
        }

        private bool CanSendLacrima(IList<ClientCard> field, IList<ClientCard> grave,
            IList<ClientCard> lostEquips, ClientCard lacrima)
        {
            return BurnEngraverEquip(field, lostEquips) != null
                || new[] { Requiem, Heist, EvilKiSikil, EvilLilLa }
                    .Any(link => BurnLinkMaterials(link, field, grave, lacrima) != null);
        }

        private IList<ClientCard> BurnFusionMaterials(IList<ClientCard> field, IList<ClientCard> grave,
            IList<ClientCard> lostEquips)
        {
            int zone = BattleRevivalZone(field);
            if (zone == 0 || _sequenceFusionUsed || !Bot.HasInExtra(LacrimaFusion))
                return null;
            int sequence = Enumerable.Range(0, 5).First(i => zone == (1 << i));
            var lacrima = new ClientCard(LacrimaFusion, CardLocation.MonsterZone, sequence, (int)CardPosition.FaceUpAttack);
            var afterFusion = field.Concat(new[] { lacrima }).ToList();
            var lights = grave.Where(BurnLightFiend).ToList();
            for (int i = 0; i < lights.Count; ++i)
            {
                for (int j = i + 1; j < lights.Count; ++j)
                {
                    var materials = new[] { lights[i], lights[j] };
                    if (CanSendLacrima(afterFusion, grave.Except(materials).ToList(), lostEquips, lacrima))
                        return materials;
                }
            }
            return null;
        }

        private IList<ClientCard> BurnSequenceMaterials(BattlePosition position)
        {
            if (_sequenceFusionUsed || !Bot.HasInExtra(Sequence) || !Bot.HasInExtra(LacrimaFusion))
                return null;
            var monsters = position.Field.Where(card => card.IsFaceup()
                && !card.IsCode(Nightfall, Sunny, Gryphon, Caesar, Desirae)).ToList();
            for (int i = 0; i < monsters.Count; ++i)
            {
                for (int j = i + 1; j < monsters.Count; ++j)
                {
                    var materials = new[] { monsters[i], monsters[j] };
                    if (!materials.Any(BurnLightFiend))
                        continue;
                    var remaining = position.Field.Except(materials).ToList();
                    int zones = RotationExtraZones(remaining);
                    if (zones == 0)
                        continue;
                    int sequence = Enumerable.Range(0, 7).First(z => (zones & (1 << z)) != 0);
                    remaining.Add(new ClientCard(Sequence, CardLocation.MonsterZone, sequence, (int)CardPosition.FaceUpAttack));
                    if (BurnFusionMaterials(remaining, position.Grave.Concat(materials).ToList(), position.LostEquips) != null)
                        return materials;
                }
            }
            return null;
        }

        private bool CanPlanLacrimaBurn(BattlePosition position)
        {
            if (_lacrimaBurnUsed)
                return false;
            var lacrima = position.Field.FirstOrDefault(card => card.IsCode(LacrimaFusion) && card.IsFaceup());
            if (lacrima != null)
                return CanSendLacrima(position.Field, position.Grave, position.LostEquips, lacrima);
            if (position.Field.Any(card => card.IsCode(Sequence) && card.IsFaceup() && RotationAllowed(card))
                && BurnFusionMaterials(position.Field, position.Grave, position.LostEquips) != null)
                return true;
            return BurnSequenceMaterials(position) != null;
        }

        private bool ActivateSequenceForBurn()
        {
            if (!BurnWindow() || Card.Location != CardLocation.MonsterZone || !MatchesEffect(Sequence, 0))
                return false;
            var position = CurrentBattlePosition();
            _burnFusionMaterials = BurnFusionMaterials(position.Field, position.Grave, position.LostEquips);
            if (_burnFusionMaterials == null)
                return false;
            _sequenceFusion = LacrimaFusion;
            return true;
        }

        private bool PrepareSequenceForBurn()
        {
            if (!BurnWindow() || Bot.HasInMonstersZone(LacrimaFusion, faceUp: true))
                return false;
            var position = CurrentBattlePosition();
            var materials = BurnSequenceMaterials(position);
            if (materials == null)
                return false;
            AI.SelectMaterials(materials, HintMsg.LinkMaterial);
            AI.SelectPlace(RotationExtraZones(position.Field.Except(materials).ToList()));
            return true;
        }

        private bool PrepareLacrimaBurnLink()
        {
            if (!BurnWindow())
                return false;
            var field = Bot.GetMonsters();
            var lacrima = field.FirstOrDefault(card => card.IsCode(LacrimaFusion) && card.IsFaceup());
            if (lacrima == null)
                return false;
            int link = Card.IsCode(EvilKiSikil) ? EvilKiSikil : Card.IsCode(EvilLilLa) ? EvilLilLa : Card.Id;
            var materials = BurnLinkMaterials(link, field, Bot.Graveyard, lacrima);
            if (materials == null)
                return false;
            AI.SelectMaterials(materials, HintMsg.LinkMaterial);
            AI.SelectPlace(RotationExtraZones(field.Except(materials).ToList()));
            return true;
        }

        private bool SendLacrimaWithEngraver()
        {
            if (!BurnWindow() || Card.Location != CardLocation.MonsterZone || !MatchesEffect(Engraver, 1))
                return false;
            var lacrima = Bot.GetMonsters().FirstOrDefault(card => card.IsCode(LacrimaFusion) && card.IsFaceup());
            var equip = BurnEngraverEquip(Bot.GetMonsters(), new List<ClientCard>());
            if (lacrima == null || equip == null)
                return false;
            AI.SelectCard(equip);
            AI.SelectNextCard(lacrima);
            return true;
        }
    }
}
