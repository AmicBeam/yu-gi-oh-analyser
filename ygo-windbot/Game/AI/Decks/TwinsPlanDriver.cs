using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using YGOSharp.OCGWrapper.Enums;

namespace WindBot.Game.AI.Decks
{
    public partial class TwinsExecutor
    {
        private PlanResult _boardPlan;
        private int _planStep, _planReturnZone, _planDraws, _planOwnerTurn;
        private long _referenceSearchMs;
        private ClientCard _planReturningNewYear;
        private bool _planStopped, _planInterrupted, _planDealLinked, _planFiends, _planTwins;
        private bool _planOtherInterference, _planInventoryChanged, _planDrawPending;
        private const int MoonSqueeze = 47605577;
        private bool _moonDrawPressure, _settleDrawPressure, _finishWithBackrow;
        private ChainInfo _moonDrawOnly;
        private ulong _planUsed;
        private MainPhase _planMain;
        private PlanMove _planPlacement, _planPayment;
        private IList<ClientCard> _planMaterialCards;
        private readonly Dictionary<int, PlanMove> _planActivations = new Dictionary<int, PlanMove>();
        private List<int> _planSetTraps;
        private bool InBoardBuildPhase => Duel.Player == 0 &&
            (Duel.Phase == DuelPhase.Main2 || Duel.Phase == DuelPhase.Main1 &&
                (Duel.Turn == 1 || Duel.MainPhase != null && !Duel.MainPhase.CanBattlePhase));

        private bool CanPlanBoard()
        {
            // Combat, board breaking and lethal are handled by the existing Main 1 / Battle policy.
            // The board builder needs a clear field because its model is for building, not removal.
            return InBoardBuildPhase && (Duel.Turn == 1 || Enemy.IsFieldEmpty() && !_searchLocked
                && Bot.GetMonsters().All(card => card.IsFaceup() && !card.IsDisabled())
                && !(BurnWindow() && CanPlanLacrimaBurn(CurrentBattlePosition())));
        }
        private bool CanSetForEndBoard()
        {
            // These sets cannot contribute to this turn's combat on a cleared field.
            return Duel.Player != 0 || Duel.Turn == 1 || Duel.Phase != DuelPhase.Main1
                || Duel.MainPhase == null || !Duel.MainPhase.CanBattlePhase || !Enemy.IsFieldEmpty()
                || ReadyAttackDamage(Bot.GetMonsters()) == 0;
        }

        private PlanMove NextPlanMove => _boardPlan != null && _planStep < _boardPlan.Moves.Count
            ? _boardPlan.Moves[_planStep] : null;

        private void RegisterBoardPlanner()
        {
            AddExecutor(ExecutorType.GoToEndPhase, PlanEndPhase);
            AddExecutor(ExecutorType.SpellSet, -1, () => _settleDrawPressure && DefaultSpellSet()
                || _finishWithBackrow && CanSetRemainingTwinTrap(Card));
            AddExecutor(ExecutorType.GoToEndPhase, () => _settleDrawPressure || _finishWithBackrow);
            foreach (ExecutorType kind in new[] { ExecutorType.Activate, ExecutorType.SpSummon, ExecutorType.Summon, ExecutorType.SpellSet })
                AddExecutor(kind, -1, ExecuteBoardMove);
        }

        private OpponentDrawEffects LiveDrawEffects() => new OpponentDrawEffects {
            MaxxC = enemyResolvedEffectIdList.Count(id => id == _CardId.MaxxC),
            Fuwalos = enemyResolvedEffectIdList.Count(id => id == Fuwalos),
            Purulia = enemyResolvedEffectIdList.Count(id => id == _CardId.MulcharmyPurulia),
            Nyalus = enemyResolvedEffectIdList.Count(id => id == _CardId.MulcharmyNyalus),
            Moon = _moonDrawPressure ? 1 : 0,
        };

        private bool HasOpponentDrawPressure => _moonDrawPressure || enemyResolvedEffectIdList.Count != 0;

        private static CardLocation PlannedSummonSource(PlanMove move)
        {
            if (move.SummonSource != 0)
                return move.SummonSource;
            // Older opening tables predate source metadata. Treat ambiguous sources as possible.
            if (move.Type == ExecutorType.Summon)
                return CardLocation.Hand;
            if (move.Type == ExecutorType.SpSummon)
                return move.Card == Frost || move.Card == Treat ? CardLocation.Hand : CardLocation.Extra;
            if (move.Type != ExecutorType.Activate)
                return 0;
            switch (move.Card)
            {
                case KiSikil:
                case LilLa:
                    return CardLocation.Deck | CardLocation.Hand;
                case Requiem:
                    return move.Effect == 0 ? CardLocation.Deck | CardLocation.Hand : 0;
                case NightPhoto:
                    return move.Effect == 0 ? CardLocation.Hand : CardLocation.Grave;
                case NewYear:
                    return move.Effect == 0 ? CardLocation.Hand | CardLocation.Grave : 0;
                case Sequence:
                case Nightfall:
                    return move.Fusion != 0 || move.Card == Nightfall && move.Effect == 1 ? CardLocation.Extra : 0;
                case EvilKiSikil:
                case EvilLilLa:
                case Heist:
                case Yama:
                case Sweet:
                    return move.Effect == 1 ? CardLocation.Grave : 0;
                case LacrimaFusion:
                case Sunny:
                    return move.Effect == 0 ? CardLocation.Grave : 0;
                case Engraver:
                    return move.Effect == 2 ? CardLocation.Grave : 0;
                case Abao:
                    return move.Option == 3 ? CardLocation.Grave : 0;
                default:
                    return 0;
            }
        }

        private static bool WouldGiveDraw(PlanMove move, int drawCard)
        {
            if (drawCard == MoonSqueeze)
                return move.Type == ExecutorType.Activate && (move.Card == Trouble || move.Card == Password
                    || move.Card == NewYear && move.Effect == 1);
            CardLocation source = PlannedSummonSource(move);
            switch (drawCard)
            {
                case _CardId.MaxxC: return source != 0 && move.Type != ExecutorType.Summon;
                case _CardId.MulcharmyFuwalos: return (source & (CardLocation.Deck | CardLocation.Extra)) != 0;
                case _CardId.MulcharmyPurulia: return (source & CardLocation.Hand) != 0;
                case _CardId.MulcharmyNyalus: return (source & (CardLocation.Grave | CardLocation.Removed)) != 0;
                default: return false;
            }
        }

        private bool ShouldSaveCounterForDrawEffect()
        {
            var response = Duel.CurrentChainInfo.LastOrDefault();
            if (response == null || response.ActivatePlayer != 1)
                return false;
            int id = response.ActivateAlias != 0 ? response.ActivateAlias : response.ActivateId;
            if (id == MoonSqueeze && response.HasAnnounce(Util.GetStringId(MoonSqueeze, 2)))
                return true;
            if (!InBoardBuildPhase || _boardPlan == null || !response.IsActivateCode(
                _CardId.MaxxC, _CardId.MulcharmyFuwalos, _CardId.MulcharmyPurulia, _CardId.MulcharmyNyalus, MoonSqueeze))
                return false;
            var pending = Duel.CurrentChainInfo.Where(chain => chain.ActivatePlayer == 0)
                .Select(chain => _planActivations.TryGetValue(PlanCode(chain.RelatedCard), out var move)
                    ? move : new PlanMove { Type = ExecutorType.Activate, Card = PlanCode(chain.RelatedCard),
                        Effect = LivePlanEffect(chain, PlanCode(chain.RelatedCard)) });
            return pending.Concat(_boardPlan.Moves.Skip(_planStep)).Where(move => WouldGiveDraw(move, id))
                .Take(2).Count() <= 1;
        }

