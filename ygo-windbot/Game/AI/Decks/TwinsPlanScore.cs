using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using YGOSharp.OCGWrapper.Enums;

namespace WindBot.Game.AI.Decks
{
    public partial class TwinsExecutor
    {
        private int EndBoardScore(PlanPosition p, bool exact)
        {
            if (p.ReturningAbao == 0)
                return ScoreBoard(p, p.Field, exact);

            int best = -1;
            for (int i = 0; i < 5; i++)
            {
                if (p.Field[i] != 0)
                    continue;
                var field = (int[])p.Field.Clone();
                field[i] = Abao;
                int score = ScoreBoard(p, field, exact);
                if (score > best)
                {
                    best = score;
                    p.AbaoZone = i;
                }
            }
            return best >= 0 ? best : ScoreBoard(p, p.Field, exact);
        }

        // Keep fractional interactions exact internally, including 1.05 × 0.7.
        private const int ScoreUnitsPerPoint = 1000;
        private const int ScoreUnitsPerTenth = ScoreUnitsPerPoint / 10;
        private const int DirectRemovalPoints = 1000;
        protected virtual bool UsesUnifiedBoardWeights => true;
        private int DelayedRemovalPoints => UsesUnifiedBoardWeights ? 800 : 1000;
        private int HeistNegatePoints => UsesUnifiedBoardWeights ? 735 : 1000;
        private int DrawPoints => UsesUnifiedBoardWeights ? 500 : 400;
        private int DisconnectNegatePoints => UsesUnifiedBoardWeights ? 1100 : 1000;
        private int NegateDestroyBonus => UsesUnifiedBoardWeights ? 300 : 0;

        private static string TraceScore(int score) =>
            (score / (decimal)ScoreUnitsPerTenth).ToString(System.Globalization.CultureInfo.InvariantCulture);

