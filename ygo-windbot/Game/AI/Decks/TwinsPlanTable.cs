using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Security.Cryptography;
using System.Text;
using YGOSharp.OCGWrapper.Enums;

namespace WindBot.Game.AI.Decks
{
    public partial class TwinsExecutor
    {
        private const int TableInertDiscard = -2;
        private const int TableVersion = 2;
        private static readonly Lazy<Dictionary<string, TablePlan>> OpeningTable =
            new Lazy<Dictionary<string, TablePlan>>(() => ReadOpeningTable(Path.Combine("Decks", "AI_Twins.plan.jsonl")));
        private static bool SameComboCards(int[] first, int[] second)
        {
            return (first ?? Array.Empty<int>()).OrderBy(id => id)
                .SequenceEqual((second ?? Array.Empty<int>()).OrderBy(id => id));
        }

        private static bool MatchesCachedMove(PlanMove legal, PlanMove saved)
        {
            return legal != null && legal.Type == saved.Type && legal.Card == saved.Card
                && legal.Effect == saved.Effect && legal.Target == saved.Target
                && (legal.Cost == saved.Cost || legal.Card == NightPhoto && legal.Effect == 1
                    && saved.Cost == Password && legal.Cost == Trouble)
                && legal.Fusion == saved.Fusion && legal.Option == saved.Option
                && (saved.Discard == TableInertDiscard ? IsTableInert(legal.Discard) : legal.Discard == saved.Discard)
                && SameComboCards(legal.MaterialIds, saved.MaterialIds)
                && SameComboCards(legal.Selection, saved.Selection) && SameComboCards(legal.Traps, saved.Traps);
        }

        private IEnumerable<PlanPosition> ComboSuccessors(PlanPosition position,
            BoardSearchPolicy policy = BoardSearchPolicy.Reference)
        {
            foreach (var next in NextPositions(position, policy))
            {
                if (next.Move != null)
                    yield return next;
                else
                    foreach (var afterPass in NextPositions(next, policy))
                        if (afterPass.Move != null)
                            yield return afterPass;
            }
        }

        private List<PlanPosition> ReplayCachedOpening(List<PlanPosition> firstMoves, IList<PlanMove> moves,
            Stopwatch timer, int deadline, bool requirePlacement = false,
            BoardSearchPolicy policy = BoardSearchPolicy.Reference)
        {
            var states = firstMoves;
            for (int index = 0; index < moves.Count; index++)
            {
                if (timer.ElapsedMilliseconds >= deadline)
                    return new List<PlanPosition>();
                var move = moves[index];
                var successors = index == 0 ? states : states.SelectMany(position => ComboSuccessors(position, policy));
                states = successors.Where(next => MatchesCachedMove(next.Move, move))
                    .Where(next => !requirePlacement || next.Move.Places == move.Places
                        && SameComboCards(next.Move.Materials, move.Materials)
                        && (next.Move.SummonedZones ?? Array.Empty<int>()).SequenceEqual(move.SummonedZones ?? Array.Empty<int>()))
                    .OrderByDescending(next => next.Move.Places == move.Places)
                    .ThenByDescending(next => (next.Move.Materials ?? Array.Empty<int>())
                        .SequenceEqual(move.Materials ?? Array.Empty<int>()))
                    .GroupBy(next => next.SearchKey()).Select(group => group.First()).Take(4).ToList();
                if (states.Count == 0)
                    break;
            }
            return states;
        }

        private sealed class BoardTactic
        {
            public readonly Func<PlanPosition, bool> Ready;
            public readonly Func<PlanPosition, PlanMove, bool>[] Steps;

            public BoardTactic(Func<PlanPosition, bool> ready, params Func<PlanPosition, PlanMove, bool>[] steps)
            {
                Ready = ready;
                Steps = steps;
            }
        }

        private static Func<PlanPosition, PlanMove, bool> ActivateTactic(int card, int effect, int target = 0) =>
            (state, move) => move.Type == ExecutorType.Activate && move.Card == card && move.Effect == effect
                && (target == 0 || move.Target == target);

        private static Func<PlanPosition, PlanMove, bool> SummonTactic(int card, params int[] requiredMaterials) =>
            (state, move) => move.Type == ExecutorType.SpSummon && move.Card == card
                && requiredMaterials.All(material => move.MaterialIds != null && move.MaterialIds.Contains(material));

        private static Func<PlanPosition, PlanMove, bool> SummonWithExactMaterials(int card, params int[] materials) =>
            (state, move) => move.Type == ExecutorType.SpSummon && move.Card == card
                && SameComboCards(move.MaterialIds, materials);

