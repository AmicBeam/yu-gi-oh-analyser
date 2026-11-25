using System.Collections.Generic;
using System.Linq;
using YGOSharp.OCGWrapper.Enums;

namespace WindBot.Game.AI.Decks
{
    [Deck("Twins", "AI_Twins", "Normal")]
    public partial class TwinsExecutor : DefaultExecutor
    {
        private const int KiSikil = 36326160;
        private const int LilLa = 73810864;
        private const int Frost = 54257392;
        private const int Treat = 81078880;
        private const int Sweet = 82699999;
        private const int NewYear = 25150;
        private const int NightPhoto = 25151;
        private const int Scout = 25152;
        private const int Heist = 25153;
        private const int Nightfall = 25154;
        private const int Disconnect = 25156;
        private const int Trouble = 37582948;
        private const int Password = 61976639;
        private const int Challenge = 98360333;
        private const int Deal = 6636319;
        private const int EvilKiSikil = 9205573;
        private const int EvilLilLa = 36609518;
        private const int Sunny = 93672138;
        private const int Requiem = 2463794;
        private const int Engraver = 60764609;
        private const int Lacrima = 28803166;
        private const int Tract = 98567237;
        private const int Sequence = 49867899;
        private const int LacrimaFusion = 46640168;
        private const int Desirae = 82135803;
        private const int Caesar = 79559912;
        private const int Gryphon = 65330383;
        private const int Abao = 4731783;
        private const int Sharvara = 88554436;
        private const int Yama = 24269961;
        private const int Nibiru = 27204311;
        private const int NibiruToken = 27204312;
        private const int Droll = 94145021;
        private const int Fuwalos = 42141493;
        private const int Crossout = 65681983;
        private const int KiSikilSet = 0x152;
        private const int LilLaSet = 0x153;

        private static readonly int[] TroubleTargets = { NewYear, Frost, Treat, NightPhoto, KiSikil, LilLa };

        private bool _normalSummoned;
        private bool _newYearSearched;
        private int _enemySummoned;
        private int _lastSummonMessage;
        private int _planOwnSummons;
        private bool _planRequiemFromExtra, _planEarlyRequiem, _planRequiemDefenseAtNibiruWindow, _planPhotoAtNibiruWindow;
        private int _sequenceFusion;
        private bool _sequenceFusionUsed;
        private bool _desiraeNegateUsed;
        private bool _desiraeRemovalUsed;
        private int _challengeLink;
        private ClientCard _opponentNewYearRelease;
        private int _opponentNewYearPlace;
        private bool _waitingForNewYearCycle;