        private int ScoreBoard(PlanPosition p, int[] field, bool exact)
        {
            int score = NetDrawPoints(p) + (HasNibiruDefense(p) ? NibiruDefensePoints : 0);
            if (CanActivateOnField(field, Array.IndexOf(field, Caesar), p.NormalZones))
                score += 1300; // Caesar covers summon effects and can destroy the negated card.
            bool desirae = CanActivateOnField(field, Array.IndexOf(field, Desirae), p.NormalZones) && p.Equipment != 0;
            if (desirae)
                score += UsesUnifiedBoardWeights ? 1050 : 1000; // Desirae negates effects across all three card types.

            bool canPayAbao = p.Hand.Count != 0;
            bool selfTrade = desirae && (p.Equipment != 0 || p.Grave.Any(LightFiend));
            bool blueInGrave = p.Grave.Contains(EvilLilLa) && p.CanRevive(EvilLilLa);
            bool redInGrave = p.Grave.Contains(EvilKiSikil) && p.CanRevive(EvilKiSikil);
            bool heistInGrave = p.Grave.Contains(Heist) && p.CanRevive(Heist);
            bool evilOnField = field.Any(Evil);
            bool counterSet = (p.Set & SetDisconnect) != 0;
            if (evilOnField && p.Grave.Contains(Frost))
                score += DrawPoints / 2; // The opponent must search before Frost can draw.
            bool nightfall = CanActivateOnField(field, Array.IndexOf(field, Nightfall), p.NormalZones);
            bool photo = CanActivateOnField(field, Array.IndexOf(field, NightPhoto), p.NormalZones) && PhotoRevivalCost(p) != 0;
            bool challenge = (p.Set & SetChallenge) != 0;
            var opportunities = new List<BoardOpportunity> { new BoardOpportunity { Field = field, Red = field.Any(IsRedTwin) } };
            bool earlyRotation = false;
            if (nightfall)
            {
                var released = (int[])field.Clone();
                released[Array.IndexOf(field, Nightfall)] = 0;
                int[] costs = p.Grave.Where(id => Evil(id) || id == Disconnect || id == Challenge).Distinct().ToArray();
                if (NeedsEarlyNightfallRotation(field))
                {
                    int cost = costs.FirstOrDefault(id => id != EvilKiSikil && id != EvilLilLa);
                    var early = cost == 0 ? null : EarlyNightfallRotation(released, p.Extra.Contains,
                        redInGrave && blueInGrave, p.NormalZones, p.BlockedExtraZones);
                    if (early != null)
                    {
                        early.Spent = cost;
                        early.Sunny |= early.Field.Contains(Sunny);
                        earlyRotation = true;
                        opportunities.Clear();
                        opportunities.Add(early);
                    }
                }
                else
                {
                    int[] spare = costs.Where(id => id != EvilKiSikil && id != EvilLilLa && id != Heist).Take(1).ToArray();
                    if (spare.Length > 0)
                        costs = spare;
                    foreach (int card in new[] { Heist, EvilLilLa, EvilKiSikil, Sunny }.Where(p.Extra.Contains))
                    foreach (int cost in costs)
                    {
                        foreach (var option in OpponentSummons(released, card, true, p.NormalZones))
                        {
                            option.Spent = cost;
                            opportunities.Add(option);
                            if (!exact)
                                break;
                        }
                    }
                    if (Array.IndexOf(field, Nightfall) >= 5 && p.Extra.Contains(Deal) && GryphonPoints(field) == 0
                        && costs.Length != 0 && (field.Contains(Gryphon) || p.Grave.Contains(Gryphon)))
                    {
                        foreach (var option in OpponentSummons(released, Deal, true, p.NormalZones, p.BlockedExtraZones))
                        {
                            if (Array.IndexOf(option.Field, Deal) < 5 || (GryphonPoints(option.Field) == 0
                                && AbaoGryphonPoints(p, option.Field, costs[0]) == 0))
                                continue;
                            option.Spent = costs[0];
                            opportunities.Add(option);
                            if (!exact)
                                break;
                        }
                    }
                }
            }
            if (!earlyRotation && field.Contains(Sunny)
                && CanActivateOnField(field, Array.IndexOf(field, Sunny), p.NormalZones))
                opportunities.Add(new BoardOpportunity { Field = field, Sunny = true, Red = field.Any(IsRedTwin) });

            int interaction = 0;
            foreach (var option in opportunities)
            {
                bool blue = option.Blue;
                bool heist = option.Heist;
                bool challengeFollowup = false;
                bool draw = option.Draw;
                bool red = option.Red;
                int[] current = option.Field;
                bool trap = counterSet && current.Any(Evil);
                bool canUseBlue = blueInGrave && option.Spent != EvilLilLa;
                bool canUseHeist = heistInGrave && option.Spent != Heist;
                bool heistInExtra = p.Extra.Contains(Heist) && option.Summoned != Heist;
                if (option.Sunny && CanActivateOnField(current, Array.IndexOf(current, Sunny), p.NormalZones))
                {
                    var released = (int[])current.Clone();
                    released[Array.IndexOf(current, Sunny)] = 0;
                    if (canUseBlue && redInGrave && option.Spent != EvilKiSikil && CanRevivePair(released))
                    {
                        blue = true;
                        draw = true;
                        red = true;
                        challengeFollowup = challenge && heistInExtra
                            && p.Grave.Any(id => id != option.Spent && LinkRating(id) == 0
                                && (IsRedTwin(id) || IsBlueTwin(id)) && p.CanRevive(id))
                            && CanFollowBlueWithHeist(released, p.BlockedExtraZones);
                    }
                    else if (canUseBlue && released.Any(IsRedTwin) && CanReviveBlue(released))
                        blue = true;
                }

                bool blueByRevival = canUseBlue && red && !blue && CanReviveBlue(current);
                bool redRevives = CanActivateOnField(current, Array.IndexOf(current, EvilKiSikil), p.NormalZones)
                    && !current.Any(IsBlueTwin);
                blue |= blueByRevival && redRevives;
                if (redInGrave && option.Spent != EvilKiSikil && !current.Any(IsRedTwin)
                    && CanActivateOnField(current, Array.IndexOf(current, EvilLilLa), p.NormalZones)
                    && OpponentSummons(current, EvilKiSikil, false, p.NormalZones).Any(entry => entry.Draw))
                {
                    draw = true;
                    red = true;
                }
                if (photo && (blueByRevival || canUseHeist))
                {
                    var withoutPhoto = (int[])current.Clone();
                    withoutPhoto[Array.IndexOf(current, NightPhoto)] = 0;
                    if (blueByRevival && CanReviveBlue(withoutPhoto))
                        blue = true;
                    else if (canUseHeist && OpponentSummons(withoutPhoto, Heist, false, p.NormalZones).Any(entry => entry.Heist))
                        heist = true;
                }

                bool newYearTrades = selfTrade && (p.Hand.Contains(NewYear) || p.Grave.Contains(NewYear))
                    && (p.Hand.Contains(NewYear) || option.Spent != NewYear);
                bool challengeTrades = false;
                bool challengeHeist = false;
                if (challenge && selfTrade && (Occupied(current) & BoardMain) != BoardMain)
                {
                    var withoutDesirae = (int[])current.Clone();
                    withoutDesirae[Array.IndexOf(current, Desirae)] = 0;
                    if (FreeLinkZones(withoutDesirae) != 0)
                    {
                        foreach (int partner in p.Grave.Where(id => IsRedTwin(id) || IsBlueTwin(id))
                            .Where(id => id != option.Spent && p.CanRevive(id)).Distinct())
                        {
                            if (heistInExtra)
                            {
                                challengeTrades = true;
                                challengeHeist = OpponentSummons(withoutDesirae, Heist, true, p.NormalZones).Any(entry => entry.Heist);
                            }
                            else if (IsRedTwin(partner) && p.Extra.Contains(EvilKiSikil)
                                || IsBlueTwin(partner) && p.Extra.Contains(EvilLilLa))
                                challengeTrades = true;
                        }
                    }
                }

                int gryphon = GryphonPoints(current);
                int abaoZone = canPayAbao ? Array.IndexOf(current, Abao) : -1;
                bool abaoAvailable = CanActivateOnField(current, abaoZone, p.NormalZones);
                int abaoPop = abaoAvailable ? DirectRemovalPoints : 0;
                int abaoGryphon = abaoAvailable ? AbaoGryphonPoints(p, current, option.Spent) : 0;
                bool abaoChangesLock = abaoAvailable && current.Contains(Gryphon)
                    && (Arrows(Abao, abaoZone) & (BoardEnemyMain | (3 << 5))) != 0;
                for (int useChallenge = 0; useChallenge <= 2; useChallenge++)
                {
                    bool challengeBlue = useChallenge == 1;
                    bool challengeTrade = useChallenge == 2;
                    if (challengeBlue && (!challenge || !blueByRevival) || challengeTrade && !challengeTrades)
                        continue;
                    bool releasedBlue = blue || challengeBlue;
                    bool releasedHeist = heist || challengeTrade && challengeHeist
                        || useChallenge == 0 && challengeFollowup;
                    bool trapCanDestroy = trap && (releasedBlue || current.Any(IsBlueTwin));
                    bool tradedWithoutTrap = newYearTrades || challengeTrade;
                    bool traded = tradedWithoutTrap || trapCanDestroy && selfTrade;
                    int trapPoints = trap ? DisconnectNegatePoints + (red ? DrawPoints : 0) : 0;
                    if (trapCanDestroy && (!selfTrade || tradedWithoutTrap))
                        trapPoints += NegateDestroyBonus;
                    int total = (releasedBlue ? DelayedRemovalPoints : 0) + (releasedHeist ? HeistNegatePoints : 0)
                        + (draw ? DrawPoints : 0) + trapPoints + (traded ? DelayedRemovalPoints : 0);
                    // Each option spends the same activation, so choose one result.
                    int abaoResult = Math.Max(gryphon + abaoPop, abaoGryphon);
                    if (abaoAvailable && (abaoResult < gryphon + DirectRemovalPoints || abaoChangesLock))
                        abaoResult = Math.Max(abaoResult, AbaoTwinPoints(current, p.NormalZones, abaoZone,
                            canUseBlue && !releasedBlue, canUseHeist && !releasedHeist,
                            redInGrave && option.Spent != EvilKiSikil && !draw));
                    total += abaoResult;
                    interaction = Math.Max(interaction, total);
                }
            }
            if (challenge && blueInGrave && redInGrave && nightfall)
            {
                int cycledGryphon = NewYearGryphonPoints(p, field);
                if (cycledGryphon > 0)
                    interaction = Math.Max(interaction, DelayedRemovalPoints + DrawPoints + cycledGryphon
                        + (counterSet ? DisconnectNegatePoints + DrawPoints + NegateDestroyBonus : 0));
            }
            return score + interaction;
        }

        private const int SweetDrawPenalty = 200;

        private int NetDrawPoints(PlanPosition p) =>
            p.Draws * DrawPoints - p.OpponentDraws * ScoreUnitsPerPoint - (p.RedDrawWithSweetInDeck ? SweetDrawPenalty : 0);

        private int AbaoTwinPoints(int[] field, int normalZones, int abaoZone, bool blue, bool heist, bool red)
        {
            if (!blue && !heist && !red)
                return 0;
            var released = (int[])field.Clone();
            released[abaoZone] = 0;
            int best = 0;
            foreach (int twin in AbaoScoringTwins)
            {
                if (twin == EvilLilLa && (!blue || !released.Any(IsRedTwin))
                    || twin == EvilKiSikil && (!red || !released.Any(IsBlueTwin))
                    || twin == Heist && !heist)
                    continue;
                foreach (var option in OpponentSummons(released, twin, false, normalZones))
                {
                    int effect = option.Blue ? DelayedRemovalPoints : option.Heist ? HeistNegatePoints : option.Draw ? DrawPoints : 0;
                    if (effect == 0)
                        continue;
                    bool preservesEffects = Enumerable.Range(0, field.Length).All(zone =>
                        field[zone] != Nightfall && field[zone] != Sunny && field[zone] != Desirae
                            || !CanActivateOnField(field, zone, normalZones)
                            || CanActivateOnField(option.Field, zone, normalZones));
                    if (preservesEffects)
                        best = Math.Max(best, GryphonPoints(option.Field) + effect);
                }
            }
            return best;
        }