        // Each tactic starts from usable resources. Its legal targets, materials and
        // placements are resolved anew, so it can also run after an interrupted line.
        private static readonly BoardTactic[] BoardTactics =
        {
            new BoardTactic(p => p.Field.Any(IsRedTwin) && p.Field.Any(IsBlueTwin) && p.Deck.Contains(Sweet)
                    && p.Fresh(PlanEffect.Deal) && p.Fresh(PlanEffect.Requiem) && p.Fresh(PlanEffect.Sweet),
                SummonTactic(Deal), ActivateTactic(Deal, 0, Sweet), ActivateTactic(Sweet, 1),
                SummonTactic(Requiem, Deal), ActivateTactic(Requiem, 0, Engraver)),
            new BoardTactic(p => p.Has(KiSikil) && p.Has(LilLa) && p.Trouble
                    && !p.Hand.Contains(NightPhoto) && p.Deck.Contains(Sweet)
                    && p.Fresh(PlanEffect.Deal) && p.Fresh(PlanEffect.Sweet)
                    && p.Fresh(PlanEffect.Requiem) && p.Fresh(PlanEffect.BlueRevive),
                SummonTactic(Deal, KiSikil), ActivateTactic(Deal, 0, Sweet), ActivateTactic(Sweet, 1),
                SummonTactic(Requiem, Deal), ActivateTactic(Requiem, 0, Engraver),
                SummonTactic(EvilLilLa, LilLa, Sweet), ActivateTactic(EvilLilLa, 1, Deal),
                SummonTactic(Yama, Deal, Engraver)),
            new BoardTactic(p => p.Has(Yama) && p.Has(EvilLilLa) && p.Field.Count(id => id != 0) == 2
                    && p.Trouble && p.Hand.Count > 0 && !p.Hand.Contains(NightPhoto)
                    && p.Grave.Contains(LilLa) && p.Deck.Contains(Frost) && p.Fresh(PlanEffect.Abao)
                    && p.Fresh(PlanEffect.SmallBlue),
                SummonWithExactMaterials(Abao, Yama, EvilLilLa), ActivateTactic(Abao, 0, LilLa),
                ActivateTactic(LilLa, 0, Frost)),
            new BoardTactic(p => p.Field.Any(IsBlueTwin) && !p.Field.Any(IsRedTwin) && p.Deck.Contains(NewYear)
                    && p.Fresh(PlanEffect.Scout) && p.Fresh(PlanEffect.NewYearJump)
                    && p.Fresh(PlanEffect.NewYearSearch) && !p.Hand.Contains(NewYear)
                    && !(p.Has(Engraver) && p.Has(LilLa) && p.Fresh(PlanEffect.SmallBlue)),
                SummonTactic(Scout), ActivateTactic(Scout, 0, NewYear),
                ActivateTactic(NewYear, 0, Scout), ActivateTactic(NewYear, 1, Password)),
            new BoardTactic(p => p.Has(NewYear) && p.Grave.Contains(Sweet) && p.Fresh(PlanEffect.Sweet)
                    && p.Fresh(PlanEffect.Deal) && p.Fresh(PlanEffect.Requiem) && p.Deck.Contains(LilLa),
                SummonTactic(Deal, NewYear), ActivateTactic(Deal, 0, LilLa),
                ActivateTactic(Sweet, 1), SummonTactic(Requiem, Deal)),
            new BoardTactic(p => p.Has(NewYear) && p.Has(KiSikil) && p.Grave.Contains(Scout)
                    && p.Hand.Contains(Password) && (p.Deck.Contains(NightPhoto) || p.Hand.Contains(NightPhoto))
                    && !p.Hand.Contains(Sweet) && p.Deck.Contains(Sweet)
                    && p.Fresh(PlanEffect.Deal) && p.Fresh(PlanEffect.Sweet) && p.Fresh(PlanEffect.Requiem),
                SummonTactic(Deal, KiSikil), ActivateTactic(Deal, 0, Sweet), ActivateTactic(Sweet, 1),
                SummonTactic(Requiem, Deal), ActivateTactic(Requiem, 0, Engraver)),
            new BoardTactic(CanTurnSweetIntoAbao,
                SummonWithExactMaterials(EvilLilLa, NewYear, Sweet), ActivateTactic(EvilLilLa, 1, Deal),
                SummonWithExactMaterials(Yama, Deal, Engraver),
                SummonWithExactMaterials(Abao, Yama, EvilLilLa)),
            new BoardTactic(p => p.Has(Abao) && p.Field.Count(id => id != 0) == 1
                    && p.Grave.Contains(KiSikil) && p.Grave.Contains(Yama)
                    && p.Grave.Contains(EvilLilLa) && p.CanRevive(EvilLilLa) && p.Deck.Contains(LilLa)
                    && p.Hand.Contains(Password) && p.Deck.Contains(Trouble)
                    && p.Deck.Contains(NightPhoto) && p.Hand.Count > 1 && !p.SearchLocked
                    && p.Fresh(PlanEffect.Password) && p.Fresh(PlanEffect.Trouble)
                    && p.Fresh(PlanEffect.Abao) && p.Fresh(PlanEffect.SmallRed) && p.Fresh(PlanEffect.RedRevive),
                ActivateTactic(Password, 0, Trouble), ActivateTactic(Trouble, 0, NightPhoto),
                ActivateTactic(Abao, 0, KiSikil), ActivateTactic(KiSikil, 0, LilLa),
                SummonWithExactMaterials(EvilKiSikil, KiSikil, LilLa),
                ActivateTactic(EvilKiSikil, 1, EvilLilLa)),
            new BoardTactic(p => p.Has(EvilKiSikil) && p.Has(EvilLilLa) && p.Trouble && p.Hand.Contains(NightPhoto)
                    && p.Grave.Contains(Yama) && p.Grave.Contains(Scout) && p.CanRevive(Scout)
                    && p.Grave.Contains(Engraver) && p.Grave.Contains(Deal)
                    && p.Extra.Contains(Sequence) && p.ReturningAbao != 0
                    && p.Fresh(PlanEffect.BluePop) && p.Fresh(PlanEffect.Yama) && p.Fresh(PlanEffect.Engraver),
                ActivateTactic(EvilLilLa, 0, Trouble), ActivateTactic(Yama, 1, Scout),
                ActivateTactic(Engraver, 2, Deal), SummonWithExactMaterials(Sequence, Scout, Engraver)),
            new BoardTactic(p => p.Has(NewYear) && p.Has(KiSikil) && p.Hand.Contains(Trouble)
                    && p.Deck.Contains(Sweet) && p.Deck.Contains(NightPhoto)
                    && p.Fresh(PlanEffect.Deal) && p.Fresh(PlanEffect.Sweet) && p.Fresh(PlanEffect.Requiem)
                    && p.Fresh(PlanEffect.BlueRevive) && p.Fresh(PlanEffect.SmallRed),
                SummonTactic(Deal, KiSikil), ActivateTactic(Deal, 0, Sweet), ActivateTactic(Sweet, 1),
                SummonTactic(Requiem, Deal), ActivateTactic(Requiem, 0, Engraver),
                SummonTactic(EvilLilLa, NewYear, Sweet), ActivateTactic(EvilLilLa, 1, Deal),
                SummonTactic(Yama, Deal, Engraver)),
            new BoardTactic(p => p.Has(Yama) && p.Has(EvilLilLa)
                    && p.Field.Count(id => id != 0) == 2 && p.Grave.Contains(KiSikil)
                    && p.Deck.Contains(LilLa) && p.Hand.Contains(Trouble) && p.Hand.Count > 1
                    && p.Fresh(PlanEffect.SmallRed) && p.Fresh(PlanEffect.Abao),
                SummonTactic(Abao, Yama, EvilLilLa), ActivateTactic(Abao, 0, KiSikil),
                ActivateTactic(KiSikil, 0, LilLa)),
            new BoardTactic(p => p.Has(NewYear) && p.Has(Sweet) && p.Hand.Contains(NightPhoto)
                    && p.Grave.Contains(Scout) && p.CanRevive(Scout) && p.Grave.Contains(Deal)
                    && p.Grave.Contains(Requiem) && p.Fresh(PlanEffect.RedRevive)
                    && p.Fresh(PlanEffect.Fusion) && p.Fresh(PlanEffect.Nightfall),
                SummonTactic(EvilKiSikil, NewYear, Sweet), ActivateTactic(EvilKiSikil, 1, Scout),
                SummonTactic(Sequence, EvilKiSikil, Scout),
                (p, move) => move.Type == ExecutorType.Activate && move.Card == Sequence
                    && move.Fusion == LacrimaFusion && move.Selection.Contains(Deal) && move.Selection.Contains(Requiem),
                ActivateTactic(LacrimaFusion, 0, EvilKiSikil), SummonTactic(Nightfall, Sequence, EvilKiSikil),
                ActivateTactic(Nightfall, 0)),
            new BoardTactic(p => p.Has(Nightfall) && p.Has(LacrimaFusion) && p.Hand.Contains(NightPhoto)
                    && p.Grave.Contains(Scout) && p.Grave.Contains(EvilKiSikil) && p.CanRevive(EvilKiSikil)
                    && p.Fresh(PlanEffect.Photo) && p.Fresh(PlanEffect.PhotoRevive)
                    && PhotoRevivalCost(p) != 0 && p.Extra.Contains(Yama) && p.Extra.Contains(Abao),
                ActivateTactic(NightPhoto, 0, Scout), SummonTactic(Yama, Nightfall, LacrimaFusion),
                ActivateTactic(NightPhoto, 1, EvilKiSikil), SummonTactic(Abao, Yama, EvilKiSikil)),
            new BoardTactic(p => p.Has(LilLa) && p.Has(Frost) && p.Extra.Contains(Scout)
                    && p.Grave.Contains(EvilKiSikil) && p.CanRevive(EvilKiSikil)
                    && p.Fresh(PlanEffect.BlueRevive) && p.Fresh(PlanEffect.RedDraw),
                SummonTactic(Scout, LilLa), SummonTactic(EvilLilLa, Frost, Scout),
                ActivateTactic(EvilLilLa, 1, EvilKiSikil), ActivateTactic(EvilKiSikil, 0)),
            new BoardTactic(p => p.Has(EvilKiSikil) && p.Has(EvilLilLa) && p.Trouble && p.Grave.Contains(Yama)
                    && p.Grave.Contains(Engraver) && p.Hand.Contains(Treat)
                    && p.Fresh(PlanEffect.BluePop) && p.Fresh(PlanEffect.Yama) && p.Fresh(PlanEffect.Sunny)
                    && p.Fresh(PlanEffect.RedDraw) && p.Fresh(PlanEffect.Fusion) && p.Fresh(PlanEffect.Nightfall)
                    && p.Fresh(PlanEffect.Engraver) && p.Extra.Contains(Caesar),
                ActivateTactic(EvilLilLa, 0, Trouble),
                (p, move) => move.Type == ExecutorType.Activate && move.Card == Yama
                    && move.Effect == 1 && LightFiend(move.Target),
                SummonWithExactMaterials(Sunny, EvilKiSikil, EvilLilLa), SplitTacticalPair,
                ActivateTactic(EvilKiSikil, 0), SummonTactic(Treat),
                (p, move) => move.Type == ExecutorType.SpSummon && move.Card == Sequence
                    && move.MaterialIds.Contains(Treat)
                    && move.MaterialIds.Any(id => id != EvilKiSikil && LightFiend(id)),
                SummonWithExactMaterials(Nightfall, EvilKiSikil, EvilLilLa), ActivateTactic(Nightfall, 0),
                (p, move) => move.Type == ExecutorType.Activate && move.Card == Sequence
                    && move.Fusion == LacrimaFusion && !move.Selection.Contains(Engraver)
                    && !move.Selection.Contains(EvilKiSikil),
                ActivateTactic(LacrimaFusion, 0, EvilKiSikil),
                SummonWithExactMaterials(Gryphon, Sequence, EvilKiSikil),
                ActivateTactic(Engraver, 2, Sequence), SummonWithExactMaterials(Caesar, Engraver, LacrimaFusion)),
            new BoardTactic(p => p.Has(EvilKiSikil) && p.Has(EvilLilLa) && p.Trouble && p.Grave.Contains(Yama)
                    && p.Fresh(PlanEffect.BluePop) && p.Fresh(PlanEffect.Yama) && p.Fresh(PlanEffect.Sunny)
                    && p.Extra.Contains(Gryphon) && (p.Set & (SetChallenge | SetDisconnect)) == (SetChallenge | SetDisconnect),
                SummonTactic(Sunny, EvilKiSikil, EvilLilLa), SplitTacticalPair,
                ActivateTactic(EvilLilLa, 0, Trouble), ActivateTactic(Yama, 1, Sunny),
                SummonTactic(Gryphon, EvilKiSikil, EvilLilLa)),
            new BoardTactic(p => p.Has(NewYear) && p.Field.Any(id => id != NewYear && IsRedTwin(id))
                    && p.Hand.Contains(Trouble) && p.Deck.Contains(Sweet) && p.Grave.Contains(Scout)
                    && p.CanRevive(Scout) && p.Fresh(PlanEffect.Deal) && p.Fresh(PlanEffect.Sweet)
                    && p.Fresh(PlanEffect.RedRevive) && p.Fresh(PlanEffect.Nightfall),
                (p, move) => move.Type == ExecutorType.SpSummon && move.Card == Deal
                    && !move.MaterialIds.Contains(NewYear),
                ActivateTactic(Deal, 0, Sweet), SummonTactic(EvilKiSikil, NewYear, Deal),
                ActivateTactic(EvilKiSikil, 1, Scout), ActivateTactic(Sweet, 1),
                SummonTactic(EvilLilLa, Scout, Sweet), SummonTactic(Nightfall, EvilKiSikil, EvilLilLa),
                ActivateTactic(Nightfall, 0)),
            new BoardTactic(p => p.Has(Nightfall) && p.Hand.Contains(Trouble) && p.Hand.Contains(Password)
                    && p.Grave.Contains(Sweet) && p.Grave.Contains(EvilKiSikil) && p.Grave.Contains(EvilLilLa)
                    && p.CanRevive(EvilKiSikil) && p.CanRevive(EvilLilLa)
                    && p.Fresh(PlanEffect.BlueRevive) && p.Fresh(PlanEffect.RedDraw)
                    && p.Fresh(PlanEffect.Trouble) && p.Fresh(PlanEffect.Password)
                    && p.Fresh(PlanEffect.Photo) && p.Fresh(PlanEffect.PhotoRevive) && !p.SearchLocked,
                ActivateTactic(Trouble, 0, NightPhoto), ActivateTactic(Password, 0, Trouble),
                ActivateTactic(NightPhoto, 0, Sweet), ActivateTactic(NightPhoto, 1, EvilLilLa),
                ActivateTactic(EvilLilLa, 1, EvilKiSikil), ActivateTactic(EvilKiSikil, 0)),
            new BoardTactic(p => p.Has(Nightfall) && p.Has(EvilKiSikil) && p.Has(EvilLilLa)
                    && p.Grave.Contains(Deal) && p.Fresh(PlanEffect.Requiem) && p.Fresh(PlanEffect.Engraver)
                    && p.Fresh(PlanEffect.Fusion) && p.Extra.Contains(Caesar) && !p.Requiem,
                SummonTactic(Requiem, EvilKiSikil), ActivateTactic(Requiem, 0, Engraver),
                SummonTactic(Sequence, EvilLilLa, Engraver),
                (p, move) => move.Type == ExecutorType.Activate && move.Card == Sequence && move.Fusion == LacrimaFusion
                    && move.Selection.Contains(Deal) && move.Selection.Contains(Requiem),
                ActivateTactic(LacrimaFusion, 0, EvilKiSikil), SummonTactic(Gryphon, Sequence, EvilKiSikil),
                ActivateTactic(Engraver, 2, Sequence), SummonTactic(Caesar, Engraver, LacrimaFusion)),
            new BoardTactic(p => p.Has(Engraver) && p.Field.Any(IsBlueTwin) && p.Hand.Contains(NightPhoto)
                    && p.Grave.Contains(Deal) && p.Deck.Contains(NewYear)
                    && !(p.Has(LilLa) && p.Fresh(PlanEffect.SmallBlue))
                    && p.Fresh(PlanEffect.Scout) && p.Fresh(PlanEffect.BlueRevive)
                    && p.Fresh(PlanEffect.Engraver) && p.Fresh(PlanEffect.NewYearSearch),
                SummonTactic(Scout), ActivateTactic(Scout, 0, NewYear),
                SummonTactic(EvilLilLa, Scout, Engraver), ActivateTactic(Engraver, 2, Deal),
                ActivateTactic(EvilLilLa, 1, NewYear), ActivateTactic(NewYear, 1, Password)),
            new BoardTactic(p => p.Has(Engraver) && p.Fresh(PlanEffect.BlueRevive)
                    && p.Grave.Contains(Deal) && p.CanRevive(Deal) && p.Extra.Contains(Abao),
                (p, move) => move.Type == ExecutorType.SpSummon && move.Card == EvilLilLa
                    && !move.MaterialIds.Contains(Engraver),
                ActivateTactic(EvilLilLa, 1, Deal), SummonTactic(Yama, Deal, Engraver),
                SummonTactic(Abao, Yama, EvilLilLa)),
            new BoardTactic(p => p.Has(Engraver) && p.Field.Any(IsBlueTwin) && p.Grave.Contains(Requiem)
                    && p.Grave.Contains(Deal) && p.CanRevive(Deal) && p.Fresh(PlanEffect.BlueRevive)
                    && p.Fresh(PlanEffect.Engraver) && p.Extra.Contains(Yama) && p.Extra.Contains(Abao),
                SummonTactic(EvilLilLa, Engraver), ActivateTactic(EvilLilLa, 1, Deal),
                ActivateTactic(Engraver, 2, Requiem), SummonTactic(Yama, Deal, Engraver),
                SummonTactic(Abao, Yama, EvilLilLa)),
            new BoardTactic(p => p.Has(Engraver) && p.Has(Sweet) && p.Grave.Contains(Deal)
                    && p.Grave.Contains(Requiem) && p.CanRevive(Deal) && p.Extra.Contains(LacrimaFusion)
                    && p.Fresh(PlanEffect.Fusion) && p.Fresh(PlanEffect.RedRevive) && p.Fresh(PlanEffect.Nightfall),
                SummonTactic(Sequence, Engraver, Sweet),
                (p, move) => move.Type == ExecutorType.Activate && move.Card == Sequence
                    && move.Fusion == LacrimaFusion && !move.Selection.Contains(Deal),
                ActivateTactic(LacrimaFusion, 0, Deal), SummonTactic(EvilKiSikil, Deal, LacrimaFusion),
                ActivateTactic(EvilKiSikil, 1, Sweet), SummonTactic(Nightfall, Sequence, EvilKiSikil),
                ActivateTactic(Nightfall, 0)),
            new BoardTactic(p => p.Has(Nightfall) && p.Has(Sweet) && p.Grave.Contains(EvilKiSikil)
                    && p.CanRevive(EvilKiSikil) && p.Deck.Contains(NewYear) && p.Deck.Contains(Treat)
                    && p.Fresh(PlanEffect.Scout) && p.Fresh(PlanEffect.NewYearJump)
                    && p.Fresh(PlanEffect.NewYearSearch) && p.Fresh(PlanEffect.Password)
                    && p.Fresh(PlanEffect.Trouble) && p.Fresh(PlanEffect.BlueRevive) && !p.SearchLocked,
                SummonTactic(Scout, Sweet), ActivateTactic(Scout, 0, NewYear), ActivateTactic(NewYear, 0, Scout),
                ActivateTactic(NewYear, 1, Password), ActivateTactic(Password, 0, Trouble),
                ActivateTactic(Trouble, 0, Treat), SummonTactic(Treat)),
            new BoardTactic(p => p.Has(Treat) && p.Has(NewYear) && p.Grave.Contains(EvilKiSikil)
                    && p.CanRevive(EvilKiSikil) && p.Fresh(PlanEffect.BlueRevive)
                    && p.Fresh(PlanEffect.RedDraw) && p.Has(Nightfall) && p.Extra.Contains(Gryphon),
                SummonTactic(EvilLilLa, NewYear, Treat), ActivateTactic(EvilLilLa, 1, EvilKiSikil),
                ActivateTactic(EvilKiSikil, 0), SummonTactic(Gryphon, EvilKiSikil, EvilLilLa)),
            new BoardTactic(p => p.Has(Engraver) && p.Has(LilLa) && p.Has(Sweet)
                    && p.Hand.Contains(Trouble) && p.Deck.Contains(Frost) && p.Grave.Contains(Requiem)
                    && p.Grave.Contains(Deal) && p.CanRevive(Deal) && p.Extra.Contains(Nightfall)
                    && p.Fresh(PlanEffect.BlueRevive) && p.Fresh(PlanEffect.Engraver) && p.Fresh(PlanEffect.Trouble),
                SummonTactic(EvilLilLa, Engraver, LilLa), ActivateTactic(EvilLilLa, 1, Deal),
                ActivateTactic(Engraver, 2, Requiem), SummonTactic(Yama, Deal, Engraver),
                SummonTactic(Nightfall, Yama, EvilLilLa), ActivateTactic(Nightfall, 0)),
            new BoardTactic(p => p.Has(Nightfall) && p.Has(Sweet) && p.Hand.Contains(Trouble)
                    && p.Deck.Contains(Frost) && p.Fresh(PlanEffect.Trouble) && p.Fresh(PlanEffect.RedRevive)
                    && p.Grave.Contains(EvilLilLa) && p.CanRevive(EvilLilLa),
                ActivateTactic(Trouble, 0, Frost), SummonTactic(Frost),
                SummonTactic(EvilKiSikil, Frost, Sweet), ActivateTactic(EvilKiSikil, 1, EvilLilLa)),
            new BoardTactic(p => p.Has(Nightfall) && p.Has(EvilKiSikil) && p.Has(EvilLilLa)
                    && p.Trouble && p.Grave.Contains(Yama) && p.Grave.Contains(Engraver)
                    && p.Fresh(PlanEffect.Sunny) && p.Fresh(PlanEffect.RedDraw)
                    && p.Fresh(PlanEffect.BluePop) && p.Fresh(PlanEffect.Yama),
                SummonTactic(Sunny, EvilKiSikil, EvilLilLa),
                SplitTacticalPair,
                ActivateTactic(EvilKiSikil, 0), ActivateTactic(EvilLilLa, 0, Trouble), ActivateTactic(Yama, 1, Engraver)),
            new BoardTactic(p => p.Has(Nightfall) && p.Has(Engraver) && p.Has(EvilKiSikil) && p.Has(EvilLilLa)
                    && p.Grave.Contains(Sunny) && p.Fresh(PlanEffect.Scout) && p.Fresh(PlanEffect.NewYearJump)
                    && p.Fresh(PlanEffect.Fusion) && p.Deck.Contains(NewYear) && p.Extra.Contains(Caesar),
                SummonTactic(Scout, EvilLilLa), ActivateTactic(Scout, 0, NewYear), ActivateTactic(NewYear, 0, Scout),
                SummonTactic(Sequence, NewYear, EvilKiSikil),
                (p, move) => move.Type == ExecutorType.Activate && move.Card == Sequence && move.Fusion == LacrimaFusion,
                ActivateTactic(LacrimaFusion, 0, EvilKiSikil), SummonTactic(Caesar, Engraver, LacrimaFusion),
                SummonTactic(Gryphon, Sequence, EvilKiSikil)),
            new BoardTactic(p => p.Has(Abao) && p.Hand.Count > 0 && p.Fresh(PlanEffect.Abao)
                    && CanRestart(p) && Occupied(p.Field) == (1 << Array.IndexOf(p.Field, Abao)),
                (p, move) => move.Type == ExecutorType.Activate && move.Card == Abao
                    && CanRestartWith(p, move.Target),
                (p, move) => move.Type == ExecutorType.Activate
                    && (move.Card == KiSikil && move.Target == LilLa
                        || move.Card == LilLa && (move.Target == KiSikil || move.Target == Frost))),
            new BoardTactic(p => p.Has(LilLa) && p.Has(KiSikil)
                    && (p.Deck.Contains(NewYear) || p.Deck.Contains(Frost) && CanJumpNewYear(p))
                    && p.Fresh(PlanEffect.Scout) && p.Fresh(PlanEffect.NewYearJump)
                    && p.Fresh(PlanEffect.NewYearSearch),
                SummonTactic(Scout, LilLa),
                (p, move) => move.Type == ExecutorType.Activate && move.Card == Scout && move.Effect == 0
                    && (move.Target == NewYear || move.Target == Frost && CanJumpNewYear(p)),
                ActivateTactic(NewYear, 0, Scout), ActivateTactic(NewYear, 1, Password)),
            new BoardTactic(p => p.Hand.Contains(Password) && p.Deck.Contains(Trouble)
                    && p.Fresh(PlanEffect.Password) && p.Fresh(PlanEffect.Trouble) && !p.SearchLocked,
                ActivateTactic(Password, 0, Trouble),
                (p, move) => move.Type == ExecutorType.Activate && move.Card == Trouble
                    && (p.Hand.Contains(NightPhoto)
                        ? move.Target == Frost || p.ReturningAbao == 0 && move.Target == Treat
                        : move.Target == NightPhoto)),
            new BoardTactic(p => p.Has(NewYear) && p.Has(EvilLilLa) && p.Has(Engraver)
                    && p.Hand.Contains(Treat) && p.Hand.Contains(NightPhoto)
                    && p.Fresh(PlanEffect.RedRevive) && p.Fresh(PlanEffect.Sunny)
                    && p.Fresh(PlanEffect.RedDraw) && p.Fresh(PlanEffect.Nightfall),
                SummonTactic(EvilKiSikil, NewYear, EvilLilLa), ActivateTactic(EvilKiSikil, 1, EvilLilLa),
                SummonTactic(Sunny, EvilKiSikil, EvilLilLa), SplitTacticalPair,
                ActivateTactic(EvilKiSikil, 0), SummonTactic(Nightfall, EvilKiSikil, EvilLilLa),
                ActivateTactic(Nightfall, 0)),
            new BoardTactic(p => p.Has(Nightfall) && p.Has(Engraver) && p.Hand.Contains(NightPhoto)
                    && p.Hand.Contains(Treat) && p.Grave.Contains(Scout) && p.Grave.Contains(EvilKiSikil)
                    && p.CanRevive(EvilKiSikil) && p.Grave.Contains(Sunny) && p.Grave.Contains(Requiem)
                    && p.Fresh(PlanEffect.Photo) && p.Fresh(PlanEffect.PhotoRevive)
                    && PhotoRevivalCost(p) != 0 && p.Fresh(PlanEffect.Fusion),
                ActivateTactic(NightPhoto, 0, Scout), ActivateTactic(NightPhoto, 1, EvilKiSikil),
                SummonTactic(Treat), SummonTactic(Scout, Treat), SummonTactic(Sequence, Scout, Engraver),
                (p, move) => move.Type == ExecutorType.Activate && move.Card == Sequence && move.Fusion == Desirae,
                SummonTactic(Gryphon, Sequence, EvilKiSikil), ActivateTactic(Sequence, 1, Desirae)),
            new BoardTactic(p => p.Has(KiSikil) && p.Has(NewYear) && p.Hand.Contains(NightPhoto)
                    && p.Trouble && p.Grave.Contains(Yama) && p.Grave.Contains(EvilLilLa)
                    && p.CanRevive(EvilLilLa) && p.Fresh(PlanEffect.RedRevive) && p.Fresh(PlanEffect.BluePop)
                    && p.Fresh(PlanEffect.Yama) && p.Fresh(PlanEffect.Engraver) && p.Grave.Contains(Engraver),
                SummonTactic(EvilKiSikil, KiSikil, NewYear), ActivateTactic(EvilKiSikil, 1, EvilLilLa),
                ActivateTactic(EvilLilLa, 0, Trouble), ActivateTactic(Yama, 1, Scout),
                ActivateTactic(Engraver, 2, Deal), SummonTactic(Sequence, Scout, Engraver)),
            new BoardTactic(CanBuildTwinPairFromTrouble,
                ActivateTactic(Trouble, 0, NightPhoto), SummonTactic(EvilKiSikil, KiSikil, LilLa),
                ActivateTactic(EvilKiSikil, 1, EvilLilLa), ActivateTactic(EvilLilLa, 0, Trouble),
                ActivateTactic(Yama, 1, Scout), ActivateTactic(Engraver, 2, Deal),
                SummonTactic(Sequence, Scout, Engraver)),
            new BoardTactic(p => CanBuildTwinPairFromTrouble(p) && p.Hand.Contains(Frost)
                    && p.Fresh(PlanEffect.FrostSummon),
                ActivateTactic(Trouble, 0, NightPhoto), SummonTactic(Frost),
                SummonTactic(EvilKiSikil, Frost, LilLa), ActivateTactic(EvilKiSikil, 1, EvilLilLa),
                ActivateTactic(EvilLilLa, 0, Trouble), ActivateTactic(Yama, 1, Scout),
                ActivateTactic(Engraver, 2, Deal), SummonTactic(Sequence, Scout, Engraver)),
            new BoardTactic(p => p.Has(KiSikil) && p.Has(LilLa) && p.Hand.Contains(NightPhoto)
                    && p.Hand.Contains(Frost) && p.Trouble && p.Fresh(PlanEffect.FrostSummon)
                    && p.Grave.Contains(Yama) && p.Grave.Contains(Engraver) && p.Grave.Contains(Deal)
                    && p.Grave.Contains(Scout) && p.Grave.Contains(EvilLilLa) && p.CanRevive(Scout)
                    && p.CanRevive(EvilLilLa) && p.Fresh(PlanEffect.RedRevive) && p.Fresh(PlanEffect.BluePop)
                    && p.Fresh(PlanEffect.Yama) && p.Fresh(PlanEffect.Engraver),
                SummonTactic(Frost), SummonTactic(EvilKiSikil, Frost, LilLa),
                ActivateTactic(EvilKiSikil, 1, EvilLilLa), ActivateTactic(EvilLilLa, 0, Trouble),
                ActivateTactic(Yama, 1, Scout), ActivateTactic(Engraver, 2, Deal),
                SummonTactic(Sequence, Scout, Engraver)),
            new BoardTactic(p => p.Has(LilLa) && p.Has(Frost) && p.Trouble
                    && p.Grave.Contains(Yama) && p.Grave.Contains(Engraver) && p.Grave.Contains(EvilLilLa)
                    && p.CanRevive(EvilLilLa) && p.Fresh(PlanEffect.RedRevive)
                    && p.Fresh(PlanEffect.BluePop) && p.Fresh(PlanEffect.Yama),
                SummonTactic(EvilKiSikil, Frost, LilLa), ActivateTactic(EvilKiSikil, 1, EvilLilLa),
                ActivateTactic(EvilLilLa, 0, Trouble), ActivateTactic(Yama, 1, Engraver)),
            new BoardTactic(p => p.Has(LilLa) && p.Has(Frost) && p.Trouble && p.Hand.Contains(Treat)
                    && p.Grave.Contains(Yama) && p.Grave.Contains(Engraver)
                    && p.Grave.Contains(EvilLilLa) && p.CanRevive(EvilLilLa)
                    && p.Extra.Contains(Scout) && p.Deck.Contains(NewYear) && p.Fresh(PlanEffect.Scout)
                    && p.Fresh(PlanEffect.NewYearJump) && p.Fresh(PlanEffect.Engraver)
                    && p.Fresh(PlanEffect.RedRevive) && p.Fresh(PlanEffect.BluePop) && p.Fresh(PlanEffect.Yama),
                SummonWithExactMaterials(Scout, LilLa), ActivateTactic(Scout, 0, NewYear),
                ActivateTactic(NewYear, 0, Scout), SummonWithExactMaterials(EvilKiSikil, NewYear, Frost),
                ActivateTactic(EvilKiSikil, 1, EvilLilLa)),
            new BoardTactic(p => p.Has(LilLa) && p.Has(Frost) && p.Trouble
                    && !p.Hand.Contains(NightPhoto) && p.Grave.Contains(Engraver) && p.Grave.Contains(Deal)
                    && p.Grave.Contains(Yama) && p.Grave.Contains(EvilLilLa) && p.CanRevive(EvilLilLa)
                    && p.Extra.Contains(Scout) && p.Deck.Contains(NewYear) && p.Fresh(PlanEffect.Scout)
                    && p.Fresh(PlanEffect.NewYearJump) && p.Fresh(PlanEffect.Engraver)
                    && p.Fresh(PlanEffect.RedRevive) && p.Fresh(PlanEffect.BluePop) && p.Fresh(PlanEffect.Yama),
                ActivateTactic(Engraver, 2, Deal), SummonTactic(Scout, LilLa), ActivateTactic(Scout, 0, NewYear),
                ActivateTactic(NewYear, 0, Scout), SummonTactic(EvilKiSikil, NewYear, Frost),
                ActivateTactic(EvilKiSikil, 1, EvilLilLa), ActivateTactic(EvilLilLa, 0, Trouble),
                ActivateTactic(Yama, 1, Scout)),
            new BoardTactic(p => p.Has(EvilKiSikil) && p.Has(EvilLilLa) && p.Has(Scout) && p.Has(Engraver)
                    && !p.Hand.Contains(NightPhoto) && p.Grave.Contains(Requiem)
                    && p.Fresh(PlanEffect.Sunny) && p.Fresh(PlanEffect.RedDraw) && p.Fresh(PlanEffect.Fusion)
                    && p.Extra.Contains(Desirae) && p.Extra.Contains(Heist) && p.ReturningAbao != 0,
                SummonWithExactMaterials(Sunny, EvilKiSikil, EvilLilLa), SplitTacticalPair, ActivateTactic(EvilKiSikil, 0),
                SummonTactic(Sequence, Scout, Engraver),
                (p, move) => move.Type == ExecutorType.Activate && move.Card == Sequence && move.Fusion == Desirae
                    && (move.Places & BoardMain) != 0),
            new BoardTactic(p => p.Has(Desirae) && p.Has(Sequence) && p.Has(EvilKiSikil) && p.Has(EvilLilLa)
                    && !p.Hand.Contains(NightPhoto) && p.Fresh(PlanEffect.HeistRevive)
                    && p.Fresh(PlanEffect.Nightfall) && p.Fresh(PlanEffect.SequenceEquip)
                    && p.Extra.Contains(Heist) && p.Extra.Contains(Gryphon) && p.ReturningAbao != 0,
                SummonWithExactMaterials(Gryphon, Sequence, EvilKiSikil), SummonTactic(Heist, Gryphon, EvilLilLa),
                ActivateTactic(Heist, 1, EvilKiSikil), SummonTactic(Nightfall, Heist, EvilKiSikil),
                ActivateTactic(Nightfall, 0), ActivateTactic(Sequence, 1, Desirae)),
            new BoardTactic(p => p.Has(LilLa) && (p.Has(Frost) || p.Has(KiSikil)) && p.Trouble && p.Hand.Contains(NightPhoto)
                    && p.PhotoAtNibiruWindow
                    && p.Grave.Contains(Yama) && p.Grave.Contains(Engraver) && p.Grave.Contains(Scout)
                    && p.Grave.Contains(Deal) && p.Grave.Contains(EvilLilLa)
                    && p.CanRevive(Scout) && p.CanRevive(EvilLilLa) && p.Fresh(PlanEffect.Engraver)
                    && p.Fresh(PlanEffect.RedRevive) && p.Fresh(PlanEffect.BluePop) && p.Fresh(PlanEffect.Yama),
                SummonTactic(EvilKiSikil, LilLa), ActivateTactic(EvilKiSikil, 1, EvilLilLa),
                ActivateTactic(EvilLilLa, 0, Trouble), ActivateTactic(Yama, 1, Scout),
                ActivateTactic(Engraver, 2, Deal), SummonTactic(Sequence, Scout, Engraver)),
            new BoardTactic(p => p.Has(EvilKiSikil) && p.Has(EvilLilLa) && p.Has(Engraver)
                    && p.Fresh(PlanEffect.Sunny) && p.Fresh(PlanEffect.RedDraw)
                    && p.Fresh(PlanEffect.Fusion) && p.Hand.Contains(NightPhoto) && p.Extra.Contains(Caesar),
                SummonTactic(Sunny, EvilKiSikil, EvilLilLa),
                SplitTacticalPair,
                ActivateTactic(EvilKiSikil, 0)),
            new BoardTactic(p => p.Has(EvilKiSikil) && p.Has(EvilLilLa) && p.Has(Engraver)
                    && p.Grave.Contains(Sunny) && p.Hand.Contains(NightPhoto) && p.Fresh(PlanEffect.Photo)
                    && p.Fresh(PlanEffect.Fusion) && p.Extra.Contains(Caesar),
                SummonTactic(Gryphon, EvilKiSikil, EvilLilLa), ActivateTactic(NightPhoto, 0),
                SummonTactic(Sequence, Gryphon, NightPhoto),
                (p, move) => move.Type == ExecutorType.Activate && move.Card == Sequence
                    && move.Fusion == LacrimaFusion && !move.Selection.Contains(Gryphon),
                ActivateTactic(LacrimaFusion, 0, EvilKiSikil),
                SummonTactic(Nightfall, Sequence, EvilKiSikil), ActivateTactic(Nightfall, 0),
                SummonTactic(Caesar, Engraver, LacrimaFusion)),
            new BoardTactic(p => p.Has(EvilKiSikil) && p.Has(EvilLilLa) && p.Has(Engraver)
                    && p.Grave.Contains(Sunny) && p.Grave.Contains(Deal) && p.Hand.Contains(NightPhoto)
                    && p.Fresh(PlanEffect.Photo) && p.Fresh(PlanEffect.PhotoRevive)
                    && PhotoRevivalCost(p) != 0 && p.Fresh(PlanEffect.Fusion) && p.Extra.Contains(Desirae),
                SummonTactic(Gryphon, EvilKiSikil, EvilLilLa),
                SummonTactic(Sequence, Gryphon, Engraver),
                (p, move) => move.Type == ExecutorType.Activate && move.Card == Sequence
                    && move.Fusion == Desirae && !move.Selection.Contains(Gryphon)),
            new BoardTactic(p => p.Has(Sequence) && p.Has(Desirae) && p.Hand.Contains(NightPhoto)
                    && p.Grave.Contains(EvilKiSikil) && p.CanRevive(EvilKiSikil)
                    && p.Fresh(PlanEffect.Photo) && p.Fresh(PlanEffect.PhotoRevive)
                    && PhotoRevivalCost(p) != 0 && p.Fresh(PlanEffect.Nightfall),
                (p, move) => move.Type == ExecutorType.Activate && move.Card == NightPhoto && move.Effect == 0
                    && move.Target != EvilKiSikil && move.Target != EvilLilLa,
                ActivateTactic(NightPhoto, 1, EvilKiSikil), SummonTactic(Nightfall, Sequence, EvilKiSikil),
                ActivateTactic(Nightfall, 0), ActivateTactic(Sequence, 1, Desirae)),
            new BoardTactic(p => p.Has(Sequence) && p.Has(EvilKiSikil) && p.Has(EvilLilLa)
                    && p.Grave.Contains(Engraver) && p.Grave.Contains(Requiem)
                    && p.Fresh(PlanEffect.Sunny) && p.Fresh(PlanEffect.Fusion) && p.Fresh(PlanEffect.RedDraw),
                SummonTactic(Sunny, EvilKiSikil, EvilLilLa),
                SplitTacticalPair,
                ActivateTactic(EvilKiSikil, 0),
                (p, move) => move.Type == ExecutorType.Activate && move.Card == Sequence
                    && move.Fusion == Desirae && (move.Places & (3 << 5)) != 0),
            new BoardTactic(CanFinishDesiraeWithTwins,
                (p, move) => move.Type == ExecutorType.Activate && move.Card == Sequence
                    && move.Fusion == Desirae && (move.Places & (3 << 5)) != 0),
            new BoardTactic(p => p.Has(Sequence) && p.Has(Desirae) && p.Has(EvilLilLa) && p.Has(EvilKiSikil)
                    && p.Hand.Contains(NightPhoto) && p.Fresh(PlanEffect.Photo) && p.Fresh(PlanEffect.PhotoRevive)
                    && PhotoRevivalCost(p) != 0 && p.Fresh(PlanEffect.Nightfall)
                    && p.Fresh(PlanEffect.SequenceEquip) && p.Extra.Contains(Gryphon),
                SummonTactic(Gryphon, Sequence, EvilKiSikil), ActivateTactic(NightPhoto, 0),
                ActivateTactic(NightPhoto, 1, EvilKiSikil),
                SummonTactic(Nightfall, EvilKiSikil, EvilLilLa), ActivateTactic(Nightfall, 0),
                ActivateTactic(Sequence, 1, Desirae)),
        };