        public TwinsExecutor(GameAI ai, Duel duel) : base(ai, duel)
        {
            AddExecutor(ExecutorType.Activate, Sweet, () => Card.Location == CardLocation.Hand && EnemyChained());
            RegisterBoardPlanner();
            AddExecutor(ExecutorType.Activate, LacrimaFusion, ActivateLacrimaBurn);
            // Also applies to SELECT_CHAIN immediately after a successful Link Summon.
            AddExecutor(ExecutorType.Activate, Requiem,
                () => Duel.Player == 0 && MatchesEffect(Requiem, 0));
            RegisterGryphonResponses();
            AddExecutor(ExecutorType.Activate, Disconnect, () => EnemyChained() && (Duel.Player == 0 || HasDisconnectPair()));
            AddExecutor(ExecutorType.Activate, Crossout, CrossoutActivate);
            AddExecutor(ExecutorType.Activate, _CardId.AshBlossom,
                () => !ShouldSaveCounterForDrawEffect() && DefaultAshBlossomAndJoyousSpring());
            AddExecutor(ExecutorType.Activate, _CardId.EffectVeiler, DefaultEffectVeiler);
            AddExecutor(ExecutorType.Activate, _CardId.GhostOgreAndSnowRabbit, DefaultGhostOgreAndSnowRabbit);
            AddExecutor(ExecutorType.Activate, _CardId.InfiniteImpermanence, DefaultInfiniteImpermanence);
            AddExecutor(ExecutorType.Activate, Droll, () => Duel.Player == 1);
            AddExecutor(ExecutorType.Activate, Nibiru, () => !UseGryphonResponses() && NibiruActivate());
            AddExecutor(ExecutorType.Activate, Caesar, EnemyChained);
            AddExecutor(ExecutorType.Activate, Desirae, DesiraeActivate);
            AddExecutor(ExecutorType.Activate, Disconnect, () => EnemyChained() && !HoldDisconnectForTwins());
            AddExecutor(ExecutorType.Repos, RepositionForLethal);
            AddExecutor(ExecutorType.Activate, Sequence, ActivateSequenceForBurn);
            AddExecutor(ExecutorType.SpSummon, Sequence, PrepareSequenceForBurn);
            AddExecutor(ExecutorType.Activate, Engraver, SendLacrimaWithEngraver);
            AddExecutor(ExecutorType.SpSummon, Requiem, PrepareLacrimaBurnLink);
            AddExecutor(ExecutorType.SpSummon, EvilKiSikil, PrepareLacrimaBurnLink);
            AddExecutor(ExecutorType.SpSummon, EvilLilLa, PrepareLacrimaBurnLink);
            AddExecutor(ExecutorType.SpSummon, Heist, PrepareLacrimaBurnLink);
            AddExecutor(ExecutorType.Activate, EvilLilLa, ActivateBlueBattleRemoval);
            AddExecutor(ExecutorType.Activate, Abao, TryStartBattleClear);
            AddExecutor(ExecutorType.SpSummon, Abao, TryStartBattleClear);
            AddExecutor(ExecutorType.Activate, EvilKiSikil, TryStartBattleClear);
            AddExecutor(ExecutorType.Activate, Requiem, TryStartBattleClear);
            AddExecutor(ExecutorType.Activate, Engraver, TryStartBattleClear);
            AddExecutor(ExecutorType.Activate, Sunny, () => Card.Location == CardLocation.Grave && TryStartBattleClear());
            AddExecutor(ExecutorType.GoToBattlePhase, EnterBattleForCurrentBoard);
            AddExecutor(ExecutorType.GoToBattlePhase, EnterBattleForNightfall);
            AddExecutor(ExecutorType.SpSummon, Nightfall, PrepareNightfallRotation);
            AddExecutor(ExecutorType.Activate, Abao, AbaoActivate);
            AddExecutor(ExecutorType.Activate, Nightfall, NightfallActivate);
            AddExecutor(ExecutorType.Activate, Challenge, ChallengeActivate);
            AddExecutor(ExecutorType.Activate, Sunny, SunnyActivate);
            AddExecutor(ExecutorType.Activate, NewYear, NewYearActivate);
            AddExecutor(ExecutorType.Activate, EvilKiSikil, () => TwinLinkActivate(EvilKiSikil));
            AddExecutor(ExecutorType.Activate, EvilLilLa, () => TwinLinkActivate(EvilLilLa));
            AddExecutor(ExecutorType.Activate, Heist, () => (MatchesEffect(Heist, 0)
                || Duel.Player == 1 && Card.Location == CardLocation.MonsterZone && ActivateDescription <= 0)
                && Enemy.GetMonsters().Concat(Enemy.GetSpells()).Any(card => card.IsFaceup()
                    && !card.IsDisabled() && !HasPendingRemoval(card)));
            AddExecutor(ExecutorType.Activate, KiSikil);
            AddExecutor(ExecutorType.Activate, LilLa);
            AddExecutor(ExecutorType.Activate, Scout);
            AddExecutor(ExecutorType.Activate, Deal);
            AddExecutor(ExecutorType.Activate, Lacrima, () => Card.Location == CardLocation.MonsterZone
                || Duel.Player == 1 && Bot.GetMonsters().Count == 0);
            AddExecutor(ExecutorType.Activate, Sharvara, SharvaraActivate);
            AddExecutor(ExecutorType.Activate, Nibiru, () => UseGryphonResponses() && NibiruActivate());

            AddExecutor(ExecutorType.Summon, KiSikil, () => Bot.GetMonsters().Count == 0);
            AddExecutor(ExecutorType.Summon, LilLa, () => Bot.GetMonsters().Count == 0);
            AddExecutor(ExecutorType.Activate, Trouble);
            AddExecutor(ExecutorType.Activate, Password);
            AddExecutor(ExecutorType.Summon, NewYear, () => Bot.GetMonsters().Count == 0);
            AddExecutor(ExecutorType.Summon, Frost, () => Bot.GetMonsters().Count == 0);
            AddExecutor(ExecutorType.Summon, Treat, () => Bot.GetMonsters().Count == 0);
            AddExecutor(ExecutorType.Summon, Sweet, () => Bot.GetMonsters().Count == 0);
            AddExecutor(ExecutorType.Activate, Sweet, () => Card.Location == CardLocation.Grave);
            AddExecutor(ExecutorType.Activate, Frost, () => Card.Location == CardLocation.Grave);
            AddExecutor(ExecutorType.Activate, Treat, () => Card.Location == CardLocation.Grave && HasVisibleEnemyCard());

            AddExecutor(ExecutorType.SpSummon, Requiem, () => PrepareLink(Deal));
            AddExecutor(ExecutorType.SpSummon, Deal, () => PrepareLink(KiSikil, NewYear, Frost));
            AddExecutor(ExecutorType.SpSummon, Frost);
            AddExecutor(ExecutorType.SpSummon, Treat);
            AddExecutor(ExecutorType.SpSummon, Scout, () => PrepareLink(Treat, LilLa, Sweet));
            AddExecutor(ExecutorType.Activate, NightPhoto, NightPhotoActivate);
            AddExecutor(ExecutorType.Activate, Sequence, SequenceActivate);
            AddExecutor(ExecutorType.Activate, Requiem, () => MatchesEffect(Requiem, 1) && NeedsDesiraeEquip());
            AddExecutor(ExecutorType.Activate, Sequence, () => MatchesEffect(Sequence, 1) && NeedsDesiraeEquip());
            AddExecutor(ExecutorType.Activate, Engraver, EngraverActivate);
            AddExecutor(ExecutorType.Activate, Tract, () => Bot.HasInDeck(Lacrima, Engraver));
            AddExecutor(ExecutorType.Summon, Lacrima, () => !Bot.HasInHand(KiSikil) && !Bot.HasInHand(LilLa));
            AddExecutor(ExecutorType.SpSummon, Abao, PrepareAbaoRestart);
            AddExecutor(ExecutorType.SpSummon, Yama, PrepareYamaForAbao);
            AddExecutor(ExecutorType.SpSummon, EvilLilLa, PrepareLilLaForAbao);
            AddExecutor(ExecutorType.SpSummon, Sequence, () => Bot.HasInExtra(Desirae) && PrepareSequence());
            AddExecutor(ExecutorType.SpSummon, Gryphon, PrepareGryphon);
            AddExecutor(ExecutorType.SpSummon, Caesar, PrepareCaesar);
            AddExecutor(ExecutorType.SpSummon, Sequence, () => !Bot.HasInExtra(Desirae) && PrepareSequence());
            AddExecutor(ExecutorType.Activate, Gryphon, () => MatchesEffect(Gryphon, 0));
            AddExecutor(ExecutorType.Activate, LacrimaFusion, LacrimaFusionActivate);
            AddExecutor(ExecutorType.Activate, Yama, () => Card.Location == CardLocation.MonsterZone
                || Bot.GetMonsters().Count == 0);

            AddExecutor(ExecutorType.SpSummon, Nightfall, () => CanSetTwinTrap() && PrepareLink(Heist, EvilLilLa, EvilKiSikil, Scout, Deal));
            AddExecutor(ExecutorType.SpSummon, EvilKiSikil, () => !Bot.HasInMonstersZone(Heist)
                && PrepareLink(Deal, Scout, Sweet, Frost, Treat, NewYear, KiSikil, LilLa));
            AddExecutor(ExecutorType.SpSummon, EvilLilLa, () => !Bot.HasInMonstersZone(Heist)
                && PrepareLink(Scout, Deal, Frost, Treat, Sweet, KiSikil, LilLa, NewYear, EvilKiSikil));
            AddExecutor(ExecutorType.SpSummon, Heist, () => PrepareLink(Scout, Deal, Sweet, Frost, Treat, KiSikil, LilLa, NewYear));
            AddExecutor(ExecutorType.Activate, Heist, () => MatchesEffect(Heist, 1)
                && Duel.Player == 0 && HasReviveTarget());
            AddExecutor(ExecutorType.SpSummon, Sunny, () => !Bot.HasInMonstersZone(Nightfall)
                && PrepareLink(Heist, EvilLilLa, EvilKiSikil, Scout, Deal));
            AddExecutor(ExecutorType.SpellSet, Disconnect, CanSetForEndBoard);
            AddExecutor(ExecutorType.SpellSet, Challenge, CanSetForEndBoard);
            AddExecutor(ExecutorType.SpellSet, Crossout, () => CanSetRemainingBackrow(Card));
            AddExecutor(ExecutorType.SpellSet, _CardId.InfiniteImpermanence, () => CanSetRemainingBackrow(Card));
            AddExecutor(ExecutorType.Repos, DefaultMonsterRepos);
        }