        public override void OnDraw(int player)
        {
            var chain = Duel.GetCurrentSolvingChainInfo();
            if (player == 1 && chain != null && chain.ActivatePlayer == 1 && chain.IsActivateCode(MoonSqueeze))
                _moonDrawOnly = chain;
            if (player == 0 && InBoardBuildPhase && _boardPlan != null)
            {
                _planDrawPending = true;
                int drawn = PlanCode(Bot.Hand.LastOrDefault());
                if (drawn != 0 && !Bot.HasInDeck(drawn) && _boardPlan.Moves.Skip(_planStep).Any(move =>
                    DrawInvalidatesDeckMove(move, drawn, Bot.HasInGraveyard(drawn))))
                    _planInventoryChanged = true;
            }
            base.OnDraw(player);
        }

        private static bool DrawInvalidatesDeckMove(PlanMove move, int drawn, bool inGrave) =>
            move.Target == drawn && (move.Card == Deal || move.Card == Scout || IsTwinSearch(move)
                || move.TargetLocation == CardLocation.Deck)
            || !inGrave && move.Card == Nightfall && move.Traps?.Contains(drawn) == true;

        public override void OnChainSolved(int chainIndex)
        {
            var chain = Duel.GetCurrentSolvingChainInfo();
            if (chain != null && chain.ActivatePlayer == 1 && chain.IsActivateCode(MoonSqueeze)
                && !Duel.IsCurrentSolvingChainNegated())
            {
                if (chain.HasAnnounce(Util.GetStringId(MoonSqueeze, 1))
                    || !chain.HasAnnounce(Util.GetStringId(MoonSqueeze, 2)) && chain != _moonDrawOnly)
                    _moonDrawPressure = true;
            }
            base.OnChainSolved(chainIndex);
        }

        private PlanPosition LivePlanPosition()
        {
            var result = new PlanPosition
            {
                Field = Bot.MonsterZone.Take(7).Select(PlanCode).ToArray(),
                UsesExtraZones = Duel.IsNewRule,
                FusionNeedsLink = Duel.IsNewRule && !Duel.IsNewRule2020,
                BlockedExtraZones = (Enemy.MonsterZone[5] != null ? 1 << 6 : 0)
                    | (Enemy.MonsterZone[6] != null ? 1 << 5 : 0),
                Hand = Bot.Hand.Select(PlanCode).ToList(),
                Grave = Bot.Graveyard.Select(PlanCode).ToList(),
                Deck = PlanMainCards.SelectMany(id => Enumerable.Repeat(id, Bot.GetCardCountInDeck(id))).ToList(),
                Extra = PlanExtraCards.Where(id => Bot.HasInExtra(id)).ToList(),
                SpellSlots = Bot.SpellZone.Take(5).Count(card => card != null),
                Trouble = Bot.SpellZone.Any(card => card != null && card.IsCode(Trouble) && card.IsFaceup()),
                Used = _planUsed, Normal = _normalSummoned, Requiem = _requiemSummoned,
                OwnSummons = _planOwnSummons, EarlyRequiem = _planEarlyRequiem,
                RequiemDefenseAtNibiruWindow = _planRequiemDefenseAtNibiruWindow,
                PhotoAtNibiruWindow = _planPhotoAtNibiruWindow, SearchLocked = _searchLocked,
                FirstCaesarSummonMove = Bot.HasInMonstersZone(Caesar) ? 0 : int.MaxValue,
                FirstYamaGraveMove = Bot.HasInGraveyard(Yama) || Bot.Banished.Any(card => card.IsCode(Yama)) ? 0 : int.MaxValue,
                PasswordBanishes = Bot.Banished.Count(card => card.IsCode(Password)),
                NewYearBanished = Bot.Banished.Count(card => card.IsCode(NewYear)),
                DealLinked = _planDealLinked, Fiends = _planFiends, Twins = _planTwins,
                Draws = _planDraws, ReturningAbao = AbaoIsReturning() ? 1 : 0,
                DrawEffects = LiveDrawEffects(),
            };
            if (_nightfallEffectUsed)
                result.Used |= Flag(PlanEffect.Nightfall) | Flag(PlanEffect.NightfallRotate);
            if (_sunnyFieldEffectUsed)
                result.Used |= Flag(PlanEffect.Sunny);
            foreach (var card in Bot.GetMonsters())
            {
                if (!card.IsSpecialSummoned)
                    result.NormalZones |= 1 << card.Sequence;
                if (card == _planReturningNewYear)
                    result.NewYearZone = card.Sequence;
                if (card.IsCode(Desirae))
                {
                    if (card.EquipCards.Any(equip => equip.IsCode(Requiem))) result.Equipment |= 1;
                    if (card.EquipCards.Any(equip => equip.IsCode(Sequence))) result.Equipment |= 2;
                }
            }
            foreach (var card in Bot.Graveyard.Where(card => !card.IsCanRevive()))
                result.Unrevivable |= ExtraFlag(PlanCode(card));
            foreach (var card in Bot.SpellZone.Where(card => card != null && card.IsFacedown()))
                result.Set |= TrapBit(PlanCode(card));
            return result;
        }

        private bool MatchesPlanActivation(PlanMove move, ClientCard card, int desc)
        {
            if (move == null || move.Type != ExecutorType.Activate || PlanCode(card) != move.Card)
                return false;
            if (move.ActivationLocation != 0 && card.Location != move.ActivationLocation)
                return false;
            if (move.Card == Requiem || move.Card == Sequence || move.Card == NewYear || move.Card == NightPhoto
                || move.Card == Sunny || move.Card == Engraver || move.Card == Nightfall
                || move.Card == EvilLilLa || move.Card == EvilKiSikil || move.Card == Heist || move.Card == Abao)
            {
                if (desc < 0)
                    return move.Trigger;
                return desc == Util.GetStringId(move.Card, move.Effect)
                    || desc == Util.GetStringId(card.Id, move.Effect);
            }
            return desc <= 0 || desc == Util.GetStringId(move.Card, move.Effect)
                || desc == Util.GetStringId(card.Id, move.Effect);
        }

        private bool PlanOffered(PlanMove move)
        {
            var main = Duel.MainPhase;
            if (main == null || move == null)
                return false;
            if (move.Type == ExecutorType.Activate)
                return FollowsBoardPriorities(move) && PlanCanPay(move)
                    && main.ActivableCards.Where((card, index) => MatchesPlanActivation(move, card, main.ActivableDescs[index])).Any();
            if (move.Type == ExecutorType.Summon) return main.SummonableCards.Any(card => PlanCode(card) == move.Card);
            if (move.Type == ExecutorType.SpSummon)
                return main.SpecialSummonableCards.Any(card => PlanCode(card) == move.Card)
                    && (move.Materials == null || MatchPlanMaterials(move) != null);
            return move.Type == ExecutorType.SpellSet && main.SpellSetableCards.Any(card => PlanCode(card) == move.Card);
        }

        private bool UseOpeningPolicy()
        {
            if (Duel.Turn != 1 || _normalSummoned || Bot.GetMonsters().Count != 0 || Duel.MainPhase == null)
                return false;
            return Duel.MainPhase.SummonableCards.Any(card => IsTwin(PlanCode(card)))
                || Duel.MainPhase.ActivableCards.Any(card => card.IsCode(Trouble, Password));
        }