        private static bool CanTurnSweetIntoAbao(PlanPosition p) =>
            p.Has(NewYear) && p.Has(Sweet) && p.Has(Engraver)
            && !p.Field.Any(id => id != NewYear && IsRedTwin(id))
            && p.Grave.Contains(Deal) && p.CanRevive(Deal)
            && p.Extra.Contains(EvilLilLa) && p.Extra.Contains(Yama) && p.Extra.Contains(Abao)
            && p.Fresh(PlanEffect.BlueRevive);

        private static bool CanBuildTwinPairFromTrouble(PlanPosition p) =>
            p.Has(KiSikil) && p.Has(LilLa) && p.Hand.Contains(Trouble)
            && p.Deck.Contains(NightPhoto) && p.Fresh(PlanEffect.Trouble) && !p.SearchLocked
            && p.Grave.Contains(Yama) && p.Grave.Contains(Engraver) && p.Grave.Contains(Deal)
            && p.Grave.Contains(Scout) && p.Grave.Contains(EvilLilLa) && p.CanRevive(Scout)
            && p.CanRevive(EvilLilLa) && p.Fresh(PlanEffect.RedRevive) && p.Fresh(PlanEffect.BluePop)
            && p.Fresh(PlanEffect.Yama) && p.Fresh(PlanEffect.Engraver);