        public override bool OnSelectHand()
        {
            return System.Environment.GetEnvironmentVariable("TWINS_PLAN_TRACE") != "1"
                || System.Environment.GetEnvironmentVariable("TWINS_GO_SECOND") != "1";
        }

        public override void OnNewTurn()
        {
            RecordPlanEnd();
            ResetBoardPlanner();
            _normalSummoned = false;
            _newYearSearched = false;
            _enemySummoned = 0;
            _lastSummonMessage = 0;
            _planOwnSummons = 0;
            _planRequiemFromExtra = _planEarlyRequiem = _planRequiemDefenseAtNibiruWindow = _planPhotoAtNibiruWindow = false;
            _sequenceFusion = 0;
            _sequenceFusionUsed = false;
            _desiraeNegateUsed = false;
            _desiraeRemovalUsed = false;
            ClearChallengeLink();
            ResetBattleRoute();
            _battleClearEffectsUsed.Clear();
            _requiemSummoned = false;
            _lacrimaBurnUsed = false;
            _blueBattleRemovalTarget = null;
            _sunnyRemovalCost = null;
            _sunnyRemovalTarget = null;
            _abaoRemovalTarget = null;
            _abaoReviveTarget = null;
            _abaoRevivalPlace = 0;
            _opponentNewYearRelease = null;
            _opponentNewYearPlace = 0;
            _waitingForNewYearCycle = false;
            _challengeRevivalTarget = null;
            _challengeRevivalPlace = 0;
            _challengeRevivalForRemoval = false;
            _challengeBattleStarter = 0;
            _opponentActivatedCards.Clear();
            _twinTrapActivated = false;
            _abaoReloadNightfall = false;
            _returningAbao = null;
            _abaoReturnZone = 0;
            _gryphonAtTurnStart = Bot.HasInMonstersZone(Gryphon);
            base.OnNewTurn();
        }

        public override void OnChaining(int player, ClientCard card)
        {
            if (player == 1)
            {
                _opponentActivatedCards.Add(card);
                PlanOpponentChained(card);
            }
            else if (card.IsCode(Disconnect, Challenge))
                _twinTrapActivated = true;
            if (player == 0 && card.IsCode(NewYear) && card.Location == CardLocation.MonsterZone)
                _newYearSearched = true;
            base.OnChaining(player, card);
        }

        public override void OnSummoning()
        {
            if (Duel.LastSummonPlayer == 0)
            {
                _normalSummoned = true;
                CountOwnSummons(1);
            }
            else if (Duel.LastSummonPlayer == 1)
                _enemySummoned++;
            base.OnSummoning();
        }

        public override void OnMove(ClientCard card, int previousControler, int previousLocation, int currentControler, int currentLocation)
        {
            if (currentControler == 0 && previousLocation == (int)CardLocation.Extra
                && currentLocation == (int)CardLocation.MonsterZone)
            {
                if (card.IsCode(Deal))
                    _planDealLinked = true;
                if (card.IsCode(Requiem))
                    _planRequiemFromExtra = true;
            }
            base.OnMove(card, previousControler, previousLocation, currentControler, currentLocation);
        }

        public override void OnSpSummoning()
        {
            _lastSummonMessage = Duel.SummoningCards.Count;
            if (Duel.LastSummonPlayer == 0 && Duel.CurrentChainInfo.Count == 0
                && Duel.SummoningCards.Any(card => card != null && card.IsCode(Frost) && card.LastLocation == CardLocation.Hand))
                _planUsed |= Flag(PlanEffect.FrostSummon);
            base.OnSpSummoning();
        }

        public override void OnSpSummoned()
        {
            if (Duel.LastSummonPlayer == 0 && _challengeLink != 0
                && Duel.SummoningCards.Any(card => card != null && card.IsCode(_challengeLink)))
                ClearChallengeLink();
            if (Duel.LastSummonPlayer == 1)
                _enemySummoned += _lastSummonMessage;
            else if (Duel.LastSummonPlayer == 0)
            {
                bool requiemSummoned = Duel.SummoningCards.Any(card => card != null && card.IsCode(Requiem));
                _requiemSummoned |= requiemSummoned;
                _planEarlyRequiem |= requiemSummoned && _planRequiemFromExtra
                    && _planOwnSummons + _lastSummonMessage <= NibiruThreshold;
                CountOwnSummons(_lastSummonMessage);
                _planRequiemFromExtra = false;
            }
            base.OnSpSummoned();
        }