        private bool PlanEndPhase()
        {
            if (!CanPlanBoard() || _planStopped || _settleDrawPressure || _finishWithBackrow)
                return false;
            // Open through the existing ordered starter policy. The normal summon and
            // its triggered chain can establish a real position before lookahead.
            if (_boardPlan == null && !HasOpponentDrawPressure && UseOpeningPolicy())
                return false;
            if (!ReferenceEquals(_planMain, Duel.MainPhase))
            {
                _planMain = Duel.MainPhase;
                _planPlacement = null;
                _planPayment = null;
                _planMaterialCards = null;
                bool needsPlan = _planInterrupted || _planInventoryChanged || _boardPlan == null
                    || NextPlanMove != null && !PlanOffered(NextPlanMove);
                if (needsPlan || _planDrawPending)
                {
                    _planDrawPending = false;
                    if (_planInterrupted)
                        TracePlanner("recovery", "opponent-chain");
                    else if (_planInventoryChanged)
                        TracePlanner("inventory-replan", string.Empty);
                    else if (needsPlan && _boardPlan != null)
                        TracePlanner(FollowsBoardPriorities(NextPlanMove) ? "resync" : "policy-replan", NextPlanMove.ToString());
                    bool recovering = _planInterrupted;
                    _planInterrupted = _planInventoryChanged = false;

                    var timer = Stopwatch.StartNew();
                    var position = LivePlanPosition();
                    var plan = needsPlan ? ChooseBoardPlan(position, timer, recovering)
                        : ImprovePlanAfterDraw(position, timer);
                    if (plan != null)
                    {
                        _boardPlan = plan;
                        _planOwnerTurn = Duel.Turn;
                        TracePlanner("window", Duel.Turn + " " + Duel.Phase + " "
                            + (position.Normal ? 1 : 0) + " " + position.Used);
                        TracePlanner("search-ms", timer.ElapsedMilliseconds.ToString());
                        _planStep = 0;
                        _planReturnZone = _boardPlan.ReturnZone;
                        TracePlanner("plan", string.Join(" ", _boardPlan.Score, _boardPlan.States,
                            _boardPlan.Cutoffs, string.Join(" ", _boardPlan.Moves)));
                        if (position.DrawEffects.Active && _boardPlan.Score <= 0)
                        {
                            _settleDrawPressure = true;
                            TracePlanner("draw-pressure-stop", _boardPlan.Score.ToString());
                            return false;
                        }
                        if (_boardPlan.Moves.Count == 0 && !_normalSummoned && Bot.GetMonsters().Count == 0
                            && position.Hand.Any(id => IsTwin(id) || id == Trouble || id == Password))
                        {
                            _boardPlan = null;
                            _planStopped = true;
                            return false;
                        }
                    }
                }
            }
            if (_boardPlan == null || NextPlanMove != null)
                return false;

            var terminal = LivePlanPosition();
            int actual = EndBoardScore(terminal, true);
            if (_planReturnZone < 0)
                _planReturnZone = terminal.AbaoZone;
            TracePlanner("end", actual.ToString());
            if (terminal.DrawEffects.Active)
            {
                _settleDrawPressure = true;
                return false;
            }
            // Drawn traps are unknown to the route simulator until the engine adds them to hand.
            if (Duel.MainPhase.SpellSetableCards.Any(CanSetRemainingTwinTrap))
            {
                _finishWithBackrow = true;
                return false;
            }
            return true;
        }

        private PlanResult ImprovePlanAfterDraw(PlanPosition position, Stopwatch timer)
        {
            var remaining = _boardPlan.Moves.Skip(_planStep).ToList();
            int current = EndBoardScore(position, true);
            int floor = current;
            PlanPosition retained = position;
            if (remaining.Count != 0)
            {
                var first = ComboSuccessors(position, BoardSearchPolicy.Tactical)
                    .Where(next => PlanOffered(next.Move) && MatchesCachedMove(next.Move, remaining[0])).ToList();
                var replay = ReplayCachedOpening(first, remaining, timer, 100, requirePlacement: true,
                    policy: BoardSearchPolicy.Tactical);
                if (replay.Count == 0 && position.DrawEffects.Active)
                {
                    TracePlanner("draw-replan", "keep unavailable-suffix");
                    return null;
                }
                retained = null;
                foreach (var terminal in replay)
                {
                    terminal.Score = EndBoardScore(terminal, true);
                    if (retained == null || terminal.Score > retained.Score
                        || terminal.Score == retained.Score && PreferEnd(terminal, retained))
                        retained = terminal;
                }
                floor = Math.Max(current, retained?.Score ?? _boardPlan.Score);
            }

            var tactical = TryTacticalContinuation(position, timer, 230);
            var candidate = FindBoard(position, PlanOffered, 96, 10000, timer, 550, BoardSearchPolicy.HoldBackrow);
            if (tactical != null && BetterBoardPlan(tactical, candidate))
                candidate = tactical;
            bool improved = candidate.Score > floor || candidate.Score == floor && retained != null
                && ((candidate.Terminal.Set & SetChallenge) > (retained.Set & SetChallenge)
                    || CompareCaesarTiming(candidate.Terminal, retained) > 0)
                && PreferEnd(candidate.Terminal, retained);
            TracePlanner("draw-replan", string.Join(" ", floor, candidate.Score,
                improved ? "upgrade" : "keep", timer.ElapsedMilliseconds));
            return improved ? candidate : null;
        }

        private bool CanSetRemainingTwinTrap(ClientCard card) => card.IsCode(Challenge, Disconnect)
            && CanSetForEndBoard() && !HasSetTrap(PlanCode(card));

        private bool FollowsBoardPriorities(PlanMove move)
        {
            if (move.Type != ExecutorType.Activate)
                return true;
            if (move.Card == Deal || move.Card == Scout)
            {
                if (!Bot.HasInDeck(move.Target) && !Bot.HasInExtra(move.Target))
                    return false;
                int preferred = LivePreferredTwinSend(move.Card, id => Bot.HasInDeck(id));
                return preferred == 0 || move.Target == preferred
                    || move.Card == Scout && move.Target == Frost && LiveCanSendFrostWithScout();
            }
            if (move.Card != Abao || move.Discard == 0)
                return true;
            return PlannedAbaoDiscard(Bot.Hand, move.Discard) != null;
        }