        private static bool CanFinishDesiraeWithTwins(PlanPosition p) =>
            p.Has(Sequence) && p.Has(EvilKiSikil) && p.Has(EvilLilLa) && p.Hand.Contains(NightPhoto)
            && p.Grave.Contains(Engraver) && p.Grave.Contains(Sunny) && p.Grave.Contains(Requiem)
            && p.Fresh(PlanEffect.Fusion) && p.Fresh(PlanEffect.Nightfall)
            && p.Extra.Contains(Desirae) && p.Extra.Contains(Gryphon);

        private static bool SplitTacticalPair(PlanPosition position, PlanMove move) =>
            move.Type == ExecutorType.Activate && move.Card == Sunny && move.Selection != null
                && move.Selection.Contains(EvilKiSikil) && move.Selection.Contains(EvilLilLa);

        private List<PlanPosition> ApplyTactic(PlanPosition start, BoardTactic tactic, Func<PlanMove, bool> firstMove,
            Stopwatch timer, int deadline)
        {
            var states = new List<PlanPosition> { start };
            foreach (var step in tactic.Steps)
            {
                if (timer.ElapsedMilliseconds >= deadline)
                    return new List<PlanPosition>();
                states = states.SelectMany(position => ComboSuccessors(position, BoardSearchPolicy.Tactical)
                    .Where(next => step(position, next.Move)
                        && (firstMove == null || position != start || firstMove(next.Move))))
                    .GroupBy(next => next.SearchKey()).Select(group => group.First())
                    .OrderByDescending(next => EndBoardScore(next, false))
                    .ThenByDescending(HasCenteredDesiraeForGryphon).Take(8).ToList();
                if (states.Count == 0)
                    break;
            }
            return states;
        }