        private void CountOwnSummons(int count)
        {
            int previous = _planOwnSummons;
            _planOwnSummons = System.Math.Min(previous + count, NibiruThreshold + 1);
            if (previous >= NibiruThreshold || _planOwnSummons < NibiruThreshold)
                return;
            var position = LivePlanPosition();
            if (Duel.CurrentChainInfo.Any(chain => chain.ActivatePlayer == 0 && chain.IsActivateCode(NewYear)
                && chain.ActivateLocation != CardLocation.MonsterZone))
                position.Use(PlanEffect.NewYearJump);
            bool pendingNewYear = Duel.SummoningCards.Any(card => card != null && card.IsCode(NewYear)
                && CanActivateOnField(position.Field, card.Sequence, position.NormalZones));
            CaptureNibiruWindow(position, pendingNewYear);
            _planRequiemDefenseAtNibiruWindow = position.RequiemDefenseAtNibiruWindow;
            _planPhotoAtNibiruWindow = position.PhotoAtNibiruWindow;
        }

        public override void OnChainEnd()
        {
            if (Duel.CurrentChainInfo.Any(chain => chain.ActivatePlayer == 0 && chain.IsActivateCode(Sequence)
                && chain.ActivateDescription == Util.GetStringId(Sequence, 0)))
                _sequenceFusionUsed = true;
            foreach (ChainInfo chain in Duel.CurrentChainInfo.Where(link => link.ActivatePlayer == 0
                && link.IsActivateCode(Desirae)))
            {
                _desiraeNegateUsed |= chain.ActivateLocation == CardLocation.MonsterZone;
                _desiraeRemovalUsed |= chain.ActivateLocation == CardLocation.Grave;
            }
            RecordBattleRouteEffects();
            RecordBattleClearEffects();
            RecordBoardEffects();
            _lacrimaBurnUsed |= Duel.CurrentChainInfo.Any(chain => chain.ActivatePlayer == 0
                && chain.IsActivateCode(LacrimaFusion) && chain.ActivateLocation == CardLocation.Grave);
            _sequenceFusion = 0;
            _opponentNightfallTarget = 0;
            _opponentNightfallPlace = 0;
            _challengeRevivalTarget = null;
            _challengeRevivalPlace = 0;
            _challengeRevivalForRemoval = false;
            if (_challengeBattleStarter != 0 && (!Bot.HasInMonstersZone(_challengeBattleStarter)
                || Duel.CurrentChainInfo.Any(link => link.ActivatePlayer == 0 && link.IsActivateCode(_challengeBattleStarter))))
                _challengeBattleStarter = 0;
            _blueBattleRemovalTarget = null;
            _sunnyRemovalCost = null;
            _sunnyRemovalTarget = null;
            if (_abaoReviveTarget != null)
                _returningAbao = Bot.Banished.FirstOrDefault(card => card.IsCode(Abao));
            _abaoRemovalTarget = null;
            _abaoReviveTarget = null;
            _abaoRevivalPlace = 0;
            _opponentNewYearRelease = null;
            _opponentNewYearPlace = 0;
            _burnFusionMaterials = null;
            PlanChainEnded();
            base.OnChainEnd();
        }

        public override bool OnSelectYesNo(int desc)
        {
            bool? planned = BoardPlanYesNo(desc);
            if (planned.HasValue)
                return planned.Value;
            if (desc == Util.GetStringId(Challenge, 0))
            {
                if (_challengeRevivalForRemoval || _challengeBattleStarter != 0 && InBattle())
                    return false;
                if (PrepareChallengeDesiraeLink() || PrepareChallengeHeist())
                    return true;
                // On the opponent's turn, Challenge can revive without using the revived monster as material.
                if (Duel.Player == 1
                    || Bot.HasInMonstersZone(new[] { Nightfall, Sunny, Desirae, Gryphon }))
                    return false;
            }
            if (_abaoReloadNightfall && desc == Util.GetStringId(Nightfall, 2))
                return NightfallReloadTraps().Count > 1;
            if (desc == Util.GetStringId(Trouble, 0))
                return Bot.HasInDeck(TroubleTargets);
            if (desc == Util.GetStringId(Disconnect, 1))
            {
                if (HasFaceupTwin(LilLaSet))
                    return Enemy.GetMonsters().Count + Enemy.GetSpells().Count > 0
                        || Bot.GetSpells().Any(card => card.IsCode(Disconnect) && card.IsFaceup());
                return HasFaceupTwin(KiSikilSet);
            }
            return base.OnSelectYesNo(desc);
        }

        public override CardPosition OnSelectPosition(int cardId, IList<CardPosition> positions)
        {
            if (cardId == NibiruToken && positions.Contains(CardPosition.FaceUpDefence))
                return CardPosition.FaceUpDefence;

            var chain = Duel.GetCurrentSolvingChainInfo();
            if (_challengeBattleStarter != 0 && InBattle() && positions.Contains(CardPosition.FaceUpDefence)
                && chain?.ActivatePlayer == 0
                && (chain.IsActivateCode(Challenge) && cardId == _challengeBattleStarter
                    || chain.IsActivateCode(_challengeBattleStarter)
                        && (_challengeBattleStarter == KiSikil ? IsBlueTwin(cardId) : IsRedTwin(cardId))))
                return CardPosition.FaceUpDefence;
            if (Duel.Player == 0 && (Duel.Phase == DuelPhase.Main2 || Duel.Turn == 1 && Duel.Phase == DuelPhase.Main1)
                && positions.Contains(CardPosition.FaceUpDefence))
            {
                var monster = YGOSharp.OCGWrapper.NamedCard.Get(cardId);
                if (monster != null && (monster.Attack < 2000 || monster.Attack <= monster.Defense))
                    return CardPosition.FaceUpDefence;
            }
            return base.OnSelectPosition(cardId, positions);
        }

