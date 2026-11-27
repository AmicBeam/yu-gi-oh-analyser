using System.Collections.Generic;
using System.Linq;
using YGOSharp.OCGWrapper.Enums;

namespace WindBot.Game.AI.Decks
{
    // Named-only baseline for the Amazoness seat.
    [Deck("Amazoness", "AI_Amazoness", "Experimental")]
    public sealed class AmazonessExecutor : DefaultExecutor
    {
        private const int Warchief = 50486289;
        private const int Princess = 84539520;
        private const int Queen = 15951532;
        private const int Fighter = 101307006;
        private const int CurseMaster = 101307007;
        private const int Harpie = 101307008;
        private const int Call = 57312333;
        private const int Reinforcement = 32807846;
        private const int Mirror = 101307053;
        private const int Formation = 101307054;
        private const int Rescue = 101307074;
        private const int Swordmaster = 101307032;
        private const int MaxxC = 23434539;
        private const int AshBlossom = 14558128;

        public AmazonessExecutor(GameAI ai, Duel duel) : base(ai, duel)
        {
            AddExecutor(ExecutorType.Activate, Swordmaster, NegateWithSwordmaster);
            AddExecutor(ExecutorType.Activate, _CardId.InfiniteImpermanence, DefaultInfiniteImpermanence);
            AddExecutor(ExecutorType.Activate, _CardId.EffectVeiler, DefaultEffectVeiler);
            AddExecutor(ExecutorType.Activate, AshBlossom, () => Duel.LastChainPlayer == 1 && DefaultAshBlossomAndJoyousSpring());
            AddExecutor(ExecutorType.Activate, _CardId.GhostBelle, DefaultGhostBelleAndHauntedMansion);
            AddExecutor(ExecutorType.Activate, MaxxC, DefaultMaxxC);

            AddExecutor(ExecutorType.Activate, Warchief, UseWarchief);
            AddExecutor(ExecutorType.Activate, Fighter, UseFighter);
            AddExecutor(ExecutorType.Activate, CurseMaster, UseCurseMaster);
            AddExecutor(ExecutorType.Activate, Princess, UsePrincess);
            AddExecutor(ExecutorType.Activate, Queen, UsePrincess);
            AddExecutor(ExecutorType.Activate, Harpie, () => ChooseEffect(Harpie, 2, Rescue, Formation, Mirror));
            AddExecutor(ExecutorType.Activate, Swordmaster, () => ChooseEffect(Swordmaster, 0, Fighter, CurseMaster, Princess));
            AddExecutor(ExecutorType.Activate, Reinforcement, SearchStarter);
            AddExecutor(ExecutorType.Activate, Call, () => Duel.Player == 0 && Card.Location == CardLocation.Hand && SearchStarter());
            AddExecutor(ExecutorType.Activate, Formation, ReviveWithFormation);
            AddExecutor(ExecutorType.Activate, Rescue, BounceOpponent);
            AddExecutor(ExecutorType.Summon, CurseMaster);
            AddExecutor(ExecutorType.Summon, Princess);
            AddExecutor(ExecutorType.Summon, Fighter);
            AddExecutor(ExecutorType.Summon, Warchief, DefaultMonsterSummon);
            AddExecutor(ExecutorType.SpellSet, Rescue, () => !Bot.HasInSpellZone(Rescue));
            AddExecutor(ExecutorType.SpellSet, Formation, () => !Bot.HasInSpellZone(Formation));
            AddExecutor(ExecutorType.SpellSet, _CardId.InfiniteImpermanence,
                () => !Bot.IsFieldEmpty() && !Bot.HasInSpellZone(_CardId.InfiniteImpermanence));
        }

        public override bool OnSelectHand()
        {
            return true;
        }

        public override int OnSelectOption(IList<int> options)
        {
            int wipe = options.IndexOf(Util.GetStringId(Formation, 3));
            int take = options.IndexOf(Util.GetStringId(Formation, 2));
            if (wipe >= 0 && (take < 0 || Enemy.GetMonsterCount() > 1))
                return wipe;
            if (take >= 0)
                return take;
            return base.OnSelectOption(options);
        }

        private bool UseWarchief()
        {
            // The server script uses the same unspecified description for both effects.
            if (Card.IsDisabled())
                return false;
            if (Card.Location == CardLocation.Hand)
                return Duel.Player == 0;
            if (Card.Location != CardLocation.MonsterZone)
                return false;

            AI.SelectCard(Bot.HasInSpellZone(Rescue) ? Formation : Rescue, Call);
            return true;
        }