        private PlanResult ChooseBoardPlan(PlanPosition position, Stopwatch timer, bool recovering)
        {
            int width = PlannerTestLimit("TWINS_PLAN_WIDTH", 0);
            bool tableEligible = Duel.Turn == 1 && Duel.Phase == DuelPhase.Main1
                && !_planOtherInterference && !_searchLocked && !position.DrawEffects.Active && Enemy.IsFieldEmpty()
                && Bot.GetMonsters().All(card => card.IsFaceup() && !card.IsDisabled());
            bool sameOpening = width != 0 && !recovering
                && Environment.GetEnvironmentVariable("TWINS_PLAN_FREEZE_OPENING") == "1";
            var tacticalPlan = TryTacticalContinuation(position, timer, 260);
            if (!recovering && position.OwnSummons == 2 && position.Has(KiSikil) && position.Has(LilLa)
                && !position.SearchLocked && (position.Deck.Contains(NightPhoto) || position.Hand.Contains(NightPhoto))
                && position.Deck.Contains(NewYear)
                && position.Fresh(PlanEffect.Scout) && position.Fresh(PlanEffect.NewYearJump)
                && (tacticalPlan == null || !HasNibiruDefense(tacticalPlan.Terminal)
                    || position.Hand.Contains(NightPhoto) && !HasDesiraeControlBoard(tacticalPlan.Terminal)))
            {
                var protectedPlan = TryTacticalContinuation(position, timer, 500,
                    move => move.Card == Scout && PlanOffered(move));
                if (protectedPlan != null && (tacticalPlan == null || BetterBoardPlan(protectedPlan, tacticalPlan)))
                    tacticalPlan = protectedPlan;
            }
            if ((width == 0 || sameOpening) && tableEligible && TryTablePlan(position, recovering, timer, out PlanResult tablePlan))
            {
                if (tacticalPlan != null && tacticalPlan.Score > tablePlan.Score)
                {
                    if (sameOpening)
                        TracePlanner("reference", tacticalPlan.Score + " " + tacticalPlan.Score + " incumbent");
                    TracePlanner("tactic-plan", tacticalPlan.Score.ToString());
                    TracePlanner("policy-evaluations", tacticalPlan.States + " " + tacticalPlan.ScoreEvaluations);
                    return tacticalPlan;
                }
                var alternative = FindEqualPlanWithBetterPriorities(position, tablePlan, timer);
                if (sameOpening)
                    TracePlanner("reference", tablePlan.Score + " " + tablePlan.Score + " incumbent");
                TracePlanner("table-hit", tablePlan.Score.ToString());
                TracePlanner("policy-evaluations", "0 0");
                return alternative ?? tablePlan;
            }

            // Compare two tactical priorities only within the foreground time budget.
            // The offline reference has its own budget and never runs in production.
            // Leave headroom for the engine response and the client's transport.
            const int timeLimitMs = 3000;
            int firstPassMs = recovering ? 1600 : 1250;
            if (tacticalPlan != null && width == 0 && !position.DrawEffects.Active
                && (tacticalPlan.Score >= 95 || tacticalPlan.Score >= 85 && HasDesiraeControlBoard(tacticalPlan.Terminal))
                && position.Hand.All(IsTableInert)
                && tacticalPlan.Terminal.PasswordBanishes == position.PasswordBanishes)
            {
                var alternative = FindEqualPlanWithBetterPriorities(position, tacticalPlan, timer);
                TracePlanner("tactic-plan", tacticalPlan.Score.ToString());
                TracePlanner("policy-evaluations", tacticalPlan.States + " " + tacticalPlan.ScoreEvaluations);
                return alternative ?? tacticalPlan;
            }
            bool photoEngine = position.Has(NightPhoto) && position.Fresh(PlanEffect.PhotoRevive)
                && PhotoRevivalCost(position) != 0 && position.Grave.Any(id => Evil(id) && position.CanRevive(id))
                && position.Fresh(PlanEffect.Fusion) && position.Extra.Contains(Sequence)
                && Duel.MainPhase.SpecialSummonableCards.Any(card => card.IsCode(Sequence));
            var incumbent = photoEngine
                ? FindBoard(position, move => move.Type == ExecutorType.SpSummon && move.Card == Sequence && PlanOffered(move),
                    192, 28000, timer, firstPassMs, BoardSearchPolicy.DevelopEngines)
                : FindBoard(position, PlanOffered, 144, 24000, timer, firstPassMs, BoardSearchPolicy.HoldBackrow);
            int nodes = incumbent.States + (tacticalPlan?.States ?? 0);
            int evaluations = incumbent.ScoreEvaluations + (tacticalPlan?.ScoreEvaluations ?? 0);
            if (tacticalPlan != null && BetterBoardPlan(tacticalPlan, incumbent))
            {
                incumbent = tacticalPlan;
                TracePlanner("tactic-plan", incumbent.Score.ToString());
            }
            if (incumbent.Score <= 0 && position.DrawEffects.Active && !position.Normal && Occupied(position.Field) == 0
                && timer.ElapsedMilliseconds < timeLimitMs - 300)
            {
                var starter = FindDrawPressureStarter(position, timer, timeLimitMs);
                if (starter != null)
                {
                    nodes += starter.States;
                    evaluations += starter.ScoreEvaluations;
                    if (BetterBoardPlan(starter, incumbent))
                    {
                        incumbent = starter;
                        TracePlanner("draw-pressure-starter", starter.Score.ToString());
                    }
                }
            }
            if (incumbent.Score < 75 && timer.ElapsedMilliseconds < timeLimitMs - 250)
            {
                var alternate = RefineFiendsmithFusion(incumbent, timer, Math.Min(timeLimitMs - 120,
                    (int)timer.ElapsedMilliseconds + 350));
                if (alternate != null)
                {
                    nodes += alternate.States;
                    evaluations += alternate.ScoreEvaluations;
                    if (BetterBoardPlan(alternate, incumbent))
                        incumbent = alternate;
                }
            }
            if (incumbent.Score < 68 && timer.ElapsedMilliseconds < timeLimitMs - 120)
            {
                if (!photoEngine)
                {
                    int refinementDeadline = Math.Min(timeLimitMs - 320, (int)timer.ElapsedMilliseconds + 260);
                    var refined = RefineEngineChoice(incumbent, timer, refinementDeadline);
                    if (refined != null)
                    {
                        nodes += refined.States;
                        evaluations += refined.ScoreEvaluations;
                        if (BetterBoardPlan(refined, incumbent))
                            incumbent = refined;
                    }
                }
                if (incumbent.Score < 68 && timer.ElapsedMilliseconds < timeLimitMs - 180)
                {
                    var developed = FindBoard(position, PlanOffered, 256, 45000, timer, timeLimitMs,
                        photoEngine ? BoardSearchPolicy.HoldBackrow : BoardSearchPolicy.ResourceExchange);
                    nodes += developed.States;
                    evaluations += developed.ScoreEvaluations;
                    if (BetterBoardPlan(developed, incumbent))
                        incumbent = developed;
                }
            }
            if (incumbent.Score < 90 && timer.ElapsedMilliseconds < timeLimitMs - 300)
            {
                var fork = FindRestartFork(incumbent, timer, timeLimitMs - 300);
                if (fork != null)
                {
                    var continuation = FindBoard(fork, null, 96, 24000, timer, timeLimitMs,
                        BoardSearchPolicy.ContinueEffects);
                    nodes += continuation.States;
                    evaluations += continuation.ScoreEvaluations;
                    if (BetterBoardPlan(continuation, incumbent))
                        incumbent = continuation;
                }
            }
            if (!position.DrawEffects.Active && timer.ElapsedMilliseconds < timeLimitMs - 25)
            {
                var alternate = RefineDesiraeFinish(incumbent, timer, timeLimitMs);
                if (alternate != null)
                {
                    nodes += alternate.States;
                    evaluations += alternate.ScoreEvaluations;
                    if (BetterBoardPlan(alternate, incumbent))
                    {
                        incumbent = alternate;
                        TracePlanner("tactic-finish", incumbent.Score.ToString());
                    }
                }
            }
            if (timer.ElapsedMilliseconds < timeLimitMs - 25)
                incumbent = FinishWithCaesar(incumbent, timer, timeLimitMs);
            TracePlanner("policy-evaluations", nodes + " " + evaluations);
            if (width == 0)
                return incumbent;

            int limit = PlannerTestLimit("TWINS_PLAN_STATES", 360000);
            int referenceBudget = PlannerTestLimit("TWINS_PLAN_REFERENCE_MS", 10000);
            int remaining = (int)Math.Max(0, referenceBudget - _referenceSearchMs);
            var referenceTimer = Stopwatch.StartNew();
            var additional = remaining == 0 ? null : FindBoard(position, PlanOffered, width, limit, referenceTimer, remaining);
            _referenceSearchMs += referenceTimer.ElapsedMilliseconds;
            TracePlanner("reference-ms", referenceTimer.ElapsedMilliseconds.ToString());
            bool improved = additional != null && BetterBoardPlan(additional, incumbent);
            TracePlanner("reference", string.Join(" ", incumbent.Score, additional?.Score ?? incumbent.Score,
                improved ? "additional" : "incumbent"));
            var selected = improved ? additional : incumbent;
            if (tableEligible && Environment.GetEnvironmentVariable("TWINS_PLAN_EXPORT") == "1")
                ExportTablePlan(position, selected, recovering);
            return selected;
        }

