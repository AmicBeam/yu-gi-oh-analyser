using System.Collections.Generic;
using System.Linq;
using YGOSharp.OCGWrapper.Enums;

namespace WindBot.Game.AI.Decks
{
    public partial class TwinsExecutor
    {
        private const int Diabellstar = 72270339;
        private bool _gryphonLethal;
        private int _gryphonMainZone;

        private ClientCard LiveMonster(int code) => Bot.GetMonsters().FirstOrDefault(card => card.IsCode(code));
        private ClientCard Revivable(int code) => Bot.Graveyard.FirstOrDefault(card => card.IsCode(code) && card.IsCanRevive());

        private void RegisterGryphonLethal()
        {
            AddExecutor(ExecutorType.SpSummon, EvilLilLa, StartGryphonLethal);
            AddExecutor(ExecutorType.Activate, EvilLilLa, GryphonLethalBlue);
            AddExecutor(ExecutorType.Activate, EvilKiSikil, GryphonLethalRed);
            AddExecutor(ExecutorType.SpSummon, Frost, () => GryphonLethalMain() && LiveMonster(EvilKiSikil) != null);
            AddExecutor(ExecutorType.SpSummon, Gryphon, PrepareGryphonLethalLink);
            AddExecutor(ExecutorType.Activate, Gryphon, PayForGryphonLethal);
            AddExecutor(ExecutorType.SpSummon, Nightfall, PrepareGryphonLethalNightfall);
            AddExecutor(ExecutorType.GoToBattlePhase, () => GryphonLethalMain()
                && LiveMonster(Nightfall) != null && LiveMonster(Gryphon) != null
                && CanFinishWithNightfall(LiveMonster(Nightfall)));
        }

        private bool GryphonLethalMain() => _gryphonLethal && OwnMainBeforeBattle();

        private int GryphonLethalExtraZone()
        {
            int zones = RotationExtraZones(Bot.GetMonsters()
                .Where(card => !card.IsCode(NewYear, Sweet)).ToList());
            return zones & (Zones.z5 | Zones.z6);
        }

        private bool StartGryphonLethal()
        {
            if (_gryphonLethal || !UsesSecondTurnModel || !OwnMainBeforeBattle() || Duel.MainPhase?.CanBattlePhase != true
                || Enemy.LifePoints > 8000 || !Bot.HasInHand(Frost) || !Bot.HasInHand(Disconnect)
                || !Bot.HasInExtra(Gryphon) || !Bot.HasInExtra(Nightfall) || !Bot.HasInExtra(Sunny)
                || Revivable(EvilKiSikil) == null || Bot.GetMonsters().Count != 3
                || LiveMonster(NewYear) == null || LiveMonster(Sweet) == null || LiveMonster(LilLa) == null
                || Enemy.GetMonsters().Count != 1 || !Enemy.GetMonsters()[0].IsCode(Diabellstar)
                || Enemy.GetSpells().Count > 1 || !Bot.Graveyard.Any(card => card.IsCode(Password)))
                return false;
            int available = GryphonLethalExtraZone();
            if (available == 0) return false;

            // Anchor the two Link-4s to the same column. Big Blue's left arrow
            // links it to Big Red when it returns to the adjacent Main Zone.
            _gryphonMainZone = (available & Zones.z6) != 0 ? Zones.z3 : Zones.z1;
            _gryphonLethal = true;
            AI.SelectMaterials(new List<ClientCard> { LiveMonster(NewYear), LiveMonster(Sweet) }, HintMsg.LinkMaterial);
            AI.SelectPlace((available & Zones.z6) != 0 ? Zones.z6 : Zones.z5);
            return true;
        }

        private bool GryphonLethalBlue()
        {
            if (!GryphonLethalMain()) return false;
            if ((MatchesEffect(EvilLilLa, 1) || MatchesEffect(Card.Id, 1)) && LiveMonster(EvilKiSikil) == null && Revivable(EvilKiSikil) != null)
            {
                AI.SelectCard(Revivable(EvilKiSikil));
                AI.SelectPlace(_gryphonMainZone);
                return true;
            }
            ClientCard target = Enemy.GetMonsters().FirstOrDefault(card => card.IsCode(Diabellstar));
            if ((MatchesEffect(EvilLilLa, 0) || MatchesEffect(Card.Id, 0) || ActivateDescription < 0) && LiveMonster(Gryphon) != null && target != null)
            {
                AI.SelectCard(target);
                return true;
            }
            return false;
        }

        private bool GryphonLethalRed()
        {
            if (!GryphonLethalMain()) return false;
            if (MatchesEffect(EvilKiSikil, 0) || MatchesEffect(Card.Id, 0) || ActivateDescription < 0 && LiveMonster(Gryphon) == null)
                return true;
            if (!(MatchesEffect(EvilKiSikil, 1) || MatchesEffect(Card.Id, 1)) ||
                LiveMonster(Gryphon) == null || Revivable(EvilLilLa) == null || Duel.CurrentChainInfo.Count != 0)
                return false;
            AI.SelectCard(Revivable(EvilLilLa));
            AI.SelectPlace(_gryphonMainZone << 1);
            return true;
        }

        private bool PrepareGryphonLethalLink()
        {
            if (!GryphonLethalMain()) return false;
            var materials = new[] { LiveMonster(EvilLilLa), LiveMonster(LilLa), LiveMonster(Frost) };
            if (materials.Any(card => card == null) || LiveMonster(EvilKiSikil)?.Sequence !=
                (_gryphonMainZone == Zones.z3 ? 3 : 1))
                return false;
            AI.SelectMaterials(materials.ToList(), HintMsg.LinkMaterial);
            AI.SelectPlace(_gryphonMainZone == Zones.z3 ? Zones.z6 : Zones.z5);
            return true;
        }

        private bool PayForGryphonLethal()
        {
            if (!GryphonLethalMain() || !Bot.HasInHand(Disconnect) ||
                !Bot.Graveyard.Any(card => card.IsCode(Password)) || LiveMonster(EvilKiSikil) == null)
                return false;
            AI.SelectCard(Disconnect);
            AI.SelectNextCard(Password);
            return true;
        }

        private bool PrepareGryphonLethalNightfall()
        {
            if (!GryphonLethalMain() || LiveMonster(Gryphon) == null || LiveMonster(EvilKiSikil) == null ||
                LiveMonster(EvilLilLa) == null || !Bot.Graveyard.Any(card => card.IsCode(Disconnect)) ||
                Enemy.GetMonsters().Any(card => !card.IsCode(Diabellstar)))
                return false;
            AI.SelectMaterials(new List<ClientCard> { LiveMonster(EvilKiSikil), LiveMonster(EvilLilLa) }, HintMsg.LinkMaterial);
            AI.SelectPlace(_gryphonMainZone);
            return true;
        }
    }
}