        private IEnumerable<PlanPosition> TacticalSuccessors(PlanPosition start, PlanPosition position,
            Func<PlanMove, bool> firstMove, Stopwatch timer, int deadline)
        {
            foreach (var tactic in BoardTactics)
                if (tactic.Ready(position))
                    foreach (var end in ApplyTactic(position, tactic, position == start ? firstMove : null, timer, deadline))
                        yield return end;

            var actions = ComboSuccessors(position)
                .Where(next => position != start || firstMove(next.Move))
                .Select(next => new { Position = next, Priority = TacticalPriority(position, next) })
                .Where(next => next.Priority > 0)
                .OrderByDescending(next => next.Priority)
                .GroupBy(next => (next.Position.Move.Type, next.Position.Move.Card, next.Position.Move.Effect))
                .Take(4);
            foreach (var action in actions)
            foreach (var candidate in action.Take(3))
                yield return candidate.Position;
        }

        private int TacticalPriority(PlanPosition before, PlanPosition after)
        {
            var move = after.Move;
            if (move.Type == ExecutorType.SpellSet)
                return 0;
            if (move.Type == ExecutorType.Summon)
                return move.Card == KiSikil || move.Card == LilLa ? 10 : IsTwin(move.Card) ? 6 : 0;

            if (move.Type == ExecutorType.Activate)
            {
                // Finish an open effect using the live card, its remaining use, and the
                // resources it would actually move. NextPositions supplies legal targets.
                if (move.Trigger || move.Card == NewYear || move.Card == Deal || move.Card == Scout
                    || move.Card == Sweet || move.Card == Requiem)
                    return 9;
                if (move.Card == Abao)
                    return move.Target == KiSikil || move.Target == LilLa || move.Target == Nightfall ? 9 : 3;
                if (move.Card == Sunny || move.Card == NightPhoto || move.Card == Sequence)
                    return 8;
                if (move.Card == Engraver)
                    return before.Has(EvilLilLa) || before.Has(Nightfall) ? 7 : 5;
                if (move.Card == EvilKiSikil || move.Card == EvilLilLa || move.Card == Heist)
                    return before.Has(EvilLilLa) && move.Target == Deal ? 10 : 8;
                if (IsTwinSearch(move))
                    return move.Target == Trouble || IsTwin(move.Target) ? 5 : 0;
                return 0;
            }

            if (move.Type != ExecutorType.SpSummon)
                return 0;
            if (move.Card == Scout && before.Fresh(PlanEffect.Scout))
                return before.Fresh(PlanEffect.NewYearJump) ? 10 : 5;
            if (move.Card == Deal && before.Fresh(PlanEffect.Deal))
                return 10;
            if (move.Card == Requiem)
                return before.Fresh(PlanEffect.Requiem) && move.MaterialIds.Contains(Deal) ? 9 : 3;
            if (move.Card == EvilLilLa && before.Fresh(PlanEffect.BlueRevive)
                || move.Card == EvilKiSikil && before.Fresh(PlanEffect.RedRevive))
                return 9;
            if (move.Card == Yama)
                return before.Extra.Contains(Abao) && before.Has(EvilLilLa) ? 10 : 6;
            if (move.Card == Abao)
                return before.Hand.Count > 0 && before.Fresh(PlanEffect.Abao) ? 9 : 4;
            if (move.Card == Sunny)
                return before.Fresh(PlanEffect.Sunny) && before.Fresh(PlanEffect.RedDraw) ? 10 : 4;
            if (move.Card == Sequence && before.Fresh(PlanEffect.Fusion))
                return before.Grave.Count(LightFiend) > 0 ? 9 : 5;
            if (move.Card == Nightfall && before.Fresh(PlanEffect.Nightfall))
                return 9;
            if (move.Card == Gryphon || move.Card == Caesar)
                return 8;
            if (move.Card == Heist || move.Card == Frost || move.Card == Treat)
                return 5;
            return 0;
        }