        private static bool HasDesiraeControlBoard(PlanPosition p) =>
            p.Has(Desirae) && p.Has(Gryphon) && p.Has(Nightfall)
            && (p.ReturningAbao != 0 || p.Has(Abao))
            && (p.Set & (SetChallenge | SetDisconnect)) == (SetChallenge | SetDisconnect);

        private PlanResult RefineDesiraeFinish(PlanResult incumbent, Stopwatch timer, int deadline)
        {
            const int twinBackrow = SetChallenge | SetDisconnect;
            if ((incumbent.Terminal.Set & twinBackrow) == twinBackrow)
                return null;
            for (var step = incumbent.Terminal.Previous; step?.Previous != null; step = step.Previous)
                if (CanFinishDesiraeWithTwins(step))
                    return TryTacticalContinuation(step, timer, deadline, move => true);
            return null;
        }

        private PlanResult FinishWithCaesar(PlanResult incumbent, Stopwatch timer, int deadline)
        {
            var terminal = incumbent.Terminal;
            if (terminal == null || !terminal.Has(LacrimaFusion) || !terminal.Extra.Contains(Caesar)
                || terminal.Twins || !terminal.Has(Engraver) && !terminal.Grave.Contains(Engraver))
                return incumbent;
            var ready = terminal.Has(Engraver) ? new[] { terminal }
                : BodyMoves(terminal).Where(next => next.Move.Card == Engraver && next.Move.Effect == 2);
            foreach (var before in ready)
            foreach (var after in ExtraMoves(before, Caesar))
            {
                if (timer.ElapsedMilliseconds >= deadline)
                    return incumbent;
                if (terminal.Previous == null && !PlanOffered(before == terminal ? after.Move : before.Move))
                    continue;
                after.Score = EndBoardScore(after, true);
                if (after.Score <= incumbent.Score)
                    continue;
                var moves = new List<PlanMove>();
                for (var step = after; step.Previous != null; step = step.Previous)
                    if (step.Move != null)
                        moves.Add(step.Move);
                moves.Reverse();
                incumbent = new PlanResult { Moves = moves, Terminal = after, Score = after.Score,
                    ReturnZone = after.AbaoZone, States = incumbent.States, Cutoffs = incumbent.Cutoffs,
                    ScoreEvaluations = incumbent.ScoreEvaluations + 1 };
            }
            return incumbent;
        }

        private PlanResult FindDrawPressureStarter(PlanPosition start, Stopwatch timer, int deadline)
        {
            var continuations = new List<PlanPosition>();
            foreach (int id in start.Hand.Distinct().Where(id => id == KiSikil || id == LilLa))
            {
                var move = new PlanMove { Card = id, Type = ExecutorType.Summon };
                if (!PlanOffered(move))
                    continue;
                var normal = start.Next(move);
                normal.Hand.Remove(id);
                normal.Normal = true;
                var placed = PlaceBody(normal, id, 2, CardLocation.Hand, normal: true);
                continuations.AddRange(PendingMoves(placed).Where(next => next.Move.Card == id
                    && next.Move.Effect == 0 && (next.Move.Target == KiSikil || next.Move.Target == LilLa)));
            }

            PlanResult best = null;
            foreach (var afterStarter in continuations.OrderBy(candidate => candidate.OpponentDraws))
            {
                if (timer.ElapsedMilliseconds >= deadline)
                    break;
                var candidate = FindBoard(afterStarter, null, 144, 24000, timer, deadline, BoardSearchPolicy.HoldBackrow);
                if (best == null || BetterBoardPlan(candidate, best))
                    best = candidate;
            }
            return best;
        }

        private static bool IsTwinSearch(PlanMove move) => move.Type == ExecutorType.Activate
            && (move.Card == Password || move.Card == Trouble);

        private static bool DowngradesLink(PlanMove move) => move.Type == ExecutorType.SpSummon
            && LinkRating(move.Card) == 1 && move.MaterialIds?.Length == 1 && LinkRating(move.MaterialIds[0]) == 2;

        private static int CompareCachedOpeningPriorities(PlanResult candidate, PlanResult known)
        {
            int searches = known.Moves.Count(IsTwinSearch).CompareTo(candidate.Moves.Count(IsTwinSearch));
            if (searches != 0)
                return searches;
            int downgrades = known.Moves.Count(DowngradesLink).CompareTo(candidate.Moves.Count(DowngradesLink));
            if (downgrades != 0)
                return downgrades;
            return !known.Moves.Any(IsTwinSearch) ? 0 : candidate.Moves.TakeWhile(move => !IsTwinSearch(move)).Count()
                .CompareTo(known.Moves.TakeWhile(move => !IsTwinSearch(move)).Count());
        }

        private PlanResult FindEqualPlanWithBetterPriorities(PlanPosition position, PlanResult known, Stopwatch timer)
        {
            bool usesSearch = known.Moves.Any(IsTwinSearch);
            bool downgrades = known.Moves.Any(DowngradesLink);
            bool usesPhoto = !UsedPhoto(position) && known.Moves.Any(UsesPhoto);
            bool sendsTwin = known.Moves.Any(move => move.Type == ExecutorType.Activate
                && (move.Card == Deal || move.Card == Scout));
            bool canImproveRequiemTiming = !position.EarlyRequiem && position.OwnSummons < NibiruThreshold
                && position.Extra.Contains(Requiem) && known.Terminal?.EarlyRequiem != true;
            bool canImproveCaesarTiming = known.Moves.Any(move => move.Type == ExecutorType.SpSummon && move.Card == Caesar);
            if ((!usesSearch && !downgrades && !usesPhoto && !sendsTwin && !canImproveRequiemTiming && !canImproveCaesarTiming)
                || timer.ElapsedMilliseconds >= 400)
                return null;
            var policy = usesSearch || downgrades || sendsTwin ? BoardSearchPolicy.HoldBackrow : BoardSearchPolicy.PreservePhoto;
            var alternative = FindBoard(position, PlanOffered, 96, 12000, timer, known.Terminal == null ? 330 : 400, policy);
            TracePlanner("policy-evaluations", alternative.States + " " + alternative.ScoreEvaluations);
            if (alternative.Score != known.Score || alternative.Moves.Count == 0)
                return null;
            if (known.Terminal == null)
            {
                int opening = CompareCachedOpeningPriorities(alternative, known);
                if (opening < 0)
                    return null;
                if (opening == 0)
                {
                    var first = ComboSuccessors(position).Where(next => PlanOffered(next.Move)).ToList();
                    var replay = ReplayCachedOpening(first, known.Moves, timer, 400);
                    if (replay.Count == 0 || !PreferEnd(alternative.Terminal, replay[0]))
                        return null;
                }
            }
            else if (!PreferEnd(alternative.Terminal, known.Terminal))
                return null;
            TracePlanner("preferred-equal-plan", alternative.Score.ToString());
            return alternative;
        }

        private static bool BetterBoardPlan(PlanResult candidate, PlanResult incumbent)
        {
            if (candidate.Score != incumbent.Score)
                return candidate.Score > incumbent.Score;
            return PreferEnd(candidate.Terminal, incumbent.Terminal);
        }