        private static readonly int[] AbaoScoringTwins = { EvilLilLa, Heist, EvilKiSikil };

        private int NewYearGryphonPoints(PlanPosition p, int[] field)
        {
            int nightfall = Array.IndexOf(field, Nightfall);
            if (nightfall < 5 || !p.Extra.Contains(Deal) || !field.Contains(Abao)
                || !p.Grave.Contains(Gryphon) || !p.CanRevive(Gryphon) || p.Hand.Count == 0
                || !(p.Hand.Contains(NewYear) || p.Grave.Contains(NewYear))
                || field.Any(id => IsRedTwin(id) || IsBlueTwin(id))
                || !p.Grave.Any(id => Evil(id) && id != EvilKiSikil && id != EvilLilLa
                    || id == Disconnect || id == Challenge))
                return 0;

            int points = 0;
            foreach (var blue in OpponentSummons(field, EvilLilLa, false, p.NormalZones))
            {
                int blueZone = Array.IndexOf(blue.Field, EvilLilLa);
                if (!CanActivateOnField(blue.Field, blueZone, p.NormalZones))
                    continue;
                foreach (var red in OpponentSummons(blue.Field, EvilKiSikil, false, p.NormalZones).Where(option => option.Draw))
                {
                    int newYear = NewYearTwinCyclePlace(red.Field, blueZone);
                    if (newYear == 0)
                        continue;
                    red.Field[blueZone] = 0;
                    int newYearZone = Enumerable.Range(0, 5).First(zone => newYear == 1 << zone);
                    red.Field[newYearZone] = NewYear;
                    foreach (var returned in OpponentSummons(red.Field, EvilLilLa, false, p.NormalZones).Where(option => option.Blue))
                    {
                        returned.Field[nightfall] = 0;
                        foreach (var deal in OpponentSummons(returned.Field, Deal, true, p.NormalZones, p.BlockedExtraZones))
                            if (Array.IndexOf(deal.Field, Deal) >= 5)
                                points = Math.Max(points, AbaoGryphonPoints(p, deal.Field, Nightfall));
                    }
                }
            }
            return points;
        }

        private int AbaoGryphonPoints(PlanPosition p, int[] field, int spent)
        {
            if (p.Hand.Count == 0 || spent == Gryphon || !p.Grave.Contains(Gryphon) || !p.CanRevive(Gryphon)
                || !CanActivateOnField(field, Array.IndexOf(field, Abao), p.NormalZones))
                return 0;
            GryphonRevivalZone(field, out int points);
            return points;
        }

        private int GryphonPoints(int[] field)
        {
            int arrows = PointedZones(field);
            if (!field.Contains(Gryphon) || (arrows & BoardEnemyMain) != 0)
                return 0;
            bool singleExtra = (field[5] == 0) != (field[6] == 0);
            return singleExtra && (arrows & (1 << (field[5] == 0 ? 5 : 6))) == 0 ? 2300 : 2000;
        }

        private int GryphonRevivalZone(int[] field) => GryphonRevivalZone(field, out _);

        private int GryphonRevivalZone(int[] field, out int points)
        {
            points = 0;
            int abao = Array.IndexOf(field, Abao);
            if (abao < 0 || field.Contains(Gryphon))
                return -1;
            var revived = (int[])field.Clone();
            revived[abao] = 0;
            if ((PointedZones(revived) & BoardEnemyMain) != 0)
                return -1;
            int bestZone = -1;
            for (int zone = 0; zone < 5; zone++)
            {
                if (revived[zone] != 0)
                    continue;
                revived[zone] = Gryphon;
                bool preservesEffects = Enumerable.Range(0, field.Length).All(i =>
                    (field[i] != Nightfall && field[i] != Desirae && field[i] != Caesar
                        && field[i] != EvilKiSikil && field[i] != EvilLilLa)
                    || CanActivateOnField(revived, i));
                int score = preservesEffects ? GryphonPoints(revived) : 0;
                if (score > points)
                {
                    points = score;
                    bestZone = zone;
                }
                revived[zone] = 0;
            }
            return bestZone;
        }

        private sealed class BoardOpportunity
        {
            public int[] Field;
            public int Spent, Summoned;
            public bool Red, Blue, Draw, Heist, Sunny;
        }

        private IEnumerable<BoardOpportunity> OpponentSummons(int[] field, int card, bool extra, int normalZones,
            int blockedExtraZones = 0)
        {
            int zones = extra ? FreeLinkZones(field) & ~blockedExtraZones : BoardMain & ~Occupied(field);
            for (int i = 0; i < field.Length; i++)
            {
                if ((zones & (1 << i)) == 0)
                    continue;
                var placed = (int[])field.Clone();
                placed[i] = card;
                bool works = CanActivateOnField(placed, i, normalZones);
                yield return new BoardOpportunity {
                    Field = placed, Summoned = card, Red = placed.Any(IsRedTwin),
                    Blue = card == EvilLilLa && works && placed.Any(IsRedTwin),
                    Draw = card == EvilKiSikil && works && placed.Any(IsBlueTwin),
                    Heist = card == Heist && works, Sunny = card == Sunny && works
                };
            }
        }

        private static bool NeedsEarlyNightfallRotation(int[] field) =>
            field.Contains(Gryphon) && (field[5] == Nightfall || field[6] == Nightfall);

        private static readonly int[] EarlyNightfallTargets = { Deal, Scout, EvilKiSikil, EvilLilLa, Heist, Sunny };

        private BoardOpportunity EarlyNightfallRotation(int[] released, Func<int, bool> inExtra, bool canUseSunny,
            int normalZones, int blockedExtraZones)
        {
            BoardOpportunity fallback = null;
            bool sunnyFirst = canUseSunny && CanRevivePair(released);
            var targets = sunnyFirst ? new[] { Sunny }.Concat(EarlyNightfallTargets.Where(id => id != Sunny))
                : EarlyNightfallTargets;
            foreach (int target in targets)
            {
                if (!inExtra(target))
                    continue;
                var place = OpponentSummons(released, target, true, normalZones, blockedExtraZones)
                    .OrderByDescending(option => GryphonPoints(option.Field)).FirstOrDefault();
                if (place == null)
                    continue;
                // At the start of the draw phase, Heist cannot spend its trigger on a future summon.
                place.Heist = place.Blue = false;
                if (GryphonPoints(place.Field) > 0)
                    return place;
                if (fallback == null)
                    fallback = place;
            }
            return fallback;
        }