        private PlanResult TryTacticalContinuation(PlanPosition start, Stopwatch timer, int deadline,
            Func<PlanMove, bool> firstMove = null)
        {
            if (start.DrawEffects.Active || start.Sealing || timer.ElapsedMilliseconds >= deadline)
                return null;
            firstMove = firstMove ?? PlanOffered;
            var frontier = new List<PlanPosition> { start };
            var visited = new HashSet<string> { start.SearchKey() };
            PlanPosition best = start;
            start.Score = EndBoardScore(start, true);
            int evaluations = 0;
            for (int round = 0; round < 36 && frontier.Count != 0 && timer.ElapsedMilliseconds < deadline; round++)
            {
                var nextFrontier = new List<PlanPosition>();
                foreach (var position in frontier)
                {
                    if (timer.ElapsedMilliseconds >= deadline)
                        break;
                    foreach (var next in TacticalSuccessors(start, position, firstMove, timer, deadline))
                    {
                        if (!visited.Add(next.SearchKey()))
                            continue;
                        next.Score = EndBoardScore(next, true);
                        next.Potential = ContinuationRank(next) + (next.ActionCount - position.ActionCount) * 12;
                        evaluations++;
                        nextFrontier.Add(next);
                        if (next.Score > best.Score || next.Score == best.Score
                            && (PreferEnd(next, best) || !PreferEnd(best, next)
                                && HasCenteredDesiraeForGryphon(next) && !HasCenteredDesiraeForGryphon(best)))
                            best = next;
                    }
                }
                frontier = nextFrontier.OrderByDescending(next => next.Potential)
                    .GroupBy(next => next.SearchKey(ignorePlacement: true))
                    .SelectMany(group => group.Take(2)).Take(12).ToList();
            }
            if (best == start)
                return null;
            var moves = new List<PlanMove>();
            for (var step = best; step.Previous != null; step = step.Previous)
                if (step.Move != null)
                    moves.Add(step.Move);
            moves.Reverse();
            return new PlanResult { Moves = moves, Terminal = best, Score = best.Score, ReturnZone = best.AbaoZone,
                States = visited.Count - 1, ScoreEvaluations = evaluations };
        }