        private PlanResult RefineEngineChoice(PlanResult incumbent, Stopwatch timer, int timeLimitMs)
        {
            var path = new List<PlanPosition>();
            for (var step = incumbent.Terminal; step != null && step.Previous != null; step = step.Previous)
                if (step.Move != null)
                    path.Add(step);
            path.Reverse();

            var dealShortcut = path.FirstOrDefault(step => step.Move.Type == ExecutorType.SpSummon
                && step.Move.Card != Requiem && step.Move.MaterialIds != null && step.Move.MaterialIds.Contains(Deal)
                && !step.Previous.Requiem && step.Previous.Extra.Contains(Requiem));
            if (dealShortcut != null)
            {
                var before = dealShortcut.Previous;
                return FindBoard(before, move => move.Type == ExecutorType.SpSummon && move.Card == Requiem
                    && move.MaterialIds != null && move.MaterialIds.Contains(Deal)
                    && (before.Previous != null || PlanOffered(move)),
                    192, 27000, timer, timeLimitMs, BoardSearchPolicy.ResourceExchange);
            }

            foreach (var step in path)
            {
                var before = step.Previous;
                var move = step.Move;
                Func<PlanMove, bool> alternative = null;
                if (move.Type == ExecutorType.Activate && move.Card == Trouble && move.Target != NightPhoto
                    && before.Deck.Contains(NightPhoto) && before.Grave.Contains(Password)
                    && (before.Grave.Any(IsTwin) || before.Field.Any(IsTwin))
                    && before.Extra.Contains(Sequence))
                {
                    alternative = candidate => candidate.Card == Trouble && candidate.Target == NightPhoto;
                }
                else if (move.Type == ExecutorType.SpSummon && move.Card == EvilLilLa
                    && before.Hand.Contains(NightPhoto) && before.Extra.Contains(Deal) && !before.DealLinked
                    && before.Field.Any(IsRedTwin) && before.Extra.Contains(Requiem))
                {
                    alternative = candidate => candidate.Type == ExecutorType.SpSummon && candidate.Card == Deal;
                }
                else if (move.Type == ExecutorType.Activate && move.Card == Sweet && before.Has(Deal)
                    && before.Extra.Contains(Requiem) && !before.Requiem && before.Deck.Contains(Engraver))
                {
                    alternative = candidate => candidate.Type == ExecutorType.SpSummon && candidate.Card == Requiem;
                }

                if (alternative == null)
                    continue;
                Func<PlanMove, bool> root = before.Previous == null
                    ? new Func<PlanMove, bool>(candidate => alternative(candidate) && PlanOffered(candidate))
                    : alternative;
                return FindBoard(before, root, 192, 27000, timer, timeLimitMs, BoardSearchPolicy.ResourceExchange);
            }
            return null;
        }

        private PlanResult RefineFiendsmithFusion(PlanResult incumbent, Stopwatch timer, int deadline)
        {
            for (var step = incumbent.Terminal; step?.Previous != null; step = step.Previous)
            {
                var before = step.Previous;
                bool spentEngraver = step.Move?.Fusion == LacrimaFusion && step.Move.Selection.Contains(Engraver);
                if ((step.Move?.Fusion != Desirae && !spentEngraver) || !before.Extra.Contains(LacrimaFusion)
                    || !before.Extra.Contains(Caesar) || !before.Grave.Contains(Engraver))
                    continue;
                return FindBoard(before, move => move.Fusion == LacrimaFusion && !move.Selection.Contains(Engraver)
                    && (before.Previous != null || PlanOffered(move)), 96, 12000, timer, deadline,
                    BoardSearchPolicy.ResourceExchange);
            }
            return null;
        }

        private PlanPosition FindRestartFork(PlanResult incumbent, Stopwatch timer, int deadline)
        {
            var path = new Stack<PlanPosition>();
            for (var position = incumbent.Terminal; position != null; position = position.Previous)
                path.Push(position);
            while (path.Count > 0 && timer.ElapsedMilliseconds < deadline)
            {
                var before = path.Pop();
                if (!before.Has(Engraver) || !before.Extra.Contains(Abao) || !before.Extra.Contains(Yama)
                    || !before.Fresh(PlanEffect.BlueRevive) || !before.Grave.Contains(Deal))
                    continue;
                foreach (var branch in NextPositions(before, BoardSearchPolicy.ContinueEffects))
                {
                    if (ImmediateContinuationKind(branch) != ContinuationKind.Deal
                        || before.Previous == null && !PlanOffered(branch.Move))
                        continue;
                    var restart = branch;
                    // Walk only exchanges that get closer to an empty-board Twin revival.
                    while (timer.ElapsedMilliseconds < deadline)
                    {
                        if (restart.Has(Abao) && restart.Field.Count(id => id != 0) == 1 && CanRestart(restart))
                            return restart;
                        int rank = RestartSetupRank(restart);
                        var next = NextPositions(restart, BoardSearchPolicy.ContinueEffects)
                            .Where(candidate => RestartSetupRank(candidate) > rank)
                            .OrderByDescending(candidate => candidate.Fresh(PlanEffect.Yama)
                                && (candidate.Has(Yama) || candidate.Grave.Contains(Yama)))
                            .FirstOrDefault();
                        if (next == null)
                            break;
                        restart = next;
                    }
                }
            }
            return null;
        }

        private static int PlannerTestLimit(string variable, int fallback)
        {
            if (Environment.GetEnvironmentVariable("TWINS_PLAN_TRACE") == "1"
                && int.TryParse(Environment.GetEnvironmentVariable(variable), out int result) && result > 0)
                return result;
            return fallback;
        }

        private static void TracePlanner(string eventName, string data)
        {
            if (Environment.GetEnvironmentVariable("TWINS_PLAN_TRACE") == "1")
                Console.WriteLine("TwinsPlan {0} {1}", eventName, data);
        }

        public override bool OnPreActivate(ClientCard card)
        {
            if (card.IsCode(Sweet) && card.Location == CardLocation.Hand && EnemyChained())
                return true;
            if (card.IsCode(Frost) && card.Location == CardLocation.Grave && Duel.Player == 0 && !_searchLocked)
                return true;
            if (InBoardBuildPhase && _boardPlan != null && !_planStopped && !_planInterrupted && !_settleDrawPressure)
            {
                var move = NextPlanMove;
                return MatchesPlanActivation(move, card, ActivateDescription) &&
                    (Duel.CurrentChain.Count == 0 || move.Trigger);
            }
            if (InBoardBuildPhase && HasOpponentDrawPressure && !EnemyChained())
                return card.IsCode(Nightfall, EvilKiSikil)
                    && MatchesPlanActivation(Effect(PlanCode(card), 0, trigger: true), card, ActivateDescription);
            return base.OnPreActivate(card);
        }

        private bool ExecuteBoardMove()
        {
            var move = NextPlanMove;
            if (!InBoardBuildPhase || _planStopped || _planInterrupted || _settleDrawPressure
                || move == null || move.Type != Type || PlanCode(Card) != move.Card
                || Type == ExecutorType.Activate && (!MatchesPlanActivation(move, Card, ActivateDescription)
                    || !FollowsBoardPriorities(move)))
                return false;

            if (move.Materials != null)
            {
                var materials = MatchPlanMaterials(move);
                if (materials == null)
                    return false;
                _planMaterialCards = materials;
                AI.SelectMaterials(materials, move.Card == Caesar ? HintMsg.XyzMaterial : HintMsg.LinkMaterial);
            }

            _planStep++;
            _planPlacement = move;
            if (move.Type == ExecutorType.Activate)
            {
                _planPayment = move;
                _planActivations[move.Card] = move;
                if (move.Card == Nightfall)
                    _planSetTraps = move.Traps?.ToList();
                if (move.Card == Abao && move.Option == 3)
                    _abaoReviveTarget = Bot.Graveyard.FirstOrDefault(card => PlanCode(card) == move.Target);
                var flag = PlanFlag(move);
                if (flag.HasValue)
                    _planUsed |= Flag(flag.Value);
            }
            if (move.Card == Deal && move.Type == ExecutorType.SpSummon)
                _planDealLinked = true;
            TracePlanner("move", move.ToString());
            return true;
        }