        private bool CanReviveBlue(int[] field) =>
            OpponentSummons(field, EvilLilLa, false, 0).Any(option => option.Blue);

        private IEnumerable<int[]> LinkedTwinPlacements(int[] field, int twin)
        {
            if (field.Contains(twin))
            {
                yield return field;
                yield break;
            }
            foreach (var option in OpponentSummons(field, twin, false, 0))
                yield return option.Field;
        }

        private IEnumerable<int[]> LinkedTwinPairs(int[] field)
        {
            foreach (var red in LinkedTwinPlacements(field, EvilKiSikil))
            foreach (var pair in LinkedTwinPlacements(red, EvilLilLa))
            {
                if (CanActivateOnField(pair, Array.IndexOf(pair, EvilKiSikil))
                    && CanActivateOnField(pair, Array.IndexOf(pair, EvilLilLa)))
                    yield return pair;
            }
        }

        private bool CanRevivePair(int[] field) =>
            !field.Contains(EvilKiSikil) && !field.Contains(EvilLilLa) && LinkedTwinPairs(field).Any();

        private bool CanFollowBlueWithHeist(int[] field, int blockedExtraZones)
        {
            if (field.Count(id => id != 0) > 4 || field.Take(5).Count(id => id == 0) < 3)
                return false;
            foreach (var red in OpponentSummons(field, EvilKiSikil, false, 0))
            foreach (var blue in OpponentSummons(red.Field, EvilLilLa, false, 0))
            {
                if (!blue.Blue || !CanActivateOnField(blue.Field, Array.IndexOf(blue.Field, EvilKiSikil)))
                    continue;
                var remaining = (int[])blue.Field.Clone();
                remaining[Array.IndexOf(remaining, EvilLilLa)] = 0;
                if (OpponentSummons(remaining, Heist, true, 0, blockedExtraZones)
                    .Any(option => option.Heist && (!field.Contains(Gryphon) || GryphonPoints(option.Field) != 0)))
                    return true;
            }
            return false;
        }

        private int SearchRank(PlanPosition p, int score)
        {
            int resources = p.Field.Sum(id => id == 0 ? 0 : Math.Max(1, LinkRating(id)));
            int hand = p.Hand.Count(id => IsTwin(id) || id == Trouble || id == Password);
            int pending = p.Pending == 0 ? 0 : 2;
            int engine = p.Grave.Count(id => id == Engraver || id == Yama || id == LilLa || id == KiSikil);
            int reuse = p.Grave.Count(id => id == Requiem || id == Deal);
            return score * 3 / ScoreUnitsPerTenth + resources * 4 + hand * 3 + pending + engine * 4 + reuse * 3 - p.PasswordBanishes * 4
                + (p.Trouble ? 4 : 0) + (p.Normal ? 0 : 2) + p.Hand.Count
                - TwinSearchesUsed(p) * 3 - p.LinkTwoForLinkOne * 4;
        }

        private int ContinuationRank(PlanPosition p)
        {
            int cards = p.Field.Where(id => id != 0).Sum(id => 8 + Math.Min(2, LinkRating(id)) * 3);
            int twinHand = p.Hand.Count(id => IsTwin(id));
            int combo = p.Grave.Count(id => id == Deal || id == EvilKiSikil || id == EvilLilLa) * 3;
            if (p.Fresh(PlanEffect.Trouble))
            {
                if (p.Hand.Contains(Trouble)) combo += 14;
                if (p.Fresh(PlanEffect.Password) && p.Hand.Contains(Password)) combo += 14;
            }
            if (p.Has(Requiem) && p.Fresh(PlanEffect.Requiem)) combo += 15;
            if (p.Has(Engraver)) combo += 12;
            if (p.Hand.Contains(Frost) && p.Field.Any(IsBlueTwin)
                || p.Hand.Contains(Treat) && p.Field.Any(IsRedTwin))
                combo += 12;
            if (p.Hand.Contains(NightPhoto) && p.Grave.Any(id => IsRedTwin(id) || IsBlueTwin(id)))
                combo += 10;
            if (p.Grave.Contains(Engraver) && p.Fresh(PlanEffect.Engraver) && p.Grave.Count(LightFiend) > 1)
                combo += 12;
            if (p.Grave.Contains(Requiem)) combo += 8;
            if (p.Grave.Contains(Yama) && p.Fresh(PlanEffect.Yama) && p.Trouble) combo += 13;
            if (p.Fresh(PlanEffect.Abao) && (p.Grave.Contains(LilLa) && p.Fresh(PlanEffect.SmallBlue)
                || p.Grave.Contains(KiSikil) && p.Fresh(PlanEffect.SmallRed)))
                combo += p.Has(Abao) ? 20 : 7;
            if (p.Has(Yama) && p.Extra.Contains(Abao)) combo += 16;
            bool restart = p.Fresh(PlanEffect.Abao) && p.Extra.Contains(Abao) && p.Hand.Count > 0
                && (p.Grave.Contains(LilLa) && p.Fresh(PlanEffect.SmallBlue)
                    || p.Grave.Contains(KiSikil) && p.Fresh(PlanEffect.SmallRed));
            if (restart && p.Has(EvilLilLa))
            {
                if (p.Has(Yama)) combo += 32;
                else if (p.Has(Engraver) && p.Field.Count(Fiend) >= 3 && p.Extra.Contains(Yama)) combo += 26;
            }
            if (p.Has(Abao) || p.ReturningAbao != 0)
            {
                combo += 21;
                if (p.Grave.Contains(Yama) && p.Trouble || !p.Fresh(PlanEffect.Yama))
                    combo += 27;
            }
            if (p.Has(Sequence) && p.Fresh(PlanEffect.Fusion)) combo += 15;
            combo += SunnyFollowup(p) * 5;
            if (p.Twins && p.Fresh(PlanEffect.Fusion)) combo -= 14;
            if (p.Has(Nightfall) && p.Field.Count(id => id != 0) == 1) combo -= 7;
            return cards + twinHand * 10 + combo + p.Score / ScoreUnitsPerTenth + (p.Pending != 0 ? 3 : 0) - p.PasswordBanishes * 4
                - TwinSearchesUsed(p) * 3 - p.LinkTwoForLinkOne * 4;
        }

        private enum ContinuationKind { None, Starter, Yama, Deal, Scout, NewYear, Photo, Search, Frost }