        public override int OnSelectOption(IList<int> options)
        {
            int planned = BoardPlanOption(options);
            if (planned >= 0)
                return planned;
            if (_abaoReviveTarget != null)
            {
                int revive = options.IndexOf(Util.GetStringId(Abao, 3));
                if (revive >= 0)
                    return revive;
            }
            if (_abaoRemovalTarget != null)
            {
                int destroy = options.IndexOf(Util.GetStringId(Abao, 2));
                if (destroy >= 0)
                    return destroy;
            }
            const int AddToHand = 1190;
            const int SpecialSummon = 1152;
            ChainInfo chain = Duel.GetCurrentSolvingChainInfo();
            if (chain != null && chain.ActivatePlayer == 0 && chain.IsActivateCode(LacrimaFusion)
                && options.Contains(AddToHand))
            {
                int summonOption = options.IndexOf(SpecialSummon);
                if (summonOption >= 0)
                    return summonOption;
            }
            return base.OnSelectOption(options);
        }

        public override IList<ClientCard> OnSelectCard(IList<ClientCard> cards, int min, int max, int hint, bool cancelable)
        {
            if (_challengeMaterials != null && hint == HintMsg.LinkMaterial)
            {
                var materials = _challengeMaterials.Where(cards.Contains).Take(max).ToList();
                if (materials.Count >= min)
                    return materials;
            }
            ChainInfo chain = Duel.GetCurrentSolvingChainInfo();
            if (chain == null && Duel.GetCurrentChainCard() != null)
                chain = Duel.CurrentChainInfo.LastOrDefault();
            if (min == 1 && hint == HintMsg.ToGrave && chain?.ActivatePlayer == 0
                && chain.IsActivateCode(Deal, Scout))
            {
                if (chain.IsActivateCode(Scout) && LiveCanSendFrostWithScout()
                    && _planPayment?.Card == Scout && _planPayment.Target == Frost)
                {
                    var frost = cards.FirstOrDefault(card => card.IsCode(Frost));
                    if (frost != null)
                        return new[] { frost };
                }
                int preferred = LivePreferredTwinSend(PlanCode(chain.RelatedCard),
                    id => cards.Any(card => card.IsCode(id)));
                var target = cards.FirstOrDefault(card => preferred != 0 && card.IsCode(preferred));
                if (target != null)
                    return new[] { target };
            }
            var planChoice = PlanChooseCards(cards, min, max, hint, cancelable);
            if (planChoice != null)
                return planChoice;

            if (chain != null && chain.ActivatePlayer == 0)
            {
                // Let GameAI consume the target chosen when the negation was activated.
                if ((hint == HintMsg.Disable || hint == HintMsg.Faceup)
                    && chain.IsActivateCode(_CardId.EffectVeiler, _CardId.InfiniteImpermanence))
                    return base.OnSelectCard(cards, min, max, hint, cancelable);
                if (Duel.Player == 1 && hint == HintMsg.Faceup && chain.IsActivateCode(Heist))
                {
                    var targets = cards.Where(card => !HasPendingRemoval(card)).OrderByDescending(IsFreshHeistTarget)
                        .ThenByDescending(card => card.IsFloodgate()).ThenByDescending(card => card.Attack).Take(min).ToList();
                    if (targets.Count == min)
                        return targets;
                }
                if (hint == HintMsg.Destroy && chain.IsActivateCode(Disconnect))
                {
                    var targets = cards.Where(card => card.Controller == 1).Take(min).ToList();
                    if (targets.Count == 0 && min == 1)
                    {
                        ClientCard trap = cards.FirstOrDefault(card => card == chain.RelatedCard)
                            ?? cards.FirstOrDefault(card => card.Controller == 0 && card.IsCode(Disconnect) && card.IsFaceup());
                        if (trap != null)
                            return new[] { trap };
                    }
                    var desirae = cards.FirstOrDefault(CanTradeDesiraeAfterDisconnect);
                    if (desirae != null && min == 1)
                        return new[] { desirae };
                    if (targets.Count >= min)
                        return targets;
                }
                if (chain.IsActivateCode(Abao))
                {
                    if (hint == HintMsg.SpSummon && _abaoReviveTarget != null)
                    {
                        var target = cards.FirstOrDefault(card => card == _abaoReviveTarget)
                            ?? cards.FirstOrDefault(card => PlanCode(card) == PlanCode(_abaoReviveTarget));
                        if (target != null)
                            return new[] { target };
                    }
                    if (hint == HintMsg.Destroy && cards.Contains(_abaoRemovalTarget))
                        return new[] { _abaoRemovalTarget };
                    if (hint == HintMsg.ToDeck)
                        return cards.OrderBy(AbaoHandValue).Take(min).ToList();
                }
                if (chain.IsActivateCode(Desirae) && chain.ActivateLocation == CardLocation.MonsterZone
                    && Duel.Player == 1 && hint == HintMsg.Disable)
                {
                    var targets = SelectGryphonDesiraeTargets(chain, cards, min, max);
                    if (targets != null)
                        return targets;
                }

                if (hint == HintMsg.SpSummon)
                {
                    if (chain.IsActivateCode(Challenge) && _challengeLink != 0
                        && cards.Any(card => card.IsCode(_challengeLink)))
                        return Prefer(cards, min, _challengeLink);
                    if (chain.IsActivateCode(KiSikil))
                        return Prefer(cards, min, LilLa, Treat, Sweet);
                    if (chain.IsActivateCode(LilLa))
                        return Prefer(cards, min, KiSikil, Frost, NewYear);
                    if (chain.IsActivateCode(Requiem))
                        return Prefer(cards, min, Engraver, Lacrima);
                    if (chain.IsActivateCode(Sequence))
                    {
                        ClientCard fusion = cards.FirstOrDefault(card => card.IsCode(_sequenceFusion));
                        if (fusion == null)
                            fusion = Prefer(cards, min, Desirae, LacrimaFusion).FirstOrDefault();
                        if (fusion != null)
                            _sequenceFusion = fusion.Id;
                        return fusion == null ? null : new[] { fusion };
                    }
                    if (chain.IsActivateCode(Nightfall))
                    {
                        if (Duel.Player == 1 && !HasFreshHeistTarget())
                        {
                            var other = cards.Where(card => !card.IsCode(Heist)).ToList();
                            if (other.Count >= min)
                            {
                                if (_opponentNightfallTarget == Heist)
                                    _opponentNightfallPlace = 0;
                                return Prefer(other, min, _opponentNightfallTarget, Sunny, EvilLilLa, Scout);
                            }
                        }
                        return Prefer(cards, min, _opponentNightfallTarget, Sunny, Heist, EvilLilLa, Scout);
                    }
                    if (chain.IsActivateCode(NightPhoto))
                        return Prefer(cards, min, Disconnect, Challenge);
                    if (chain.IsActivateCode(Sunny))
                        return Prefer(cards, max, EvilKiSikil, EvilLilLa, NewYear, Sweet);
                    if (chain.IsActivateCode(Challenge) && _challengeRevivalTarget != null && cards.Contains(_challengeRevivalTarget))
                        return new[] { _challengeRevivalTarget };
                    if (chain.IsActivateCode(Challenge) && _waitingForNewYearCycle
                        && cards.Any(card => card.IsCode(EvilLilLa)))
                        return Prefer(cards, min, EvilLilLa);
                    if (chain.IsActivateCode(Challenge) && CanUseBlueForChallenge())
                    {
                        var partners = cards.Where(card => card.Location == CardLocation.Grave && ChallengeHeistPartner(card)).ToList();
                        if (partners.Count >= min)
                            return Prefer(partners, min, NewYear, LilLa, KiSikil, Sweet, Frost);
                    }
                    if (chain.IsActivateCode(Challenge) && CanUseSunny())
                    {
                        var spare = cards.Where(card => !card.IsCode(EvilKiSikil, EvilLilLa)).ToList();
                        if (spare.Count >= min)
                            return Prefer(spare, min, NewYear, Scout, Deal, LilLa, KiSikil);
                    }
                    if (chain.IsActivateCode(Challenge) || chain.IsActivateCode(Heist)
                        || chain.IsActivateCode(EvilKiSikil) || chain.IsActivateCode(EvilLilLa))
                        return Prefer(cards, min, EvilLilLa, EvilKiSikil, NewYear, LilLa, KiSikil, Sweet, Frost);
                }

                if (hint == HintMsg.AddToHand)
                {
                    if (chain.IsActivateCode(Password))
                    {
                        if (_nightfallTrapSearch != 0 && cards.Any(card => card.IsCode(_nightfallTrapSearch)))
                            return Prefer(cards, min, _nightfallTrapSearch);
                        if (PasswordCanSpareTrouble())
                            return Prefer(cards, min, Disconnect, Challenge, Trouble);
                        return Prefer(cards, min, Trouble, Challenge, Disconnect);
                    }
                    if (chain.IsActivateCode(Trouble))
                    {
                        if (!_normalSummoned && Bot.GetMonsters().Count == 0)
                            return Prefer(cards, min, KiSikil, LilLa, NewYear, Frost, Treat, NightPhoto);
                        var field = Bot.GetMonsters();
                        if (!_newYearSearched && Bot.HasInExtra(Scout) && Bot.HasInDeck(NewYear)
                            && field.Any(card => card.IsFaceup() && card.HasSetcode(LilLaSet)
                                && RotationExtraZones(field.Where(monster => monster != card).ToList()) != 0))
                            return Prefer(cards, min, Treat, Frost, NightPhoto, NewYear, KiSikil, LilLa);
                        return Prefer(cards, min, TroubleTargets);
                    }
                    if (chain.IsActivateCode(Tract))
                        return Prefer(cards, min, Lacrima, Engraver);
                    if (chain.IsActivateCode(Yama))
                        return Prefer(cards, min, Sharvara);
                }

                if (hint == HintMsg.ToGrave)
                {
                    if (chain.IsActivateCode(Desirae))
                    {
                        var targets = RankDesiraeTargets(cards).Take(min).ToList();
                        if (targets.Count >= min)
                            return targets;
                    }
                    if (chain.IsActivateCode(Deal))
                        return Prefer(cards, min, Treat, LilLa, Sweet);
                    if (chain.IsActivateCode(Scout))
                        return Prefer(cards, min, KiSikil, Deal, Frost, NewYear);
                    if (chain.IsActivateCode(Lacrima))
                        return Prefer(cards, min, Engraver, Tract);
                    if (chain.IsActivateCode(Sunny) && chain.ActivateDescription % 16 == 1)
                        return SelectSunnyRemovalCards(cards, min, Duel.GetCurrentSolvingChainInfo() == chain);
                }

                if (hint == HintMsg.Release && chain.IsActivateCode(NewYear))
                {
                    if (min == 1 && cards.Contains(_opponentNewYearRelease))
                        return new[] { _opponentNewYearRelease };
                    ClientCard desirae = cards.FirstOrDefault(CanTradeDesirae);
                    if (desirae != null)
                        return new[] { desirae };
                    return Prefer(cards, min, Scout, Deal, Treat, Frost, Sweet, KiSikil, LilLa);
                }
                if (hint == HintMsg.Set && chain.IsActivateCode(Nightfall))
                    return _abaoReloadNightfall
                        ? Prefer(cards, min, NightfallReloadTraps().ToArray())
                        : Prefer(cards, min, Disconnect, Challenge);
                if (hint == HintMsg.Remove && chain.IsActivateCode(Nightfall))
                    return Prefer(cards, min, Challenge, Deal, Scout, Heist, Disconnect, EvilKiSikil, EvilLilLa);
                if (hint == HintMsg.Remove && chain.IsActivateCode(NightPhoto))
                    return Prefer(cards, min, Trouble, Password);
                if (hint == HintMsg.ToDeck && chain.IsActivateCode(NightPhoto))
                    return PhotoReturnTargets(cards).Take(min).ToList();
                if (hint == HintMsg.Discard && chain.IsActivateCode(Tract))
                    return Prefer(cards, min, Engraver, NewYear, Sweet, Frost);
                if (hint == HintMsg.Destroy && chain.IsActivateCode(Abao) && _abaoRemovalTarget != null)
                {
                    ClientCard target = cards.Contains(_abaoRemovalTarget) ? _abaoRemovalTarget
                        : cards.OrderByDescending(card => card.Controller == 1)
                            .ThenBy(card => card.IsCode(Nightfall, Sunny, Gryphon, Desirae))
                            .ThenBy(card => card.Attack).FirstOrDefault();
                    _abaoRemovalTarget = null;
                    return target == null ? null : new[] { target };
                }
                if (hint == HintMsg.FusionMaterial && chain.IsActivateCode(Sequence))
                {
                    if (_burnFusionMaterials != null)
                    {
                        var planned = _burnFusionMaterials.Where(cards.Contains).Take(min).ToList();
                        if (planned.Count == min)
                            return planned;
                    }
                    return _sequenceFusion == Desirae
                        ? Prefer(cards, min, Engraver, Deal, Lacrima, Requiem, EvilKiSikil)
                        : Prefer(cards, min, Requiem, Lacrima, Deal, EvilKiSikil, Engraver);
                }
                if (hint == HintMsg.Target && (chain.IsActivateCode(Sequence) || chain.IsActivateCode(Requiem))
                    && cards.Any(card => card.IsCode(Desirae)))
                    return Prefer(cards, min, Desirae);
                if (hint == HintMsg.Target && chain.IsActivateCode(LacrimaFusion))
                    return Prefer(cards, min, Engraver, Deal, EvilKiSikil, Requiem);
                if ((hint == HintMsg.Set || hint == HintMsg.Target) && chain.IsActivateCode(Gryphon))
                    return Prefer(cards, min, Disconnect, Challenge, _CardId.InfiniteImpermanence);
            }

            if (hint == HintMsg.Destroy && _blueBattleRemovalTarget != null)
            {
                ClientCard planned = _blueBattleRemovalTarget;
                _blueBattleRemovalTarget = null;
                if (cards.Contains(planned))
                    return new[] { planned };
            }
            if (hint == HintMsg.Destroy || hint == HintMsg.Faceup || hint == HintMsg.Disable)
            {
                var targets = cards.Where(card => card.Controller == 1)
                    .OrderByDescending(card => card.IsMonsterDangerous())
                    .ThenByDescending(card => card.Attack)
                    .ToList();
                if (targets.Count >= min && targets.Count > 0)
                    return targets.Take(max == 1 ? 1 : min).ToList();
            }

            return base.OnSelectCard(cards, min, max, hint, cancelable);
        }