        private bool PlanCanPay(PlanMove move)
        {
            if (move.Discard == TableInertDiscard && !Bot.Hand.Any(card => IsTableInert(PlanCode(card))))
                return false;
            if (move.Fusion != 0)
                return move.Selection.All(id => Bot.Graveyard.Any(card => PlanCode(card) == id));
            if (move.Card == Sunny)
                return move.Selection != null && move.Selection.All(HasLiveRevival);
            if (move.Card == Abao && move.Option == 3 || move.Card == NightPhoto && move.Effect == 1
                || move.Card == Heist || (move.Card == EvilKiSikil || move.Card == EvilLilLa) && move.Effect == 1)
                return HasLiveRevival(move.Target);
            return true;
        }

        private bool HasLiveRevival(int id) => Bot.Graveyard.Any(card => PlanCode(card) == id && card.IsCanRevive());

        private IList<ClientCard> MatchPlanMaterials(PlanMove move)
        {
            var selected = new List<ClientCard>();
            for (int i = 0; i < move.Materials.Length; i++)
            {
                int id = move.MaterialIds[i];
                ClientCard card = Bot.MonsterZone[move.Materials[i]];
                if (PlanCode(card) != id || selected.Contains(card))
                    card = Bot.GetMonsters().FirstOrDefault(monster => PlanCode(monster) == id && !selected.Contains(monster));
                if (card == null)
                    return null;
                selected.Add(card);
            }
            return selected;
        }

        private bool LiveGryphonCoLinked()
        {
            var gryphon = Bot.GetMonsters().FirstOrDefault(card => card.IsCode(Gryphon));
            if (gryphon == null)
                return false;
            return Bot.GetMonsters().Any(card => card != gryphon &&
                (gryphon.GetLinkedZones() & (1 << card.Sequence)) != 0 &&
                (card.GetLinkedZones() & (1 << gryphon.Sequence)) != 0);
        }

        private static PlanEffect? PlanFlag(PlanMove move)
        {
            if (move.Effect < 0)
                return null;
            switch (move.Card)
            {
                case Trouble: return PlanEffect.Trouble;
                case Password: return PlanEffect.Password;
                case Deal: return PlanEffect.Deal;
                case Scout: return PlanEffect.Scout;
                case NewYear: return move.Effect == 0 ? PlanEffect.NewYearJump : PlanEffect.NewYearSearch;
                case KiSikil: return PlanEffect.SmallRed;
                case LilLa: return PlanEffect.SmallBlue;
                case Sweet: return move.Effect == 1 ? (PlanEffect?)PlanEffect.Sweet : null;
                case Frost: return move.Effect == 1 ? (PlanEffect?)PlanEffect.FrostDraw : null;
                case NightPhoto: return move.Effect == 0 ? PlanEffect.Photo : PlanEffect.PhotoRevive;
                case Requiem: return move.Effect == 0 ? PlanEffect.Requiem : PlanEffect.RequiemEquip;
                case Engraver: return move.Effect == 2 ? (PlanEffect?)PlanEffect.Engraver : null;
                case EvilKiSikil: return move.Effect == 0 ? PlanEffect.RedDraw : PlanEffect.RedRevive;
                case EvilLilLa: return move.Effect == 0 ? PlanEffect.BluePop : PlanEffect.BlueRevive;
                case Heist: return move.Effect == 1 ? (PlanEffect?)PlanEffect.HeistRevive : null;
                case Abao: return move.Effect == 0 ? (PlanEffect?)PlanEffect.Abao : null;
                case Yama: return PlanEffect.Yama;
                case Sunny: return move.Effect == 0 ? (PlanEffect?)PlanEffect.Sunny : null;
                case Nightfall: return move.Effect == 0 ? PlanEffect.Nightfall : PlanEffect.NightfallRotate;
                case Sequence: return move.Effect == 0 ? PlanEffect.Fusion : PlanEffect.SequenceEquip;
                case LacrimaFusion: return move.Effect == 0 ? (PlanEffect?)PlanEffect.FusionRevive : null;
                case Gryphon: return PlanEffect.GryphonSet;
                default: return null;
            }
        }

        private IList<ClientCard> PlanChooseCards(IList<ClientCard> cards, int min, int max, int hint, bool cancelable)
        {
            if (_planStopped || _planInterrupted || _boardPlan == null || !InBoardBuildPhase)
                return null;
            if (_planMaterialCards != null && (hint == HintMsg.LinkMaterial || hint == HintMsg.XyzMaterial))
            {
                var materials = _planMaterialCards.Where(cards.Contains).Take(max).ToList();
                if (materials.Count >= min)
                    return materials;
            }
            ChainInfo chain = Duel.GetCurrentSolvingChainInfo() ?? Duel.CurrentChainInfo.LastOrDefault();
            PlanMove move = null;
            if (hint == HintMsg.Discard || hint == HintMsg.Remove || hint == HintMsg.ToGrave)
                move = _planPayment;
            if (move == null && chain != null && chain.ActivatePlayer == 0)
            {
                int source = PlanCode(chain.RelatedCard);
                _planActivations.TryGetValue(source, out move);
            }
            if (move == null)
                move = _planPayment;
            if (move == null)
                return null;

            int[] desired = null;
            if (hint == HintMsg.Discard && (move.Discard >= 0 || move.Discard == TableInertDiscard))
            {
                if (move.Card == Abao)
                {
                    var choices = cards.Where(c => c.Location == CardLocation.Hand).ToList();
                    var discard = PlannedAbaoDiscard(choices, move.Discard);
                    if (discard == null)
                    {
                        _planInventoryChanged = true;
                        discard = AbaoDiscards(choices).FirstOrDefault();
                    }
                    return discard == null ? null : new[] { discard };
                }
                if (move.Discard == TableInertDiscard)
                {
                    var inert = cards.Where(c => c.Location == CardLocation.Hand && IsTableInert(PlanCode(c)))
                        .OrderBy(AbaoHandValue).FirstOrDefault();
                    return inert == null ? null : new[] { inert };
                }
                if (move.Discard == 0)
                {
                    var card = cards.Where(c => c.Location == CardLocation.Hand).OrderBy(AbaoHandValue).FirstOrDefault();
                    return card == null ? null : new[] { card };
                }
                desired = new[] { move.Discard };
            }
            else if (hint == HintMsg.Set && move.Card == Nightfall && _planSetTraps != null)
                desired = _planSetTraps.Take(1).ToArray();
            else if (hint == HintMsg.FusionMaterial && move.Fusion != 0)
                desired = move.Selection;
            else if (hint == HintMsg.SpSummon && move.Fusion != 0)
                desired = new[] { move.Fusion };
            else if (hint == HintMsg.SpSummon && move.Selection != null)
                desired = move.Selection;
            else if (hint == HintMsg.Remove && move.Cost != 0)
                desired = new[] { move.Cost };
            else if (move.Target != 0 && PlanTargetHint(move.Card, hint))
                desired = new[] { move.Target };

            if (desired == null || max == 0)
                return null;
            // Fusion and revival selection may arrive through several one-card prompts.
            var selected = desired.Select(id => cards.FirstOrDefault(card => PlanCode(card) == id
                    && (hint != HintMsg.SpSummon || move.TargetLocation == 0 || card.Location == move.TargetLocation)))
                .Where(card => card != null).Distinct().Take(max).ToList();
            if (selected.Count == 0 && min == 0 && cancelable)
                return selected;
            if (selected.Count < min || selected.Count == 0)
            {
                TracePlanner("selection-mismatch", hint + " " + move + " candidates=" + string.Join(",", cards.Select(PlanCode)));
                return null;
            }
            if (hint == HintMsg.Set && move.Card == Nightfall && _planSetTraps != null)
                _planSetTraps.RemoveAt(0);
            return selected;
        }