        private static ContinuationKind FreeDrawPressureContinuation(PlanPosition p)
        {
            ulong pending = p.Pending & ~p.Used;
            if ((pending & Flag(PlanEffect.FrostDraw)) != 0 && !p.SearchLocked
                && p.Grave.Contains(Frost) && p.Field.Any(Evil))
                return ContinuationKind.Frost;
            if ((pending & Flag(PlanEffect.NewYearJump)) != 0
                && (p.Grave.Contains(NewYear) && p.DrawEffects.FromSummon(CardLocation.Grave, false) == 0
                    || p.Hand.Contains(NewYear) && p.DrawEffects.FromSummon(CardLocation.Hand, false) == 0))
                return ContinuationKind.NewYear;
            if (p.DrawEffects.Moon == 0 && ((pending & Flag(PlanEffect.NewYearSearch)) != 0
                    && (p.Grave.Contains(Password) || !p.SearchLocked && p.Deck.Contains(Password))
                || !p.SearchLocked && p.SpellSlots < 5
                    && (p.Fresh(PlanEffect.Password) && p.Hand.Contains(Password) && p.Deck.Contains(Trouble)
                        || p.Fresh(PlanEffect.Trouble) && p.Hand.Contains(Trouble) && p.Deck.Contains(NightPhoto))))
                return ContinuationKind.Search;
            return ContinuationKind.None;
        }

        private static ContinuationKind ImmediateContinuationKind(PlanPosition p)
        {
            ulong pending = p.Pending & ~p.Used;
            if ((pending & Flag(PlanEffect.SmallRed)) != 0 && p.Deck.Any(IsBlueTwin)
                || (pending & Flag(PlanEffect.SmallBlue)) != 0 && p.Deck.Any(IsRedTwin))
                return ContinuationKind.Starter;
            if ((pending & Flag(PlanEffect.Yama)) != 0 && p.Grave.Any(id => Fiend(id)
                && id != Requiem && id != Yama && p.CanRevive(id)))
                return ContinuationKind.Yama;
            if (p.Has(Engraver) && p.Extra.Contains(Abao) && p.Extra.Contains(Yama)
                && p.Has(EvilLilLa) && !p.Field.Any(IsRedTwin) && p.Fresh(PlanEffect.BlueRevive)
                && p.Grave.Contains(Deal) && p.CanRevive(Deal))
                return ContinuationKind.Deal;
            if (p.ReturningAbao != 0 && p.Fresh(PlanEffect.NewYearSearch)
                && (p.Has(Scout) && p.Fresh(PlanEffect.Scout)
                    || p.Extra.Contains(Scout) && p.Field.Any(IsBlueTwin))
                && p.Deck.Contains(NewYear))
                return ContinuationKind.Scout;
            if ((pending & (Flag(PlanEffect.NewYearJump) | Flag(PlanEffect.NewYearSearch))) != 0)
                return ContinuationKind.NewYear;
            if (p.ReturningAbao != 0 && p.Fresh(PlanEffect.Fusion) && p.Extra.Contains(Desirae)
                && p.Grave.Contains(Engraver) && p.Grave.Contains(Requiem)
                && p.Grave.Contains(Scout) && p.Fresh(PlanEffect.PhotoRevive)
                && (p.Hand.Contains(NightPhoto) || p.Has(NightPhoto)))
                return ContinuationKind.Photo;
            return ContinuationKind.None;
        }

        private static int ContinuationReserve(PlanPosition p)
        {
            int value = p.Continuation == ContinuationKind.None ? 0 : 32;
            if (p.ReturningAbao != 0 && p.Grave.Contains(Engraver) && p.Grave.Contains(Yama)
                && (p.Fresh(PlanEffect.Yama) || p.Has(Scout)))
                value += 48;
            if (p.ReturningAbao != 0 && p.Grave.Contains(Scout) && p.Fresh(PlanEffect.PhotoRevive)
                && (p.Hand.Contains(NightPhoto) || p.Has(NightPhoto)))
                value += 36;
            return value;
        }

        private static int ReliableSetupRank(PlanPosition p)
        {
            int rank = 0;
            if ((p.Set & (SetDisconnect | SetChallenge)) == (SetDisconnect | SetChallenge))
                rank += 18;
            if (p.Has(Nightfall) && p.Fresh(PlanEffect.Nightfall)
                && p.Deck.Contains(Disconnect) && p.Deck.Contains(Challenge))
                rank += 22;
            if (p.Extra.Contains(Nightfall) && p.Fresh(PlanEffect.Nightfall)
                && p.Has(EvilLilLa) && p.Has(EvilKiSikil))
                rank += 18;
            if (p.Trouble && p.Grave.Contains(Yama) && p.Fresh(PlanEffect.Yama))
                rank += 12;
            return rank;
        }

        private static int RestartSetupRank(PlanPosition p)
        {
            if (!CanRestart(p) || !p.Extra.Contains(Abao) && !p.Has(Abao))
                return 0;
            int count = p.Field.Count(id => id != 0);
            if (count == 1 && p.Has(Abao))
                return 32;
            if (!p.Has(EvilLilLa))
                return 0;
            if (count == 2 && p.Has(Yama))
                return 30;
            return count == 3 && p.Field.Count(Fiend) == 3 && p.Extra.Contains(Yama) ? 24 : 0;
        }

        private static int SearchFamily(PlanPosition p)
        {
            int result = 0;
            if (p.Requiem) result |= 1;
            if (p.Has(Heist) || p.Twins) result |= 2;
            if (p.Has(Abao) || p.ReturningAbao != 0) result |= 4;
            if (p.Has(Sequence) || !p.Fresh(PlanEffect.Fusion)) result |= 8;
            if (p.Has(Sunny) || !p.Fresh(PlanEffect.Sunny)) result |= 16;
            if (p.Has(Nightfall)) result |= 32;
            if (p.Has(Gryphon)) result |= 64;
            if (p.Trouble || p.Grave.Contains(Trouble)) result |= 128;
            return result;
        }