        private bool MatchesEffect(int code, int index)
        {
            return ActivateDescription == Util.GetStringId(code, index);
        }

        private bool EnemyChained()
        {
            return Duel.LastChainPlayer == 1;
        }

        private bool HasVisibleEnemyCard()
        {
            return Enemy.GetMonsters().Any(card => card.IsFaceup() && !card.IsDisabled())
                || Enemy.SpellZone.Any(card => card != null && card.IsFaceup() && !card.IsDisabled());
        }

        private bool HasReviveTarget()
        {
            return Bot.Graveyard.Any(card => card.HasSetcode(KiSikilSet) || card.HasSetcode(LilLaSet));
        }

        private bool CanSetTwinTrap()
        {
            return Bot.HasInDeck(Disconnect, Challenge) || Bot.HasInGraveyard(new[] { Disconnect, Challenge });
        }

        private bool PrepareLink(params int[] preferred)
        {
            // Keep the trap live once Nightfall has been established.
            if (Bot.HasInMonstersZone(Nightfall) && !Card.IsCode(Deal, Scout))
                return false;
            var available = Bot.GetMonsters()
                .Where(card => card.IsFaceup() && !card.IsCode(Nightfall, Sunny, Desirae, Caesar, Gryphon))
                .ToList();
            int minimum = Card.IsCode(Requiem, Deal, Scout) ? 1 : 2;
            if (available.Count < minimum)
                return false;
            if (Card.IsCode(Nightfall, Sunny) && available.Count(card => card.LinkCount == 2) < 2)
                return false;
            if (Card.IsCode(Nightfall) && PrepareNightfallPlacement(available))
                return true;
            if (Card.IsCode(Requiem) && Bot.HasInMonstersZone(Deal))
                AI.SelectMaterials(Deal, HintMsg.LinkMaterial);
            else
            {
                var materials = Prefer(available, available.Count, preferred);
                if (minimum == 1)
                    materials = materials.OrderBy(card => card.LinkCount == 2).ToList();
                AI.SelectMaterials(materials, HintMsg.LinkMaterial);
            }
            return true;
        }