        private bool UseFighter()
        {
            if (Card.IsDisabled())
                return false;
            if (Card.Location == CardLocation.Hand)
                return ActivateDescription == -1 || ActivateDescription == Util.GetStringId(Fighter, 0);
            return ChooseEffect(Fighter, 1, Bot.HasInSpellZone(Rescue) ? Formation : Rescue, Formation);
        }

        private bool UseCurseMaster()
        {
            if (Card.Location == CardLocation.MonsterZone && ChooseEffect(CurseMaster, 0, Fighter, Warchief, Princess))
                return true;
            if (Duel.Player != 0 || ActivateDescription != Util.GetStringId(CurseMaster, 1) ||
                !Bot.HasInExtra(Swordmaster) || !Bot.HasInHandOrHasInMonstersZone(Warchief) &&
                !Bot.HasInHandOrHasInMonstersZone(Fighter) && !Bot.HasInHandOrHasInMonstersZone(Princess) && !Bot.HasInMonstersZone(Queen))
                return false;

            AI.SelectCard(Swordmaster);
            return true;
        }

        private bool UsePrincess()
        {
            return ChooseEffect(Princess, 0, Rescue, Formation, Call);
        }

        private bool SearchStarter()
        {
            AI.SelectCard(Bot.HasInHand(Warchief) ? CurseMaster : Warchief, CurseMaster, Princess, Fighter);
            return true;
        }

        private bool ChooseEffect(int id, int effect, params int[] preferred)
        {
            // Some SRVPro2 single-trigger prompts carry a generic description.
            bool genericTrigger = ActivateDescription == -1 && Card.Location == CardLocation.MonsterZone &&
                Duel.Phase != DuelPhase.Battle && Duel.Phase != DuelPhase.Damage && Duel.Phase != DuelPhase.DamageCal;
            if (Card.IsDisabled() || (!genericTrigger && ActivateDescription != Util.GetStringId(id, effect)))
                return false;
            AI.SelectCard(preferred);
            return true;
        }

        private bool ReviveWithFormation()
        {
            if (Card.Location != CardLocation.SpellZone || Duel.Player != 1)
                return false;

            // In the baseline, wait for a removal target and an existing body before committing Formation.
            if (Enemy.GetMonsterCount() == 0 || SingleTargetAlreadyCovered(Rescue, Swordmaster) ||
                !Bot.GetMonsters().Any(card => card.IsFaceup() &&
                card.IsCode(Warchief, Fighter, CurseMaster, Princess, Swordmaster, Harpie, Queen)))
                return false;

            AI.SelectCard(Swordmaster, Warchief, Fighter, Harpie, CurseMaster, Princess);
            return true;
        }

        private bool BounceOpponent()
        {
            if (Card.Location != CardLocation.SpellZone || ActivateDescription != Util.GetStringId(Rescue, 0) ||
                SingleTargetAlreadyCovered(Formation, Swordmaster))
                return false;
            var target = Enemy.GetMonsters()
                .Where(monster => !monster.IsShouldNotBeTarget() && !monster.IsShouldNotBeSpellTrapTarget())
                .OrderByDescending(monster => monster.GetDefensePower())
                .FirstOrDefault();
            if (target == null)
                return false;

            AI.SelectCard(target);
            AI.SelectNextCard(Fighter, Warchief, Harpie, CurseMaster, Princess);
            return true;
        }

        private bool SingleTargetAlreadyCovered(params int[] responses)
        {
            return Enemy.GetMonsterCount() == 1 && Duel.CurrentChain.Any(card =>
                card.Controller == 0 && card.IsCode(responses));
        }

        private bool NegateWithSwordmaster()
        {
            if (ActivateDescription != Util.GetStringId(Swordmaster, 2) || Card.IsDisabled() || Duel.LastChainPlayer != 1)
                return false;

            var target = Util.GetLastChainCard();
            if (target == null || target.Controller != 1 || target.Location != CardLocation.MonsterZone ||
                !target.IsFaceup() || !target.HasType(CardType.Effect) || target.IsDisabled() || target.IsShouldNotBeTarget())
                return false;

            var tribute = Bot.GetMonsters().Where(card => card != Card)
                .OrderBy(card => card.Attack).FirstOrDefault() ?? Card;
            AI.SelectCard(tribute);
            AI.SelectNextCard(target);
            return true;
        }
    }
}