        private static bool PlanTargetHint(int card, int hint)
        {
            if (card == Deal || card == Scout) return hint == HintMsg.ToGrave;
            if (card == Password || card == Trouble || card == NewYear && hint == HintMsg.AddToHand)
                return hint == HintMsg.AddToHand;
            if (card == Engraver) return hint == HintMsg.ToDeck;
            if (card == NightPhoto) return hint == HintMsg.ToDeck || hint == HintMsg.Target || hint == HintMsg.SpSummon;
            if (card == NewYear) return hint == HintMsg.Release;
            if (card == Gryphon) return hint == HintMsg.Set || hint == HintMsg.Target;
            if (card == Requiem || card == Sequence || card == LacrimaFusion)
                return hint == HintMsg.SpSummon || hint == HintMsg.Target || hint == HintMsg.Equip;
            return hint == HintMsg.SpSummon || hint == HintMsg.Target || hint == HintMsg.Destroy;
        }

        private int BoardPlanPlace(int cardId, int player, CardLocation location, int available)
        {
            if (player != 0 || location != CardLocation.MonsterZone || _boardPlan == null || _planStopped)
                return 0;
            if (InBoardBuildPhase && _planPlacement != null)
            {
                if (_planPlacement.SummonedIds != null && cardId != 0)
                {
                    for (int i = 0; i < _planPlacement.SummonedIds.Length; i++)
                    {
                        int requested = 1 << _planPlacement.SummonedZones[i];
                        if ((_planPlacement.SummonedIds[i] == cardId || cardId == 9205574 && _planPlacement.SummonedIds[i] == EvilKiSikil
                            || cardId == 36609519 && _planPlacement.SummonedIds[i] == EvilLilLa) && (available & requested) != 0)
                            return requested;
                    }
                }
                int masks = _planPlacement.Places & available;
                if (masks != 0)
                    return masks;
                if (_planPlacement.Places != 0)
                    TracePlanner("placement-mismatch", _planPlacement + " " + available);
            }
            if (Duel.Player == 0 && Duel.Phase == DuelPhase.End && _planReturnZone >= 0 && AbaoIsReturning())
                return available & (1 << _planReturnZone);
            return 0;
        }

        private int BoardPlanOption(IList<int> options)
        {
            if (InBoardBuildPhase && _planPayment != null && _planPayment.Card == Abao)
                return options.IndexOf(Util.GetStringId(Abao, _planPayment.Option));
            return -1;
        }

        private bool? BoardPlanYesNo(int description)
        {
            if (!InBoardBuildPhase || _boardPlan == null || _planStopped)
                return null;
            if (description == Util.GetStringId(Nightfall, 2) && _planSetTraps != null)
                return _planSetTraps.Count != 0;
            if (description == Util.GetStringId(Yama, 2))
                return false;
            return null;
        }

        private void PlanOpponentChained(ClientCard source)
        {
            // Track lingering effects from Standby as well as Main Phase.
            // Ash leaves no lingering rule effect after the chain.
            if (Duel.Player == 0 && Duel.Turn == 1 && (source == null || !source.IsCode(_CardId.AshBlossom)))
                _planOtherInterference = true;
            if (!InBoardBuildPhase)
                return;
            // Keep the attempted effects in the turn ledger. Let chain responses
            // settle via the normal policy and rebuild at the next idle prompt.
            _planInterrupted = true;
            _planPlacement = null;
            _planMaterialCards = null;
            TracePlanner("interrupted", string.Empty);
        }

        private static int LivePlanEffect(ChainInfo chain, int card)
        {
            int desc = chain.ActivateDescription;
            if (desc > 0 && (desc / 16 == chain.ActivateId || desc / 16 == card))
                return desc % 16;
            switch (card)
            {
                case NewYear: return chain.ActivateLocation == CardLocation.MonsterZone ? 1 : 0;
                case NightPhoto: return chain.ActivateLocation == CardLocation.Hand ? 0 : 1;
                case Sweet: return chain.ActivateLocation == CardLocation.Grave ? 1 : -1;
                case Frost: return chain.ActivateLocation == CardLocation.Grave ? 1 : -1;
                case Engraver: return chain.ActivateLocation == CardLocation.Grave ? 2 : -1;
                case Sequence: return chain.ActivateLocation == CardLocation.Grave ? 1 : 0;
                case Requiem: return chain.ActivateLocation == CardLocation.MonsterZone ? 0 : 1;
                case Heist: return -1;
                default: return 0;
            }
        }

        private void RecordBoardEffects()
        {
            for (int i = 0; i < Duel.CurrentChainInfo.Count; i++)
            {
                var chain = Duel.CurrentChainInfo[i];
                if (chain.ActivatePlayer != 0)
                    continue;
                int id = chain.ActivateAlias != 0 ? chain.ActivateAlias : chain.ActivateId;
                bool planned = _planActivations.TryGetValue(id, out PlanMove move);
                int effect = planned ? move.Effect : LivePlanEffect(chain, id);
                var flag = PlanFlag(new PlanMove { Card = id, Effect = effect });
                if (!flag.HasValue)
                    continue;
                _planUsed |= Flag(flag.Value);
                if (Duel.NegatedChainIndexList.Contains(i + 1))
                    continue;
                _planFiends |= flag == PlanEffect.RedRevive || flag == PlanEffect.BlueRevive;
                _planTwins |= flag == PlanEffect.HeistRevive;
                if (flag == PlanEffect.RedDraw || flag == PlanEffect.FrostDraw || flag == PlanEffect.GryphonSet && LiveGryphonCoLinked())
                    _planDraws++;
                if (flag == PlanEffect.NewYearJump && chain.ActivateLocation == CardLocation.Grave
                    && chain.RelatedCard.Location == CardLocation.MonsterZone)
                    _planReturningNewYear = chain.RelatedCard;
            }
        }

        private void ResetBoardPlanner()
        {
            _boardPlan = null;
            _planStep = _planOwnerTurn = _planDraws = 0;
            _referenceSearchMs = 0;
            _planReturnZone = -1;
            _planStopped = _planInterrupted = _planDealLinked = _planTwins = _planFiends = _planOtherInterference = _planInventoryChanged = false;
            _planDrawPending = false;
            _planUsed = 0;
            _moonDrawPressure = _settleDrawPressure = _finishWithBackrow = false;
            _moonDrawOnly = null;
            _planMain = null;
            _planPlacement = _planPayment = null;
            _planMaterialCards = null;
            _planReturningNewYear = null;
            _planActivations.Clear();
            _planSetTraps = null;
        }

        private void PlanChainEnded()
        {
            _planActivations.Clear();
            _planPayment = null;
        }

        private void RecordPlanEnd()
        {
            if (_planOwnerTurn == 0 || Duel.Turn != _planOwnerTurn + 1 || _boardPlan == null)
                return;
            // Compare the terminal projection with the server's positions after End Phase returns.
            _returningAbao = null;
            var observed = LivePlanPosition();
            TracePlanner("observed", EndBoardScore(observed, true) + " "
                + string.Join(",", observed.Field) + " " + observed.Set + " " + observed.Draws);
        }
    }
}