        private bool CrossoutActivate()
        {
            ClientCard last = Duel.GetCurrentChainCard();
            if (!EnemyChained() || last == null || last.Id == 0 || !Bot.HasInDeck(last.Id)
                || ShouldSaveCounterForDrawEffect())
                return false;
            AI.SelectAnnounceID(last.Id);
            return true;
        }

        private bool NightfallActivate()
        {
            if (MatchesEffect(Nightfall, 0) || (ActivateDescription <= 0 && Duel.LastSummonedCards.Contains(Card)))
                return ShouldSetNightfallTraps(Card);
            if (Duel.Player == 0)
                return MatchesEffect(Nightfall, 1) && OwnNightfallActivate();
            if (Duel.Player != 1 || !MatchesEffect(Nightfall, 1))
                return false;
            if (PrepareDrawPhaseNightfall())
                return true;
            if (!HasVisibleEnemyCard() && !Duel.ChainTargets.Contains(Card))
                return false;
            bool sunny = CanUseSunny();
            if (!Duel.ChainTargets.Contains(Card) && Bot.HasInSpellZone(Disconnect) && EnemyChained())
                return false;
            if (!Duel.ChainTargets.Contains(Card) && CanStartNewYearTwinCycle())
                return false;
            if (!Duel.ChainTargets.Contains(Card) && _waitingForNewYearCycle && Duel.Phase != DuelPhase.End
                && ((_planUsed & Flag(PlanEffect.BluePop)) == 0
                    || Bot.HasInExtra(Heist) && !HasFreshHeistTarget()))
                return false;
            bool prepared = PrepareOpponentNightfall();
            if (prepared && NightfallCosts().Any())
                AI.SelectCard(NightfallCosts().ToList());
            return prepared || Duel.ChainTargets.Contains(Card) || sunny && !Bot.HasInMonstersZone(Gryphon);
        }