        private IEnumerable<PlanPosition> SelectFrontier(List<PlanPosition> states, int width, bool reserveContinuations,
            bool timed)
        {
            if (states.Count <= width)
                return states;

            // Keep placements that change effects, then reserve room for different engines.
            var ranker = timed ? OpeningBranchRanker : null;
            var diverse = states.OrderByDescending(p => Math.Max(p.Rank, p.Potential))
                .ThenByDescending(p => UsesSecondTurnModel ? ranker?.Score(p) ?? 0 : 0)
                .ThenByDescending(p => UsesSecondTurnModel ? TwinTieScore(p) : 0)
                .ThenBy(p => p.LinkTwoForLinkOne).ThenByDescending(p => p.FirstTwinSearchMove)
                .ThenBy(p => p.FirstYamaGraveMove).ThenBy(p => p.FirstSweetSendMove)
                .ThenBy(p => p.FirstNewYearSendWithoutSearch)
                .GroupBy(p => p.SearchKey(ignorePlacement: true))
                .SelectMany(group => group.Take(group.First().Has(Gryphon) ? 5 : 2))
                .ToList();
            var scoring = diverse.OrderByDescending(p => p.Rank).Take(width / 5);
            if (ranker != null)
                _trainedFrontiers++;
            var promising = ranker == null ? diverse.OrderByDescending(p => p.Potential).ToArray()
                : diverse.OrderByDescending(ranker.Priority).ToArray();
            var immediate = !reserveContinuations ? Enumerable.Empty<PlanPosition>()
                : promising.Where(p => p.Continuation != ContinuationKind.None)
                .GroupBy(p => p.Continuation)
                .SelectMany(group => group.GroupBy(p => new {
                    p.ReturningAbao,
                    p.DealSend,
                    p.TroublePick,
                    Family = SearchFamily(p)
                }).SelectMany(bucket => bucket.Take(2)).Take(5))
                .Take(width / 5);
            var searches = promising.GroupBy(p => new { p.TroublePick, p.DealSend, Engine = SearchFamily(p) & 157 })
                .Select(group => group.First()).Take(width / 3);
            var families = promising.GroupBy(SearchFamily).Select(group => group.ToArray()).ToList();
            var developing = new List<PlanPosition>();
            for (int index = 0; developing.Count < width && families.Count != 0; index++)
            {
                foreach (var group in families)
                    if (index < group.Length)
                        developing.Add(group[index]);
                families.RemoveAll(group => index + 1 >= group.Length);
            }
            var incumbent = immediate.Concat(scoring).Concat(searches).Concat(developing).Distinct().Take(width);
            var recovery = timed && _recoverySearchActive ? RecoveryBranchRanker.Value : null;
            if (recovery == null || width < 8)
                return incumbent;
            var retained = incumbent.ToList();
            var original = new HashSet<PlanPosition>(retained);
            // Propose at most one eighth of the beam. Preserve the original prefix,
            // including its immediate continuations and high-scoring states.
            var proposals = diverse.OrderByDescending(recovery.Priority).Take(width)
                .Where(next => !original.Contains(next)).Take(width / 8).ToList();
            if (proposals.Count != 0)
                _recoveryFrontiers++;
            return retained.Take(width - proposals.Count).Concat(proposals);
        }

        private enum BoardSearchPolicy { Reference, HoldBackrow, DevelopEngines, ResourceExchange, ContinueEffects, PreservePhoto, Tactical }

        private static int EngineSetupRank(PlanPosition p)
        {
            int rank = 0;
            if (p.Fresh(PlanEffect.Requiem) && p.Extra.Contains(Requiem)
                && (p.Has(Deal) || p.Has(EvilKiSikil)))
                rank += 12;
            if (p.Has(Engraver) && (p.Extra.Contains(Sequence) || p.Extra.Contains(Yama)))
                rank += 20;
            if (p.Has(Sequence) && p.Fresh(PlanEffect.Fusion))
                rank += 14;
            if (p.Has(LacrimaFusion) && (p.Has(Engraver) || p.Fresh(PlanEffect.Engraver)
                && p.Grave.Contains(Engraver)))
                rank += 18;
            if (p.Extra.Contains(Caesar) && p.Has(LacrimaFusion) && p.Has(Engraver) && !p.Twins)
                rank += 28;
            if (p.Trouble && p.Grave.Contains(Yama) && p.Fresh(PlanEffect.Yama))
                rank += 15;
            return rank;
        }

        private static int ResourceExchangeRank(PlanPosition p)
        {
            // These are ordering weights, in material units. Only score exchanges
            // whose costs can currently be paid; the terminal scorer scores disruption.
            const int body = 12;
            int rank = 0;
            bool freeMain = (Occupied(p.Field) & BoardMain) != BoardMain;
            if (p.Fresh(PlanEffect.Requiem) && p.Extra.Contains(Requiem)
                && (p.Deck.Contains(Engraver) || p.Hand.Contains(Engraver))
                && (p.Has(Deal) || p.Has(EvilKiSikil)))
                rank += body;

            // Engraver pays one other LIGHT Fiend from the graveyard for a body.
            bool engraverCanReturn = freeMain && p.Fresh(PlanEffect.Engraver) && p.Grave.Contains(Engraver)
                && p.Grave.Any(id => id != Engraver && LightFiend(id));
            if (engraverCanReturn)
                rank += body;
            if (p.Has(Engraver) && (p.Extra.Contains(Sequence) || p.Extra.Contains(Yama)))
                rank += 20;
            if (p.Has(Sequence) && p.Fresh(PlanEffect.Fusion) && p.Grave.Count(LightFiend) >= 2)
                rank += 14;
            if (p.Has(LacrimaFusion) && (p.Has(Engraver) || engraverCanReturn))
                rank += 18;
            if (p.Extra.Contains(Caesar) && p.Has(LacrimaFusion) && p.Has(Engraver) && !p.Twins)
                rank += 28;

            // Photo trades its current body and a spent spell for a properly summoned
            // Evil Twin. Keep the Link-rating gain rather than counting both bodies.
            if (p.Has(NightPhoto) && p.Fresh(PlanEffect.PhotoRevive) && PhotoRevivalCost(p) != 0)
            {
                int gain = p.Grave.Where(id => Evil(id) && p.CanRevive(id)).Select(LinkRating).DefaultIfEmpty().Max() - 1;
                rank += Math.Max(0, gain) * (body / 2);
            }

            // A spare Trouble is a destruction target, not an extra monster.
            if (freeMain && p.Trouble && p.Grave.Contains(Yama) && p.Fresh(PlanEffect.Yama))
                rank += body;
            return rank;
        }

        private static string BoardScoreKey(PlanPosition p)
        {
            int revivalOptions = (p.Extra.Contains(Heist) ? 1 : 0) | (p.Extra.Contains(EvilLilLa) ? 2 : 0)
                | (p.Extra.Contains(EvilKiSikil) ? 4 : 0) | (p.Extra.Contains(Sunny) ? 8 : 0)
                | (p.Extra.Contains(Deal) ? 16 : 0);
            return string.Join(":", p.NormalZones, p.Set, p.Equipment, p.ReturningAbao, p.Unrevivable, revivalOptions,
                p.BlockedExtraZones, HasNibiruDefense(p), p.Hand.Count > 0, p.Hand.Contains(NewYear),
                string.Join(",", p.Field), string.Join(",", p.Grave));
        }

        private sealed class PlanResult
        {
            public IList<PlanMove> Moves;
            public PlanPosition Terminal;
            public int Score, States, Cutoffs, ReturnZone, ScoreEvaluations;
        }