        private static bool HasCenteredDesiraeForGryphon(PlanPosition p) =>
            p.Field[2] == Desirae && p.Grave.Contains(Gryphon) && (p.Has(Abao) || p.ReturningAbao != 0);

        [DataContract]
        private sealed class TablePlan
        {
            [DataMember] public int Version;
            [DataMember] public string Key;
            [DataMember] public int Score;
            [DataMember] public int ReturnZone;
            [DataMember(EmitDefaultValue = false)] public bool EarlyRequiem;
            [DataMember(EmitDefaultValue = false)] public bool PhotoDefense;
            [DataMember] public List<PlanMove> Moves;
        }

        private static bool IsTableInert(int id)
        {
            // These are only interchangeable as spare discards in this own-turn model.
            // Keep cards with modelled effects or set actions as individual names.
            return id > 0 && IsHandTrap(id) && !PlanMainCards.Contains(id) && TrapBit(id) == 0;
        }

        private static string TableKey(PlanPosition position, bool recovering)
        {
            var snapshot = position.Next(null);
            snapshot.Hand = position.Hand.Select(id => IsTableInert(id) ? TableInertDiscard : id).ToList();
            using (var hash = SHA256.Create())
                return Convert.ToBase64String(hash.ComputeHash(Encoding.UTF8.GetBytes(
                    recovering ? "ash:" + snapshot.Key() : snapshot.Key())));
        }

        private static Dictionary<string, TablePlan> ReadOpeningTable(string path)
        {
            var result = new Dictionary<string, TablePlan>(StringComparer.Ordinal);
            if (!File.Exists(path))
                return result;

            string[] lines;
            try
            {
                lines = File.ReadAllLines(path);
            }
            catch (IOException)
            {
                return result;
            }
            catch (UnauthorizedAccessException)
            {
                return result;
            }

            var serializer = new DataContractJsonSerializer(typeof(TablePlan));
            foreach (string line in lines)
            {
                if (string.IsNullOrWhiteSpace(line))
                    continue;
                try
                {
                    using (var row = new MemoryStream(Encoding.UTF8.GetBytes(line)))
                    {
                        var entry = (TablePlan)serializer.ReadObject(row);
                        if (entry.Version != TableVersion || string.IsNullOrEmpty(entry.Key)
                            || entry.Score < 0 || entry.ReturnZone < -1 || entry.ReturnZone >= 5
                            || entry.Moves == null || entry.Moves.Count == 0 || entry.Moves.Count > 52
                            || entry.Moves.Any(move => !ValidTableMove(move)))
                            continue;
                        if (!result.TryGetValue(entry.Key, out TablePlan old) || entry.Score > old.Score)
                            result[entry.Key] = entry;
                    }
                }
                catch (SerializationException)
                {
                    // A malformed row is a miss. The ordinary policy is still available.
                }
            }
            return result;
        }

        private static bool ValidTableMove(PlanMove move)
        {
            if (move == null || move.Card <= 0 || (move.Places & ~127) != 0)
                return false;
            if (move.Type != ExecutorType.Activate && move.Type != ExecutorType.Summon
                && move.Type != ExecutorType.SpSummon && move.Type != ExecutorType.SpellSet)
                return false;
            return (move.Materials == null || move.MaterialIds != null && move.Materials.Length == move.MaterialIds.Length
                    && move.Materials.All(zone => zone >= 0 && zone < 7))
                && (move.SummonedIds == null || move.SummonedZones != null && move.SummonedIds.Length == move.SummonedZones.Length
                    && move.SummonedZones.All(zone => zone >= 0 && zone < 7));
        }