        private bool NewYearActivate()
        {
            if (Card.Location == CardLocation.MonsterZone)
                return true;
            if (MatchesEffect(NewYear, 1))
                return false;
            var draw = Duel.CurrentChainInfo.LastOrDefault();
            if (Duel.Player == 1 && draw != null && draw.ActivatePlayer == 0 && draw.IsActivateCode(EvilKiSikil)
                && LivePlanEffect(draw, EvilKiSikil) == 0)
            {
                var blue = Bot.GetMonsters().FirstOrDefault(card => card.IsCode(EvilLilLa) && card.IsFaceup());
                int place = blue == null ? 0 : NewYearTwinCyclePlace(Bot.MonsterZone.Select(PlanCode).ToArray(), blue.Sequence);
                if (place != 0)
                {
                    _opponentNewYearRelease = blue;
                    _opponentNewYearPlace = place;
                    AI.SelectCard(blue);
                    return true;
                }
            }
            ClientCard desirae = Duel.Player == 1 ? Bot.GetMonsters().FirstOrDefault(CanTradeDesirae) : null;
            if (desirae != null)
            {
                if (Duel.CurrentChainInfo.Any(chain => chain.ActivatePlayer == 0 && chain.IsActivateCode(Challenge)))
                    return false;
                AI.SelectCard(desirae);
                return true;
            }
            ClientCard threatened = NewYearTargetToDodge(draw);
            if (threatened == null)
                return false;
            _opponentNewYearRelease = threatened;
            AI.SelectCard(threatened);
            return true;
        }

        private ClientCard NewYearTargetToDodge(ChainInfo chain)
        {
            if (chain == null || chain.ActivatePlayer != 1)
                return null;
            bool negate = chain.IsActivateCode(_CardId.EffectVeiler, _CardId.InfiniteImpermanence,
                _CardId.BreakthroughSkill, 25789292, 50078509);
            bool freeMain = Bot.MonsterZone.Take(5).Any(card => card == null);
            return chain.Targets.FirstOrDefault(card => card.Controller == 0 && card.Location == CardLocation.MonsterZone
                && card.IsFaceup() && (freeMain || card.Sequence < 5)
                && ((CardAttribute)card.Attribute & (CardAttribute.Light | CardAttribute.Dark)) != 0
                && !(negate && card.IsCode(Deal, Scout)));
        }

        private bool TwinLinkActivate(int id)
        {
            if (!MatchesEffect(id, 1))
                return id == EvilKiSikil || Enemy.GetMonsters().Count + Enemy.GetSpellCount() > 0;
            int partner = id == EvilKiSikil ? EvilLilLa : EvilKiSikil;
            if (Duel.CurrentChainInfo.Any(link => link.ActivatePlayer == 0 && link.IsActivateCode(Challenge)
                && link.Targets.Any(target => target.IsCode(partner))))
                return false;
            if (id == EvilLilLa && Bot.HasInExtra(Yama) && YamaMaterialsForAbao() != null)
                return false;
            if (id == EvilLilLa && Duel.Player == 1 && HasRevivableTwin(EvilKiSikil)
                && HasVisibleEnemyCard() && (Bot.HasInGraveyard(NewYear) || Bot.Hand.Any(card => card.IsCode(NewYear))))
                return true;
            return Duel.Player == 0 || id == EvilKiSikil && Enemy.GetMonsters().Count + Enemy.GetSpellCount() > 0
                || Duel.ChainTargets.Contains(Card);
        }

        private bool SunnyActivate()
        {
            if (Card.Location != CardLocation.MonsterZone)
                return false;
            if (Duel.Player == 0)
                return OwnSunnyActivate();
            return Duel.Player == 1 && (Duel.ChainTargets.Contains(Card)
                || Enemy.GetMonsters().Count + Enemy.GetSpellCount() > 0);
        }

        private bool NightPhotoActivate()
        {
            if (Card.Location == CardLocation.Hand)
            {
                if (Bot.GetMonsters().Count >= 3)
                    return false;
                var targets = PhotoReturnTargets(Bot.Graveyard
                    .Where(card => card.HasSetcode(KiSikilSet) || card.HasSetcode(LilLaSet))).ToList();
                if (targets.Count == 0)
                    return false;
                AI.SelectCard(targets);
                return true;
            }
            return Duel.Player == 1 && (EnemyChained() || HasVisibleEnemyCard());
        }

        private bool EngraverActivate()
        {
            if (Card.Location == CardLocation.Hand)
                return Bot.HasInDeck(Tract);
            if (Card.Location == CardLocation.Grave)
                return Bot.GetMonsters().Count < 4;
            return HasVisibleEnemyCard() && Enemy.GetMonsters().Count + Enemy.GetSpellCount() > 0;
        }

        private bool SharvaraActivate()
        {
            if (Card.Location == CardLocation.Grave)
                return true;
            if (Duel.Player != 0 || !Bot.HasInMonstersZone(Scout))
                return false;
            AI.SelectCard(Scout);
            return true;
        }

        private static IList<ClientCard> Prefer(IList<ClientCard> cards, int count, params int[] order)
        {
            return cards.OrderBy(card =>
            {
                int index = System.Array.FindIndex(order, id => card.IsCode(id));
                return index < 0 ? order.Length : index;
            }).Take(count).ToList();
        }
    }
}