        private PlanResult FindBoard(PlanPosition start, Func<PlanMove, bool> rootLegal, int width, int limit,
            Stopwatch timer = null, int timeLimitMs = 0, BoardSearchPolicy policy = BoardSearchPolicy.Reference)
        {
            var beam = new List<PlanPosition> { start };
            var explored = new Dictionary<string, (int downgrades, int searchMove, int caesar, int yama, int sweet, int newYear)>();
            var scoreCache = timer == null ? null : new Dictionary<string, int>();
            var exactCache = timer == null ? null : new Dictionary<string, (int score, int returnZone)>();
            start.Score = EndBoardScore(start, true);
            var best = start;
            int count = 0;
            int cutoffs = 0;
            int scoreEvaluations = 0;
            int visited = 0;
            bool continueEffects = policy == BoardSearchPolicy.ContinueEffects;
            bool drawPressure = start.DrawEffects.Active;
            bool preservePhoto = policy == BoardSearchPolicy.PreservePhoto;
            bool budgetSpent = timer != null && timer.ElapsedMilliseconds >= timeLimitMs;
            for (int depth = 0; depth < 52 && !budgetSpent && beam.Count > 0; depth++)
            {
                var next = new List<PlanPosition>();
                foreach (var pos in beam)
                {
                    foreach (var offered in NextPositions(pos, preservePhoto ? BoardSearchPolicy.HoldBackrow : policy))
                    {
                        if (timer != null && (++visited & 15) == 0 && timer.ElapsedMilliseconds >= timeLimitMs)
                        {
                            budgetSpent = true;
                            break;
                        }
                        if (preservePhoto && UsesPhoto(offered.Move))
                            continue;
                        if (depth == 0 && offered.Move != null && rootLegal != null && !rootLegal(offered.Move))
                            continue;
                        var state = offered;
                        // When Frost is the only pending trigger, evaluate the summon and its draw together.
                        if (drawPressure && (state.Pending & ~state.Used) == Flag(PlanEffect.FrostDraw))
                            state = DrawWithFrost(state) ?? state;
                        string stateKey = state.SearchKey();
                        var priorities = (state.LinkTwoForLinkOne, -state.FirstTwinSearchMove,
                            state.Has(Caesar) ? state.FirstCaesarSummonMove : int.MaxValue, state.FirstYamaGraveMove,
                            state.FirstSweetSendMove, state.FirstNewYearSendWithoutSearch);
                        if (explored.TryGetValue(stateKey, out var previous) && previous.CompareTo(priorities) <= 0)
                            continue;
                        explored[stateKey] = priorities;
                        count++;
                        string scoreKey = state.EvaluationKey = scoreCache == null ? null : BoardScoreKey(state);
                        if (scoreKey != null && scoreCache.TryGetValue(scoreKey, out int baseScore))
                            state.Score = baseScore + NetDrawPoints(state);
                        else
                        {
                            state.Score = EndBoardScore(state, false);
                            scoreEvaluations++;
                            if (scoreKey != null)
                                scoreCache.Add(scoreKey, state.Score - NetDrawPoints(state));
                        }
                        state.Rank = SearchRank(state, state.Score);
                        state.Potential = ContinuationRank(state);
                        if (policy == BoardSearchPolicy.HoldBackrow || continueEffects || preservePhoto)
                            state.Potential += ReliableSetupRank(state) + RestartSetupRank(state);
                        else if (policy == BoardSearchPolicy.DevelopEngines)
                            state.Potential += EngineSetupRank(state) + RestartSetupRank(state);
                        else if (policy == BoardSearchPolicy.ResourceExchange)
                            state.Potential += ResourceExchangeRank(state) + RestartSetupRank(state);
                        if (continueEffects)
                        {
                            state.Continuation = ImmediateContinuationKind(state);
                            state.Potential += ContinuationReserve(state);
                        }
                        else if (drawPressure)
                        {
                            state.Continuation = FreeDrawPressureContinuation(state);
                            // Compare these branches after their available follow-up has had room to resolve.
                            if (state.Continuation != ContinuationKind.None)
                                state.Potential += 30;
                        }
                        next.Add(state);
                        if (count >= limit)
                        {
                            budgetSpent = true;
                            break;
                        }
                    }
                    if (budgetSpent)
                        break;
                }
                // Keep the last evaluated plan if the clock expires before the next frontier.
                if (timer != null && timer.ElapsedMilliseconds >= timeLimitMs)
                    break;
                if (next.Count > width)
                    cutoffs += next.Count - width;
                beam = SelectFrontier(next, width, continueEffects || drawPressure, timer != null).ToList();
                foreach (var state in beam)
                {
                    if (timer != null && timer.ElapsedMilliseconds >= timeLimitMs)
                    {
                        budgetSpent = true;
                        break;
                    }
                    int score;
                    if (exactCache != null && exactCache.TryGetValue(state.EvaluationKey, out var evaluated))
                    {
                        score = evaluated.score + NetDrawPoints(state);
                        state.AbaoZone = evaluated.returnZone;
                    }
                    else
                    {
                        score = EndBoardScore(state, true);
                        if (exactCache != null)
                            exactCache.Add(state.EvaluationKey, (score - NetDrawPoints(state), state.AbaoZone));
                    }
                    state.Score = score;
                    if (score > best.Score || score == best.Score && PreferScoredEnd(state, best))
                        best = state;
                }
            }

            var moves = new List<PlanMove>();
            for (var position = best; position.Previous != null; position = position.Previous)
                if (position.Move != null)
                    moves.Add(position.Move);
            moves.Reverse();
            return new PlanResult { Moves = moves, Terminal = best, Score = best.Score, States = count,
                Cutoffs = cutoffs, ReturnZone = best.AbaoZone, ScoreEvaluations = scoreEvaluations };
        }

        private bool PreferScoredEnd(PlanPosition candidate, PlanPosition best)
        {
            if (!UsesSecondTurnModel) return PreferEnd(candidate, best);
            int tie = TwinTieScore(candidate).CompareTo(TwinTieScore(best));
            if (tie != 0) return tie > 0;
            // Only a completed end-board tie can see these calibrated weights.
            // The frontier and tactical ranking still use the incumbent.
            var model = TerminalTieRanker.Value ?? OpeningBranchRanker;
            if (model != null)
            {
                int comparison = model.Score(candidate).CompareTo(model.Score(best));
                if (comparison != 0) return comparison > 0;
            }
            return candidate.Depth < best.Depth;
        }

        private int SunnyFollowup(PlanPosition p)
        {
            bool hasPair = p.Grave.Any(id => IsRedTwin(id) && p.CanRevive(id))
                && p.Grave.Any(id => IsBlueTwin(id) && p.CanRevive(id));
            bool room = p.Field.Take(5).Any(id => id == 0);
            int sunny = Array.IndexOf(p.Field, Sunny), nightfall = Array.IndexOf(p.Field, Nightfall);
            if (sunny >= 0 && p.Fresh(PlanEffect.Sunny) && hasPair && room
                && CanActivateOnField(p.Field, sunny, p.NormalZones))
                return 3;
            if (nightfall >= 0 && p.Extra.Contains(Sunny) && p.Fresh(PlanEffect.NightfallRotate)
                && CanActivateOnField(p.Field, nightfall, p.NormalZones)
                && p.Grave.Any(id => Evil(id) && id != EvilKiSikil && id != EvilLilLa))
                return hasPair && room ? 3 : 1;
            if (!p.Grave.Contains(Sunny)) return 0;
            var otherLight = p.Grave.Where(id => id != Sunny && id != Engraver && LightFiend(id)).Distinct().ToList();
            bool enablesEngraver = room && p.Fresh(PlanEffect.Engraver) &&
                p.Grave.Contains(Engraver) && otherLight.Count == 0;
            bool enablesFusion = room && !p.Twins && p.Fresh(PlanEffect.Fusion)
                && CanActivateOnField(p.Field, Array.IndexOf(p.Field, Sequence), p.NormalZones)
                && (p.Extra.Contains(LacrimaFusion) &&
                    otherLight.Count + (p.Grave.Contains(Engraver) ? 1 : 0) == 1
                    || p.Extra.Contains(Desirae) && p.Grave.Contains(Engraver) && otherLight.Count == 1);
            return enablesEngraver || enablesFusion ? 3 : 0;
        }