        private bool TryTablePlan(PlanPosition position, bool recovering, Stopwatch timer, out PlanResult result)
        {
            result = null;
            var table = OpeningTable.Value;
            if (table.Count == 0 || !table.TryGetValue(TableKey(position, recovering), out TablePlan entry)
                || !PlanOffered(entry.Moves[0]))
                return false;
            // Recompute slot-sensitive Abao scores and any draw whose Sweet risk can change along the line.
            bool canDrawSweet = entry.Moves.Any(move => move.Type == ExecutorType.Activate
                    && move.Card == EvilKiSikil && move.Effect == 0)
                && (position.Deck.Contains(Sweet) || entry.Moves.Any(move => move.Type == ExecutorType.Activate
                    && move.Card == NightPhoto && move.Effect == 0 && move.Target == Sweet));
            if (position.Has(Abao) || position.ReturningAbao != 0
                || entry.Moves.Any(move => move.Card == Abao || move.Target == Abao || move.SummonedIds?.Contains(Abao) == true)
                || canDrawSweet)
            {
                int deadline = (int)Math.Min(timer.ElapsedMilliseconds + 100, 500);
                if (timer.ElapsedMilliseconds >= deadline)
                    return false;
                var firstMoves = ComboSuccessors(position).Where(next => MatchesCachedMove(next.Move, entry.Moves[0])).ToList();
                var replay = ReplayCachedOpening(firstMoves, entry.Moves, timer, deadline, requirePlacement: true);
                foreach (var candidate in replay)
                    candidate.Score = EndBoardScore(candidate, true);
                var terminal = replay.OrderByDescending(candidate => candidate.Score).FirstOrDefault();
                if (terminal == null)
                    return false;
                result = new PlanResult { Moves = entry.Moves, Score = terminal.Score,
                    ReturnZone = terminal.AbaoZone };
                return true;
            }
            int score = entry.Score - (entry.EarlyRequiem || entry.PhotoDefense ? NibiruDefensePoints : 0)
                + (TableHasNibiruDefense(position, entry.Moves) ? NibiruDefensePoints : 0);
            result = new PlanResult { Moves = entry.Moves, Score = score, ReturnZone = entry.ReturnZone };
            return true;
        }

        private static bool TableHasNibiruDefense(PlanPosition position, IEnumerable<PlanMove> moves)
        {
            if (position.OwnSummons >= NibiruThreshold)
                return HasNibiruDefense(position);
            var projected = position.Next(null);
            foreach (PlanMove move in moves)
            {
                if (move.Discard > 0)
                {
                    if (projected.Hand.Remove(move.Discard))
                        projected.Grave.Add(move.Discard);
                }
                if (move.MaterialIds != null)
                {
                    for (int i = 0; i < move.MaterialIds.Length; i++)
                    {
                        if (move.Materials != null)
                            projected.Field[move.Materials[i]] = 0;
                        if (move.MaterialIds[i] == NewYear && projected.NewYearZone >= 0
                            && move.Materials?[i] == projected.NewYearZone)
                            projected.NewYearZone = -1;
                        else
                            projected.Grave.Add(move.MaterialIds[i]);
                    }
                }
                bool summonFromExtra = move.Type == ExecutorType.SpSummon && projected.Extra.Remove(move.Card);
                if (summonFromExtra)
                    projected.Unrevivable &= ~ExtraFlag(move.Card);
                if (move.Type == ExecutorType.SpellSet)
                    projected.SpellSlots++;
                if (move.Type == ExecutorType.Activate)
                {
                    if (move.Card == NightPhoto && move.Effect == 0)
                    {
                        projected.Use(PlanEffect.Photo);
                        projected.Hand.Remove(NightPhoto);
                        projected.Grave.Remove(move.Target);
                    }
                    else if (move.Card == NightPhoto && move.Effect == 1)
                    {
                        projected.Use(PlanEffect.PhotoRevive);
                        projected.Grave.Remove(move.Cost);
                    }
                    else if (move.Card == NewYear && move.Effect == 0)
                    {
                        projected.Use(PlanEffect.NewYearJump);
                        int tribute = Array.IndexOf(projected.Field, move.Target);
                        if (tribute >= 0)
                            projected.Field[tribute] = 0;
                        if (projected.Grave.Contains(NewYear) && move.SummonedZones?.Length == 1)
                            projected.NewYearZone = move.SummonedZones[0];
                        else
                            projected.Hand.Remove(NewYear);
                        projected.Grave.Add(move.Target);
                    }
                    else if ((move.Card == Scout || move.Card == Deal) && move.Effect == 0)
                    {
                        if (projected.Extra.Remove(move.Target))
                            projected.Unrevivable |= ExtraFlag(move.Target);
                        else
                            projected.Deck.Remove(move.Target);
                        projected.Grave.Add(move.Target);
                    }
                    else if (move.Card == Password || move.Card == Trouble || move.Card == NewYear && move.Effect == 1)
                    {
                        if (move.Card == NewYear)
                            projected.Use(PlanEffect.NewYearSearch);
                        else
                        {
                            projected.Hand.Remove(move.Card);
                            projected.Use(move.Card == Password ? PlanEffect.Password : PlanEffect.Trouble);
                            if (move.Card == Trouble)
                                projected.SpellSlots++;
                            else
                                projected.Grave.Add(Password);
                        }
                        if (!projected.Deck.Remove(move.Target))
                            projected.Grave.Remove(move.Target);
                        projected.Hand.Add(move.Target);
                    }
                    if (move.Effect == 0 && (move.Card == Requiem || move.Card == Sunny
                        || move.Card == Abao && move.Option == 3)
                        || move.Effect == 1 && (move.Card == NightPhoto || move.Card == Nightfall))
                    {
                        int released = Array.IndexOf(projected.Field, move.Card);
                        if (released >= 0)
                            projected.Field[released] = 0;
                    }
                }
                if (move.SummonedIds != null)
                    for (int i = 0; i < move.SummonedIds.Length; i++)
                    {
                        int id = move.SummonedIds[i];
                        projected.Grave.Remove(id);
                        if (move.SummonSource == CardLocation.Hand)
                            projected.Hand.Remove(id);
                        int zone = move.SummonedZones[i];
                        if (zone >= 0 && zone < projected.Field.Length)
                            projected.Field[zone] = id;
                    }
                else if (move.Type == ExecutorType.Summon || move.Type == ExecutorType.SpSummon)
                {
                    for (int i = 0; i < projected.Field.Length; i++)
                        if (move.Places == 1 << i)
                            projected.Field[i] = move.Card;
                    if (move.Type == ExecutorType.Summon)
                        projected.Hand.Remove(move.Card);
                }
                projected.OwnSummons += move.SummonedIds?.Length
                    ?? (move.Type == ExecutorType.Summon || move.Type == ExecutorType.SpSummon ? 1 : 0);
                if (projected.OwnSummons <= NibiruThreshold && summonFromExtra && move.Card == Requiem)
                    projected.EarlyRequiem = true;
                if (projected.OwnSummons >= NibiruThreshold)
                {
                    CaptureNibiruWindow(projected, move.SummonedIds?.Contains(NewYear) == true);
                    return HasNibiruDefense(projected);
                }
            }
            return HasNibiruDefense(projected);
        }

        private void ExportTablePlan(PlanPosition position, PlanResult plan, bool recovering)
        {
            // Do not generalize rows that explicitly place or target an inert hand trap.
            if (plan.Moves.Count == 0 || plan.Moves.Any(move => IsTableInert(move.Card) || IsTableInert(move.Target)
                || move.Selection != null && move.Selection.Any(IsTableInert)
                || move.MaterialIds != null && move.MaterialIds.Any(IsTableInert)))
                return;
            var encoded = new TablePlan
            {
                Version = TableVersion, Key = TableKey(position, recovering),
                Score = plan.Score + (plan.Terminal.RedDrawWithSweetInDeck ? SweetDrawPenalty : 0),
                ReturnZone = plan.ReturnZone,
                EarlyRequiem = HasRequiemDefense(plan.Terminal),
                PhotoDefense = HasPhotoDefense(plan.Terminal),
                Moves = plan.Moves.Select(move =>
                {
                    var copy = CopyMove(move, move.Places);
                    if (IsTableInert(copy.Discard))
                        copy.Discard = TableInertDiscard;
                    return copy;
                }).ToList(),
            };
            using (var stream = new MemoryStream())
            {
                var serializer = new DataContractJsonSerializer(typeof(TablePlan));
                serializer.WriteObject(stream, encoded);
                TracePlanner("table-entry", Encoding.UTF8.GetString(stream.ToArray()));
            }
        }
    }
}