        private int TwinTieScore(PlanPosition p)
        {
            bool independentStarter = p.Hand.Any(id => id == KiSikil || id == LilLa || id == Sweet || id == NewYear);
            int resources = p.Hand.Where(id => IsHandTrap(id) && id != _CardId.InfiniteImpermanence)
                .Distinct().Count() + (independentStarter ? 1 : 0);
            if (p.Hand.Contains(NightPhoto) && CanAccessPhoto(p)) resources++;
            int conversion = (p.Set & SetChallenge) != 0 && p.Grave.Contains(EvilLilLa) && p.CanRevive(EvilLilLa) ? 1 : 0;
            if (SunnyFollowup(p) != 0) conversion++;
            int timing = p.EarlyRequiem && !p.Fresh(PlanEffect.Requiem) ? 1 : 0;
            int resilience = (HasNibiruDefense(p) ? 1 : 0) + (p.SearchLocked && independentStarter ? 1 : 0);
            return 5 * Math.Min(resources, 4) + 2 * conversion + 2 * timing + 3 * resilience;
        }

        private static bool PreferEnd(PlanPosition candidate, PlanPosition best)
        {
            int search = TwinSearchesUsed(best).CompareTo(TwinSearchesUsed(candidate));
            if (search != 0)
                return search > 0;
            if (candidate.LinkTwoForLinkOne != best.LinkTwoForLinkOne)
                return candidate.LinkTwoForLinkOne < best.LinkTwoForLinkOne;
            if (candidate.FirstTwinSearchMove != best.FirstTwinSearchMove)
                return candidate.FirstTwinSearchMove > best.FirstTwinSearchMove;
            int defensePreference = CompareNibiruDefense(candidate, best);
            if (defensePreference != 0)
                return defensePreference > 0;
            int caesarTiming = CompareCaesarTiming(candidate, best);
            if (caesarTiming != 0)
                return caesarTiming > 0;
            if (candidate.FirstYamaGraveMove != best.FirstYamaGraveMove)
                return candidate.FirstYamaGraveMove < best.FirstYamaGraveMove;
            if (candidate.FirstSweetSendMove != best.FirstSweetSendMove)
                return candidate.FirstSweetSendMove < best.FirstSweetSendMove;
            if (candidate.FirstNewYearSendWithoutSearch != best.FirstNewYearSendWithoutSearch)
                return candidate.FirstNewYearSendWithoutSearch < best.FirstNewYearSendWithoutSearch;
            int preservation = CompareBoardPreservation(candidate, best);
            if (preservation != 0)
                return preservation > 0;
            int reservePreference = CompareNightfallReserve(candidate, best);
            if (reservePreference != 0)
                return reservePreference > 0;
            int candidateEquips = candidate.Equipment == 3 ? 2 : candidate.Equipment == 0 ? 0 : 1;
            int previousEquips = best.Equipment == 3 ? 2 : best.Equipment == 0 ? 0 : 1;
            if (candidateEquips != previousEquips)
                return candidateEquips > previousEquips;
            if (candidate.Hand.Count(IsHandTrap) != best.Hand.Count(IsHandTrap))
                return candidate.Hand.Count(IsHandTrap) > best.Hand.Count(IsHandTrap);
            int currentTraps = (candidate.Set & SetChallenge) + (candidate.Set & SetDisconnect);
            int previousTraps = (best.Set & SetChallenge) + (best.Set & SetDisconnect);
            if (currentTraps != previousTraps)
                return currentTraps > previousTraps;
            if (candidate.EngraverRevivalMove != best.EngraverRevivalMove)
                return candidate.EngraverRevivalMove > best.EngraverRevivalMove;
            if (candidate.Depth != best.Depth)
                return candidate.Depth < best.Depth;
            return Occupied(candidate.Field) > Occupied(best.Field);
        }

        private static int CompareCaesarTiming(PlanPosition candidate, PlanPosition best) =>
            candidate.Has(Caesar) && best.Has(Caesar)
                ? best.FirstCaesarSummonMove.CompareTo(candidate.FirstCaesarSummonMove) : 0;

        private static int TwinSearchesUsed(PlanPosition p) =>
            (p.Fresh(PlanEffect.Password) ? 0 : 1) + (p.Fresh(PlanEffect.Trouble) ? 0 : 1);

        private static int CompareBoardPreservation(PlanPosition candidate, PlanPosition best)
        {
            if (candidate.Has(Gryphon) && best.Has(Gryphon))
            {
                int lacrima = candidate.Has(LacrimaFusion).CompareTo(best.Has(LacrimaFusion));
                if (lacrima != 0)
                    return lacrima;
            }
            int challenge = (candidate.Set & SetChallenge).CompareTo(best.Set & SetChallenge);
            if (challenge != 0)
                return challenge;
            int password = best.PasswordBanishes.CompareTo(candidate.PasswordBanishes);
            if (password != 0)
                return password;
            return UsedPhoto(best).CompareTo(UsedPhoto(candidate));
        }

        private static bool UsedPhoto(PlanPosition p) => !p.Fresh(PlanEffect.Photo) || !p.Fresh(PlanEffect.PhotoRevive);

        private static bool UsesPhoto(PlanMove move) =>
            move != null && move.Card == NightPhoto && move.Type == ExecutorType.Activate;

        private static int CompareNibiruDefense(PlanPosition candidate, PlanPosition best)
        {
            bool candidatePhoto = HasPhotoDefense(candidate), bestPhoto = HasPhotoDefense(best);
            bool candidateRequiem = HasRequiemDefense(candidate), bestRequiem = HasRequiemDefense(best);
            if ((candidateRequiem || candidatePhoto) != (bestRequiem || bestPhoto))
                return 0;
            int retainedPhoto = candidatePhoto.CompareTo(bestPhoto);
            if (retainedPhoto != 0)
                return retainedPhoto;
            return candidate.EarlyRequiem.CompareTo(best.EarlyRequiem);
        }

        private static int CompareNightfallReserve(PlanPosition candidate, PlanPosition best)
        {
            if (!candidate.Has(Nightfall) || !best.Has(Nightfall))
                return 0;
            int scout = candidate.Grave.Contains(Scout).CompareTo(best.Grave.Contains(Scout));
            return scout != 0 ? scout : candidate.Extra.Contains(Heist).CompareTo(best.Extra.Contains(Heist));
        }

    }
}
