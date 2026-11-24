using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Serialization;
using YGOSharp.OCGWrapper;
using YGOSharp.OCGWrapper.Enums;

namespace WindBot.Game.AI.Decks
{
    public partial class TwinsExecutor
    {
        // Board-building moves after combat is unavailable or complete. The server still decides which moves and selections are legal.
        private enum PlanEffect
        {
            Trouble, Password, Deal, Scout, NewYearJump, NewYearSearch, SmallRed, SmallBlue,
            Sweet, Photo, Requiem, Engraver, RedDraw, RedRevive, BluePop, BlueRevive,
            HeistRevive, Abao, Yama, Sunny, Nightfall, Fusion, FusionRevive, RequiemEquip,
            SequenceEquip, GryphonSet, NightfallRotate, PhotoRevive, FrostDraw, FrostSummon,
        }

        private struct OpponentDrawEffects
        {
            public int MaxxC, Fuwalos, Purulia, Nyalus, Moon;
            public bool Active => MaxxC + Fuwalos + Purulia + Nyalus + Moon != 0;

            public int FromSummon(CardLocation source, bool normal)
            {
                if (normal)
                    return source == CardLocation.Hand ? Purulia : 0;
                return MaxxC + (source == CardLocation.Deck || source == CardLocation.Extra ? Fuwalos : 0)
                    + (source == CardLocation.Hand ? Purulia : 0)
                    + (source == CardLocation.Grave || source == CardLocation.Removed ? Nyalus : 0);
            }
        }

        [DataContract]
        private sealed class PlanMove
        {
            [DataMember]
            public ExecutorType Type;
            [DataMember]
            public int Card;
            [DataMember]
            public int Effect = -1;
            [DataMember]
            public int[] Materials;
            [DataMember]
            public int[] MaterialIds;
            [DataMember]
            public int[] SummonedIds;
            [DataMember]
            public int[] SummonedZones;
            [DataMember]
            public int Places;
            [DataMember]
            public int[] Selection;
            [DataMember]
            public int Discard = -1;
            [DataMember]
            public int Fusion;
            [DataMember]
            public int Target;
            [DataMember(EmitDefaultValue = false)]
            public CardLocation TargetLocation;
            [DataMember(EmitDefaultValue = false)]
            public CardLocation ActivationLocation;
            [DataMember(EmitDefaultValue = false)]
            public CardLocation SummonSource;
            [DataMember]
            public int Option;
            [DataMember]
            public bool Trigger;
            [DataMember]
            public int Cost;
            [DataMember]
            public int[] Traps;


            public override string ToString()
            {
                return string.Join("/", Type, Card, Effect, Places, Target, Cost, Fusion,
                    string.Join(",", Materials ?? new int[0]), string.Join(",", Selection ?? new int[0]));
            }
        }

        private sealed class PlanPosition
        {
            public int[] Field = new int[7];
            public List<int> Hand, Deck, Extra, Grave;
            public ulong Used, Pending;
            public int Draws, Set, SpellSlots, NormalZones, NewYearZone = -1, ReturningAbao, Equipment;
            public OpponentDrawEffects DrawEffects;
            public int OpponentDraws;
            public int AbaoZone = -1, Unrevivable, TroublePick, DealSend;
            public bool Normal, Requiem, DealLinked, Trouble, Fiends, Twins, Sealing, RedDrawWithSweetInDeck;
            public int OwnSummons;
            public bool EarlyRequiem, RequiemDefenseAtNibiruWindow, PhotoAtNibiruWindow, SearchLocked;
            public bool UsesExtraZones = true, FusionNeedsLink;
            public int BlockedExtraZones;
            public PlanPosition Previous;
            public PlanMove Move;
            public int Depth, Score, Rank, Potential;
            public ContinuationKind Continuation;
            public int ActionCount, EngraverRevivalMove = int.MaxValue, FirstTwinSearchMove = int.MaxValue;
            public int FirstCaesarSummonMove = int.MaxValue, FirstYamaGraveMove = int.MaxValue;
            public int FirstSweetSendMove = int.MaxValue, FirstNewYearSendWithoutSearch = int.MaxValue;
            public int LinkTwoForLinkOne, PasswordBanishes, NewYearBanished;
            private string _resourceKey;
            public string EvaluationKey;

            public PlanPosition Next(PlanMove move)
            {
                var next = (PlanPosition)MemberwiseClone();
                next._resourceKey = null;
                next.EvaluationKey = null;
                next.Field = (int[])Field.Clone();
                next.Hand = new List<int>(Hand);
                next.Deck = new List<int>(Deck);
                next.Extra = new List<int>(Extra);
                next.Grave = new List<int>(Grave);
                next.Previous = this;
                next.Move = move;
                next.Depth++;
                if (move != null)
                    next.ActionCount++;
                return next;
            }

            public string Key(bool ignorePlacement = false)
            {
                // The beam looks up both the exact key and a placement-independent
                // key. Cache the shared canonical resource inventory on this snapshot.
                if (_resourceKey == null)
                {
                    _resourceKey = string.Join(":", Used, Pending, Draws, Set, SpellSlots,
                        ReturningAbao, Equipment, Unrevivable, Normal, Requiem, DealLinked, Trouble, Fiends, Twins, Sealing,
                        UsesExtraZones, FusionNeedsLink, BlockedExtraZones,
                        string.Join(",", Hand.OrderBy(id => id)), string.Join(",", Deck.OrderBy(id => id)),
                        string.Join(",", Extra.OrderBy(id => id)), string.Join(",", Grave.OrderBy(id => id)));
                }
                return string.Join(":", _resourceKey, ignorePlacement ? 0 : NormalZones,
                    ignorePlacement ? -1 : NewYearZone,
                    string.Join(",", ignorePlacement ? Field.OrderBy(id => id) : Field.AsEnumerable()));
            }

            public bool Has(int card) => Array.IndexOf(Field, card) >= 0;
            public string SearchKey(bool ignorePlacement = false) =>
                Key(ignorePlacement) + ":" + (OwnSummons + (EarlyRequiem ? 8 : 0) + (PhotoAtNibiruWindow ? 16 : 0))
                    + ":" + RequiemDefenseAtNibiruWindow + ":" + PasswordBanishes + ":" + OpponentDraws
                    + ":" + NewYearBanished + ":" + RedDrawWithSweetInDeck;
            public bool CanRevive(int id) => (Unrevivable & ExtraFlag(id)) == 0;
            public bool Fresh(PlanEffect effect) => (Used & Flag(effect)) == 0;
            public void Use(PlanEffect effect)
            {
                Used |= Flag(effect);
                Pending &= ~Flag(effect);
            }
        }

        private const int SetDisconnect = 1, SetChallenge = 2, SetCrossout = 4, SetImperm = 8;
        private const int NibiruThreshold = 5;
        private const int NibiruDefensePoints = 10;

        private static bool CanAccessPhoto(PlanPosition p, bool pendingNewYear = false)
        {
            if (!p.Fresh(PlanEffect.Photo) || !p.Fresh(PlanEffect.PhotoRevive))
                return false;
            int releasedEquips = p.Has(Desirae) ? (p.Equipment & 1) + ((p.Equipment >> 1) & 1) : 0;
            bool canOpenSearch = !p.SearchLocked && p.SpellSlots - releasedEquips < 5;
            bool canUsePassword = canOpenSearch && p.Fresh(PlanEffect.Password)
                && (p.Hand.Contains(Password)
                    || (pendingNewYear || (p.Pending & Flag(PlanEffect.NewYearSearch)) != 0)
                    && p.Fresh(PlanEffect.NewYearSearch)
                    && (p.Deck.Contains(Password) || p.Grave.Contains(Password)))
                && p.Deck.Any(id => id == Trouble || id == Challenge || id == Disconnect);
            bool photoAvailable = p.Hand.Contains(NightPhoto)
                || canOpenSearch && p.Fresh(PlanEffect.Trouble) && p.Deck.Contains(NightPhoto)
                    && (p.Hand.Contains(Trouble) || canUsePassword && p.Deck.Contains(Trouble));
            if (!photoAvailable || PhotoRevivalCost(p) == 0 && !canUsePassword)
                return false;
            // Photo plays after Nibiru resolves. Monsters it tributes can pay either
            // graveyard requirement, except a New Year that is banished on leaving.
            var graveAfterNibiru = p.Grave.Concat(p.Field.Where((id, zone) => id != 0 && zone != p.NewYearZone));
            var revivalTargets = graveAfterNibiru.Where(id => Evil(id) && p.CanRevive(id)).Take(2).ToArray();
            return revivalTargets.Length != 0 && graveAfterNibiru.Any(id => (IsRedTwin(id) || IsBlueTwin(id))
                && (revivalTargets.Length > 1 || id != revivalTargets[0]));
        }

        private static int PhotoRevivalCost(PlanPosition p) =>
            p.Grave.Contains(Trouble) ? Trouble : p.Grave.Contains(Password) ? Password : 0;

        private static bool HasPhotoDefense(PlanPosition p) =>
            p.PhotoAtNibiruWindow || p.OwnSummons < NibiruThreshold && CanAccessPhoto(p);

        private static bool CanJumpNewYear(PlanPosition p)
        {
            if (!p.Fresh(PlanEffect.NewYearJump) || !p.Hand.Contains(NewYear) && !p.Grave.Contains(NewYear))
                return false;
            bool freeMainZone = (Occupied(p.Field) & BoardMain) != BoardMain;
            return Enumerable.Range(0, p.Field.Length)
                .Any(zone => (zone < 5 || freeMainZone) && IsLightOrDark(p.Field[zone]));
        }

        private static bool HasRequiemDefense(PlanPosition p) => p.RequiemDefenseAtNibiruWindow
            || p.OwnSummons < NibiruThreshold && p.EarlyRequiem && CanJumpNewYear(p);

        private static bool HasNibiruDefense(PlanPosition p) => HasRequiemDefense(p) || HasPhotoDefense(p);

        private static void CaptureNibiruWindow(PlanPosition p, bool pendingNewYear = false)
        {
            p.RequiemDefenseAtNibiruWindow = p.EarlyRequiem && CanJumpNewYear(p);
            p.PhotoAtNibiruWindow = CanAccessPhoto(p, pendingNewYear);
        }
        private const int BoardMain = 31;
        private const int BoardEnemyMain = BoardMain << 16;
        private static readonly int[] PlanMainCards = {
            KiSikil, LilLa, NewYear, Frost, Treat, Sweet, NightPhoto, Trouble, Password, Engraver, Disconnect, Challenge, Crossout
        };
        private static readonly int[] PlanExtraCards = {
            Scout, Deal, Requiem, EvilKiSikil, EvilLilLa, Heist, Sequence, Yama, Abao, Sunny, Nightfall,
            Desirae, LacrimaFusion, Caesar, Gryphon
        };
        private static readonly int[] PlanLinkCards = {
            Deal, Scout, Requiem, EvilKiSikil, EvilLilLa, Heist, Sequence, Yama, Abao, Sunny, Nightfall, Gryphon
        };
        private static ulong Flag(PlanEffect effect) => 1UL << (int)effect;
        private static int PlanCode(ClientCard card) => card == null ? 0 : card.GetOriginCode();

        private static bool IsRedTwin(int id) =>
            id == KiSikil || id == Frost || id == NewYear || id == Deal || id == EvilKiSikil;
        private static bool IsBlueTwin(int id) =>
            id == LilLa || id == Treat || id == Sweet || id == Scout || id == EvilLilLa;
        private static bool IsTwin(int id) => IsRedTwin(id) || IsBlueTwin(id) || id == NightPhoto || Evil(id);
        private static bool Evil(int id) => id == Deal || id == Scout || id == EvilKiSikil || id == EvilLilLa
            || id == Heist || id == Sunny || id == Nightfall;
        private static bool LightFiend(int id) => id == Deal || id == Requiem || id == EvilKiSikil || id == Sequence
            || id == Sunny || id == Engraver || id == Desirae || id == LacrimaFusion || id == Gryphon;
        private static bool Fiend(int id) => LightFiend(id) || id == Scout || id == EvilLilLa || id == Heist
            || id == Yama || id == Abao || id == Nightfall || id == Caesar;
        private static bool IsLightOrDark(int id) => IsTwin(id) || Fiend(id);
        private static int LinkRating(int id)
        {
            if (id == Deal || id == Scout || id == Requiem)
                return 1;
            if (id == Abao || id == Nightfall || id == Sunny || id == Gryphon)
                return 4;
            return PlanLinkCards.Contains(id) ? 2 : 0;
        }

        private static int TrapBit(int id)
        {
            if (id == Disconnect) return SetDisconnect;
            if (id == Challenge) return SetChallenge;
            if (id == Crossout) return SetCrossout;
            return id == _CardId.InfiniteImpermanence ? SetImperm : 0;
        }

        // A known in-engine card may be retained as a response, even when it has no combo actions.
        private static bool IsHandTrap(int id)
        {
            return id == 0 || id == Sweet || id == Crossout || id == Nibiru || id == Fuwalos || id == Droll
                || id == 94145022 || id == 97268402 || id == 59438930 || id == _CardId.MaxxC
                || id == _CardId.AshBlossom || id == _CardId.EffectVeiler || id == _CardId.GhostOgreAndSnowRabbit
                || id == _CardId.InfiniteImpermanence;
        }

        private readonly Dictionary<int, int[]> _planArrows = new Dictionary<int, int[]>();

        private int Arrows(int card, int zone)
        {
            if (LinkRating(card) == 0)
                return 0;
            if (!_planArrows.TryGetValue(card, out int[] links))
            {
                int markers = NamedCard.Get(card)?.LinkMarker ?? 0;
                links = Enumerable.Range(0, 7).Select(i => ProjectArrows(markers, i)).ToArray();
                _planArrows[card] = links;
            }
            return links[zone];
        }

        private static readonly int[,] ArrowDirections = {
            { 1, 8, 64 },
            { 2, 0, 128 },
            { 4, 32, 256 },
        };

        private static int ProjectArrows(int markers, int zone)
        {
            int x = zone < 5 ? zone : zone == 5 ? 1 : 3;
            int y = zone < 5 ? 0 : 1;
            int result = 0;
            for (int target = 0; target < 12; target++)
            {
                int targetX = target < 5 ? target : target < 7 ? (target == 5 ? 1 : 3) : 11 - target;
                int targetY = target < 5 ? 0 : target < 7 ? 1 : 2;
                int dx = targetX - x, dy = targetY - y;
                if (Math.Abs(dx) > 1 || Math.Abs(dy) > 1 || (markers & ArrowDirections[dx + 1, dy + 1]) == 0)
                    continue;
                result |= 1 << (target < 7 ? target : target + 9);
            }
            return result;
        }

        private int PointedZones(int[] field)
        {
            int links = 0;
            for (int i = 0; i < field.Length; i++)
                links |= Arrows(field[i], i);
            return links;
        }

        private static int Occupied(int[] field)
        {
            int result = 0;
            for (int i = 0; i < field.Length; i++)
                if (field[i] != 0)
                    result |= 1 << i;
            return result;
        }

        private bool CanActivateOnField(int[] field, int index, int normalZones = 0)
        {
            if (index < 0)
                return false;
            if ((normalZones & (1 << index)) != 0 || !field.Contains(Gryphon))
                return true;
            // In a link, both endpoints are linked. An arrow to an empty zone only authorizes an Extra Deck placement.
            return (PointedZones(field) & (1 << index)) != 0 || (Arrows(field[index], index) & Occupied(field)) != 0;
        }

        private bool CanActivate(PlanPosition p, int card) =>
            CanActivateOnField(p.Field, Array.IndexOf(p.Field, card), p.NormalZones);

        private int FreeLinkZones(int[] field)
        {
            int occupied = Occupied(field);
            int zones = PointedZones(field) & BoardMain & ~occupied;
            if ((occupied & (3 << 5)) == 0)
                zones |= 3 << 5;
            return zones;
        }

        private static void ToGrave(PlanPosition p, int zone)
        {
            int card = p.Field[zone];
            p.Field[zone] = 0;
            if (zone == p.NewYearZone)
            {
                p.NewYearZone = -1;
                p.NewYearBanished++;
            }
            else
            {
                p.Grave.Add(card);
                if (card == Yama && p.FirstYamaGraveMove == int.MaxValue)
                    p.FirstYamaGraveMove = p.ActionCount;
            }
            p.NormalZones &= ~(1 << zone);
            if (card == Desirae)
            {
                if ((p.Equipment & 1) != 0)
                {
                    p.Grave.Add(Requiem);
                    p.SpellSlots--;
                }
                if ((p.Equipment & 2) != 0)
                {
                    p.Grave.Add(Sequence);
                    p.SpellSlots--;
                }
                p.Equipment = 0;
            }
        }

        private int ExtraDeckZoneMask(PlanPosition p, bool link)
        {
            if (!p.UsesExtraZones)
                return BoardMain & ~Occupied(p.Field);

            int zones = FreeLinkZones(p.Field) & ~p.BlockedExtraZones;
            if (!link && !p.FusionNeedsLink)
                zones |= BoardMain & ~Occupied(p.Field);
            return zones;
        }

        private static void ReturnCard(PlanPosition p, int card)
        {
            p.Grave.Remove(card);
            p.Unrevivable &= ~ExtraFlag(card);
            (PlanExtraCards.Contains(card) ? p.Extra : p.Deck).Add(card);
        }

        private void Put(PlanPosition p, int card, int zone)
        {
            p.Field[zone] = card;
            p.OwnSummons = Math.Min(p.OwnSummons + 1, NibiruThreshold + 1);
            if (p.OwnSummons == NibiruThreshold)
                CaptureNibiruWindow(p,
                    card == NewYear && CanActivateOnField(p.Field, zone, p.NormalZones));
            if (!CanActivateOnField(p.Field, zone, p.NormalZones))
                return;

            if (card == NewYear) p.Pending |= Flag(PlanEffect.NewYearSearch);
            if (card == KiSikil && Occupied(p.Field) == 1 << zone) p.Pending |= Flag(PlanEffect.SmallRed);
            if (card == LilLa && Occupied(p.Field) == 1 << zone) p.Pending |= Flag(PlanEffect.SmallBlue);
            if (card == EvilKiSikil && p.Field.Any(IsBlueTwin)) p.Pending |= Flag(PlanEffect.RedDraw);
            if (card == EvilLilLa && p.Field.Any(IsRedTwin) && (p.Trouble || p.Has(Gryphon)))
                p.Pending |= Flag(PlanEffect.BluePop);
            if (card == Nightfall) p.Pending |= Flag(PlanEffect.Nightfall);
            if (card == LacrimaFusion) p.Pending |= Flag(PlanEffect.FusionRevive);
        }

        private IEnumerable<PlanPosition> PlaceMain(PlanPosition p, int card, CardLocation source,
            bool normal = false, int allowed = BoardMain, bool chargeDraw = true, int preferred = -1,
            bool preserveGeometry = false)
        {
            int before = Occupied(p.Field);
            int pointed = PointedZones(p.Field);
            // For unlinked non-Link bodies, identical availability on each kind of square
            // does not need five separate nodes. Link bodies retain the exact positions.
            int tried = 0;
            var ordinaryZones = new[] { 0, 4, 2, 1, 3 };
            var zones = preferred < 0 ? ordinaryZones : new[] { preferred }.Concat(ordinaryZones.Where(zone => zone != preferred));
            foreach (int zone in zones)
            {
                if ((before & (1 << zone)) != 0 || (allowed & (1 << zone)) == 0)
                    continue;
                if (LinkRating(card) == 0 && !p.Has(Gryphon) && !normal && !preserveGeometry)
                {
                    int kind = (pointed & (1 << zone)) == 0 ? 1 : 2;
                    if ((tried & kind) != 0)
                        continue;
                    tried |= kind;
                }
                yield return PlaceBody(p, card, zone, source, normal, chargeDraw);
                if ((before & (3 << 5)) == 0 && !p.Field.Any(id => LinkRating(id) > 0))
                    yield break;
                // Link placement and the eventual lock use exact zones.
                if (LinkRating(card) == 0 && !p.Has(Gryphon) && pointed == 0)
                    yield break;
            }
        }

        private PlanPosition PlaceBody(PlanPosition p, int card, int zone, CardLocation source,
            bool normal = false, bool chargeDraw = true)
        {
            var next = p.Next(p.Move);
            next.Previous = p.Previous;
            next.Depth = p.Depth;
            next.ActionCount = p.ActionCount;
            next.Move = CopyMove(p.Move, p.Move.Places | (1 << zone));
            next.Move.SummonedIds = (p.Move.SummonedIds ?? new int[0]).Concat(new[] { card }).ToArray();
            next.Move.SummonedZones = (p.Move.SummonedZones ?? new int[0]).Concat(new[] { zone }).ToArray();
            next.Move.SummonSource |= source;
            if (normal)
                next.NormalZones |= 1 << zone;
            Put(next, card, zone);
            if (chargeDraw)
                AddOpponentDraws(next, next.DrawEffects.FromSummon(source, normal));
            return next;
        }

        private static void AddOpponentDraws(PlanPosition p, int count)
        {
            if (count == 0)
                return;
            p.OpponentDraws += count;
            if (!p.SearchLocked && p.Fresh(PlanEffect.FrostDraw) && p.Grave.Contains(Frost) && p.Field.Any(Evil))
                p.Pending |= Flag(PlanEffect.FrostDraw);
        }

        private IEnumerable<PlanPosition> PlaceFusion(PlanPosition p, int card)
        {
            int zones = ExtraDeckZoneMask(p, link: false);
            // A future Gryphon can leave two currently equivalent zones with very different links.
            bool deferredGryphon = card == Desirae && (p.Grave.Contains(Gryphon)
                || p.Extra.Contains(Gryphon) && p.Extra.Contains(Heist) && !p.Hand.Contains(NightPhoto))
                && (p.Has(Abao) || p.ReturningAbao != 0);
            foreach (var result in PlaceMain(p, card, CardLocation.Extra, allowed: zones,
                preferred: deferredGryphon ? 2 : -1, preserveGeometry: deferredGryphon))
                yield return result;
            for (int zone = 5; zone < 7; zone++)
                if ((zones & (1 << zone)) != 0)
                    yield return PlaceBody(p, card, zone, CardLocation.Extra);
        }

        private static PlanMove CopyMove(PlanMove move, int places)
        {
            return new PlanMove {
                Card = move.Card, Type = move.Type, Effect = move.Effect, Trigger = move.Trigger,
                Target = move.Target, TargetLocation = move.TargetLocation, ActivationLocation = move.ActivationLocation,
                SummonSource = move.SummonSource,
                Discard = move.Discard, Fusion = move.Fusion, Selection = move.Selection,
                Materials = move.Materials, MaterialIds = move.MaterialIds, Places = places,
                SummonedIds = move.SummonedIds, SummonedZones = move.SummonedZones,
                Cost = move.Cost, Option = move.Option, Traps = move.Traps
            };
        }

        private static PlanMove Effect(int card, int number, int target = 0, bool trigger = false)
        {
            return new PlanMove { Type = ExecutorType.Activate, Card = card, Effect = number, Target = target, Trigger = trigger };
        }

        private static PlanPosition UseEffect(PlanPosition p, PlanEffect flag, PlanMove move)
        {
            var next = p.Next(move);
            next.Use(flag);
            return next;
        }

        private static PlanPosition DrawWithFrost(PlanPosition p)
        {
            if (p.SearchLocked || (p.Pending & Flag(PlanEffect.FrostDraw)) == 0 || !p.Fresh(PlanEffect.FrostDraw)
                || !p.Grave.Contains(Frost) || !p.Field.Any(Evil))
                return null;
            var next = UseEffect(p, PlanEffect.FrostDraw, Effect(Frost, 1, trigger: true));
            next.Grave.Remove(Frost);
            next.Hand.Add(0);
            next.Draws++;
            return next;
        }

        private IEnumerable<PlanPosition> PendingMoves(PlanPosition p)
        {
            if ((p.Pending & Flag(PlanEffect.NewYearJump)) != 0 && p.Fresh(PlanEffect.NewYearJump))
            {
                foreach (bool fromGrave in new[] { true, false })
                {
                    if (!(fromGrave ? p.Grave : p.Hand).Contains(NewYear)
                        || !fromGrave && !p.DrawEffects.Active && p.Grave.Contains(NewYear))
                        continue;
                    CardLocation source = fromGrave ? CardLocation.Grave : CardLocation.Hand;
                    for (int i = 0; i < p.Field.Length; i++)
                    {
                        int cost = p.Field[i];
                        if (cost == 0 || !IsLightOrDark(cost))
                            continue;
                        var next = UseEffect(p, PlanEffect.NewYearJump, Effect(NewYear, 0, cost, true));
                        next.Move.ActivationLocation = source;
                        (fromGrave ? next.Grave : next.Hand).Remove(NewYear);
                        ToGrave(next, i);
                        foreach (var result in PlaceMain(next, NewYear, source))
                        {
                            if (fromGrave)
                                result.NewYearZone = Array.IndexOf(result.Field, NewYear);
                            yield return result;
                        }
                    }
                }
            }
            if ((p.Pending & Flag(PlanEffect.NewYearSearch)) != 0 && p.Fresh(PlanEffect.NewYearSearch)
                && (p.Grave.Contains(Password) || !p.SearchLocked && p.Deck.Contains(Password)))
            {
                var next = UseEffect(p, PlanEffect.NewYearSearch, Effect(NewYear, 1, Password, true));
                if (!next.Grave.Remove(Password)) next.Deck.Remove(Password);
                next.Hand.Add(Password);
                AddOpponentDraws(next, next.DrawEffects.Moon);
                yield return next;
            }
            foreach (var small in new[] { PlanEffect.SmallRed, PlanEffect.SmallBlue })
            {
                if ((p.Pending & Flag(small)) == 0 || !p.Fresh(small))
                    continue;
                bool red = small == PlanEffect.SmallRed;
                foreach (int target in p.Hand.Concat(p.Deck).Distinct()
                    .Where(id => red ? IsBlueTwin(id) : IsRedTwin(id)))
                foreach (bool fromDeck in new[] { true, false })
                {
                    if (!(fromDeck ? p.Deck : p.Hand).Contains(target)
                        || !fromDeck && !p.DrawEffects.Active && p.Deck.Contains(target))
                        continue;
                    CardLocation source = fromDeck ? CardLocation.Deck : CardLocation.Hand;
                    var next = UseEffect(p, small, Effect(red ? KiSikil : LilLa, 0, target, true));
                    (fromDeck ? next.Deck : next.Hand).Remove(target);
                    if (p.DrawEffects.Active)
                        next.Move.TargetLocation = source;
                    int preferred = p.DrawEffects.Active && p.Move?.Type == ExecutorType.Summon
                        && p.Field[2] == p.Move.Card ? 1 : -1;
                    foreach (var result in PlaceMain(next, target, source, preferred: preferred)) yield return result;
                }
            }
            if (!p.SearchLocked && (p.Pending & Flag(PlanEffect.RedDraw)) != 0 && p.Fresh(PlanEffect.RedDraw))
            {
                var next = UseEffect(p, PlanEffect.RedDraw, Effect(EvilKiSikil, 0, trigger: true));
                next.RedDrawWithSweetInDeck = p.Deck.Contains(Sweet);
                next.Hand.Add(0);
                next.Draws++;
                yield return next;
            }
            var frostDraw = DrawWithFrost(p);
            if (frostDraw != null)
                yield return frostDraw;
            if ((p.Pending & Flag(PlanEffect.BluePop)) != 0 && p.Fresh(PlanEffect.BluePop) && p.Trouble
                && p.Grave.Contains(Yama) && p.Fresh(PlanEffect.Yama))
            {
                var next = UseEffect(p, PlanEffect.BluePop, Effect(EvilLilLa, 0, Trouble, true));
                next.Trouble = false;
                next.SpellSlots--;
                next.Grave.Add(Trouble);
                next.Pending |= Flag(PlanEffect.Yama);
                yield return next;
            }
            if ((p.Pending & Flag(PlanEffect.BluePop)) != 0 && p.Fresh(PlanEffect.BluePop) && p.Has(Gryphon))
            {
                var next = UseEffect(p, PlanEffect.BluePop, Effect(EvilLilLa, 0, Gryphon, true));
                ToGrave(next, Array.IndexOf(next.Field, Gryphon));
                if (p.Grave.Contains(Yama) && p.Fresh(PlanEffect.Yama))
                    next.Pending |= Flag(PlanEffect.Yama);
                yield return next;
            }
            if ((p.Pending & Flag(PlanEffect.Yama)) != 0 && p.Fresh(PlanEffect.Yama))
            {
                foreach (int target in p.Grave.Distinct().Where(Fiend))
                {
                    if (target == Requiem || target == Yama || !p.CanRevive(target))
                        continue;
                    var next = UseEffect(p, PlanEffect.Yama, Effect(Yama, 1, target, true));
                    next.Grave.Remove(Yama);
                    next.Grave.Remove(target);
                    foreach (var result in PlaceMain(next, target, CardLocation.Grave)) yield return result;
                }
            }
            if ((p.Pending & Flag(PlanEffect.FusionRevive)) != 0 && p.Fresh(PlanEffect.FusionRevive))
            {
                foreach (int target in p.Grave.Distinct().Where(LightFiend))
                {
                    if (target == Requiem || !p.CanRevive(target))
                        continue;
                    var next = UseEffect(p, PlanEffect.FusionRevive, Effect(LacrimaFusion, 0, target, true));
                    next.Grave.Remove(target);
                    foreach (var result in PlaceMain(next, target, CardLocation.Grave)) yield return result;
                }
            }
            if ((p.Pending & Flag(PlanEffect.Nightfall)) != 0 && p.Fresh(PlanEffect.Nightfall) && p.Fresh(PlanEffect.NightfallRotate))
            {
                int[] traps = new[] { Disconnect, Challenge }.Where(id =>
                    (p.Set & TrapBit(id)) == 0 && (p.Deck.Contains(id) || p.Grave.Contains(id)))
                    .Take(Math.Max(0, 5 - p.SpellSlots)).ToArray();
                if (traps.Length > 0)
                {
                    var move = Effect(Nightfall, 0, trigger: true);
                    move.Traps = traps;
                    var next = UseEffect(p, PlanEffect.Nightfall, move);
                    foreach (int trap in traps)
                    {
                        if (!next.Deck.Remove(trap)) next.Grave.Remove(trap);
                        next.Set |= TrapBit(trap);
                        next.SpellSlots++;
                    }
                    yield return next;
                }
            }
            if ((p.Pending & Flag(PlanEffect.GryphonSet)) != 0 && p.Fresh(PlanEffect.GryphonSet) && p.SpellSlots < 5)
            {
                int zone = Array.IndexOf(p.Field, Gryphon);
                bool coLinked = Enumerable.Range(0, 7).Any(i =>
                    (Arrows(Gryphon, zone) & (1 << i)) != 0 && (Arrows(p.Field[i], i) & (1 << zone)) != 0);
                foreach (int target in new[] { Disconnect, Challenge }.Where(id =>
                    p.Grave.Contains(id) && (p.Set & TrapBit(id)) == 0))
                foreach (int discard in Discards(p).Take(1))
                {
                    var move = Effect(Gryphon, 0, target, true);
                    move.Discard = discard;
                    var next = UseEffect(p, PlanEffect.GryphonSet, move);
                    next.Hand.Remove(discard);
                    next.Grave.Remove(target);
                    next.Set |= TrapBit(target);
                    next.SpellSlots++;
                    if (coLinked && !p.SearchLocked)
                    {
                        next.Draws++;
                        next.Hand.Add(0);
                    }
                    yield return next;
                }
            }
        }

        private static IEnumerable<int> Discards(PlanPosition p)
        {
            return p.Hand.Distinct().OrderBy(id => IsHandTrap(id)).ThenBy(id => PlanMainCards.Contains(id));
        }

        private static int PreferredTwinSend(int sender, Func<int, bool> available, bool sweetLive,
            bool newYearLive, bool spareNewYear)
        {
            if (sender == Deal)
                return sweetLive && available(Sweet) ? Sweet : 0;
            if (sender != Scout)
                return 0;
            if ((newYearLive || spareNewYear) && available(NewYear))
                return NewYear;
            return available(Frost) ? Frost : 0;
        }

        private int LivePreferredTwinSend(int sender, Func<int, bool> available)
        {
            bool sweetLive = (_planUsed & Flag(PlanEffect.Sweet)) == 0
                && Bot.MonsterZone.Take(5).Any(card => card == null)
                && Bot.GetMonsters().Any(card => card.IsFaceup() && IsRedTwin(PlanCode(card)));
            bool newYearUsed = (_planUsed & Flag(PlanEffect.NewYearJump)) != 0;
            bool spareNewYear = newYearUsed && Bot.Banished.Any(card => card.IsCode(NewYear))
                && (Bot.HasInHand(Frost) || Bot.HasInGraveyard(Frost));
            return PreferredTwinSend(sender, available, sweetLive, !newYearUsed, spareNewYear);
        }

        private bool LiveCanSendFrostWithScout() =>
            (_planUsed & Flag(PlanEffect.NewYearJump)) == 0
            && (Bot.HasInHand(NewYear) || Bot.HasInGraveyard(NewYear))
            && !Bot.HasInGraveyard(Frost)
            && Bot.MonsterZone.Where((card, zone) => card != null
                && (zone < 5 || Bot.MonsterZone.Take(5).Any(main => main == null)))
                .Any(card => IsLightOrDark(PlanCode(card)));

        private IEnumerable<PlanPosition> SearchMoves(PlanPosition p)
        {
            if (!p.SearchLocked && p.SpellSlots < 5 && p.Hand.Contains(Trouble) && p.Fresh(PlanEffect.Trouble))
            {
                foreach (int target in p.Deck.Distinct().Where(id => IsTwin(id) && !Evil(id)))
                {
                    var next = UseEffect(p, PlanEffect.Trouble, Effect(Trouble, 0, target));
                    next.FirstTwinSearchMove = Math.Min(p.FirstTwinSearchMove, next.ActionCount);
                    next.Hand.Remove(Trouble);
                    next.Deck.Remove(target);
                    next.Hand.Add(target);
                    AddOpponentDraws(next, next.DrawEffects.Moon);
                    next.Trouble = true;
                    next.SpellSlots++;
                    next.TroublePick = target;
                    yield return next;
                }
            }
            if (!p.SearchLocked && p.SpellSlots < 5 && p.Hand.Contains(Password) && p.Fresh(PlanEffect.Password))
            {
                foreach (int target in p.Deck.Distinct().Where(id => id == Trouble || id == Challenge || id == Disconnect))
                {
                    var next = UseEffect(p, PlanEffect.Password, Effect(Password, 0, target));
                    next.FirstTwinSearchMove = Math.Min(p.FirstTwinSearchMove, next.ActionCount);
                    next.Hand.Remove(Password);
                    next.Grave.Add(Password);
                    next.Deck.Remove(target);
                    next.Hand.Add(target);
                    AddOpponentDraws(next, next.DrawEffects.Moon);
                    yield return next;
                }
            }

            foreach (var send in new[] { Deal, Scout })
            {
                var flag = send == Deal ? PlanEffect.Deal : PlanEffect.Scout;
                if (!p.Fresh(flag) || !CanActivate(p, send))
                    continue;
                bool sweetLive = p.Fresh(PlanEffect.Sweet) && (Occupied(p.Field) & BoardMain) != BoardMain
                    && p.Field.Any(IsRedTwin);
                bool newYearLive = p.Fresh(PlanEffect.NewYearJump);
                int preferred = PreferredTwinSend(send, p.Deck.Contains, sweetLive, newYearLive,
                    !newYearLive && p.NewYearBanished > 0 && (p.Hand.Contains(Frost) || p.Grave.Contains(Frost)));
                var targets = preferred == 0 ? p.Deck.Concat(p.Extra).Distinct().Where(send == Deal
                    ? (Func<int, bool>)IsBlueTwin : IsRedTwin) : new[] { preferred };
                if (send == Scout && preferred == NewYear && p.Deck.Contains(Frost)
                    && !p.Grave.Contains(Frost) && CanJumpNewYear(p))
                    targets = targets.Append(Frost);
                foreach (int target in targets)
                {
                    var next = UseEffect(p, flag, Effect(send, 0, target));
                    if (send == Deal) next.DealSend = target;
                    if (target == Sweet)
                        next.FirstSweetSendMove = Math.Min(p.FirstSweetSendMove, next.ActionCount);
                    if (target == NewYear && !p.Hand.Contains(NewYear) && !p.Hand.Contains(Password) && !p.Hand.Contains(Trouble))
                        next.FirstNewYearSendWithoutSearch = Math.Min(p.FirstNewYearSendWithoutSearch, next.ActionCount);
                    if (!next.Deck.Remove(target))
                    {
                        next.Extra.Remove(target);
                        next.Unrevivable |= ExtraFlag(target);
                    }
                    next.Grave.Add(target);
                    // New Year can chain after this effect pays its send-to-grave cost.
                    if (next.Fresh(PlanEffect.NewYearJump) && (next.Grave.Contains(NewYear) || next.Hand.Contains(NewYear)))
                        next.Pending |= Flag(PlanEffect.NewYearJump);
                    yield return next;
                }
            }
        }

        private IEnumerable<PlanPosition> BodyMoves(PlanPosition p)
        {
            foreach (int card in p.Hand.Distinct())
            {
                if (!p.Normal && IsTwin(card) && card != NightPhoto)
                {
                    var next = p.Next(new PlanMove { Card = card, Type = ExecutorType.Summon });
                    next.Hand.Remove(card);
                    next.Normal = true;
                    foreach (var result in PlaceMain(next, card, CardLocation.Hand, normal: true)) yield return result;
                }
                if (card == Frost && p.Fresh(PlanEffect.FrostSummon) && p.Field.Any(IsBlueTwin)
                    || card == Treat && p.Field.Any(IsRedTwin))
                {
                    var next = p.Next(new PlanMove { Card = card, Type = ExecutorType.SpSummon });
                    if (card == Frost)
                        next.Use(PlanEffect.FrostSummon);
                    next.Hand.Remove(card);
                    foreach (var result in PlaceMain(next, card, CardLocation.Hand)) yield return result;
                }
            }

            if (p.Grave.Contains(Sweet) && p.Fresh(PlanEffect.Sweet) && p.Field.Any(IsRedTwin))
            {
                var next = UseEffect(p, PlanEffect.Sweet, Effect(Sweet, 1));
                next.Grave.Remove(Sweet);
                foreach (var result in PlaceMain(next, Sweet, CardLocation.Grave)) yield return result;
            }
            if (p.Hand.Contains(NightPhoto) && p.Fresh(PlanEffect.Photo))
            {
                foreach (int target in p.Grave.Distinct().Where(id => IsBlueTwin(id) || IsRedTwin(id))
                    .OrderBy(id => id == Scout))
                {
                    var next = UseEffect(p, PlanEffect.Photo, Effect(NightPhoto, 0, target));
                    next.Hand.Remove(NightPhoto);
                    ReturnCard(next, target);
                    foreach (var result in PlaceMain(next, NightPhoto, CardLocation.Hand)) yield return result;
                }
            }
            int photoCost = PhotoRevivalCost(p);
            if (p.Fresh(PlanEffect.PhotoRevive) && CanActivate(p, NightPhoto) && photoCost != 0)
            {
                foreach (int target in p.Grave.Distinct().Where(id => Evil(id) && p.CanRevive(id)))
                {
                    var move = Effect(NightPhoto, 1, target);
                    move.Cost = photoCost;
                    var next = UseEffect(p, PlanEffect.PhotoRevive, move);
                    ToGrave(next, Array.IndexOf(next.Field, NightPhoto));
                    next.Grave.Remove(photoCost);
                    if (photoCost == Password)
                        next.PasswordBanishes++;
                    next.Grave.Remove(target);
                    foreach (var result in PlaceMain(next, target, CardLocation.Grave)) yield return result;
                }
            }
            if (p.Fresh(PlanEffect.Requiem) && CanActivate(p, Requiem))
            {
                foreach (bool fromDeck in new[] { true, false })
                {
                    if (!(fromDeck ? p.Deck : p.Hand).Contains(Engraver))
                        continue;
                    var next = UseEffect(p, PlanEffect.Requiem, Effect(Requiem, 0, Engraver));
                    var source = fromDeck ? CardLocation.Deck : CardLocation.Hand;
                    if (p.DrawEffects.Active)
                        next.Move.TargetLocation = source;
                    ToGrave(next, Array.IndexOf(next.Field, Requiem));
                    (fromDeck ? next.Deck : next.Hand).Remove(Engraver);
                    foreach (var result in PlaceMain(next, Engraver, source)) yield return result;
                }
            }
            if (p.Grave.Contains(Engraver) && p.Fresh(PlanEffect.Engraver))
            {
                foreach (int target in p.Grave.Distinct().Where(id => LightFiend(id) && id != Engraver))
                {
                    var next = UseEffect(p, PlanEffect.Engraver, Effect(Engraver, 2, target));
                    next.EngraverRevivalMove = next.ActionCount;
                    next.Grave.Remove(Engraver);
                    ReturnCard(next, target);
                    foreach (var result in PlaceMain(next, Engraver, CardLocation.Grave)) yield return result;
                }
            }
        }

        private IEnumerable<PlanPosition> RotationMoves(PlanPosition p)
        {
            foreach (int card in new[] { EvilKiSikil, EvilLilLa, Heist })
            {
                bool red = card == EvilKiSikil, blue = card == EvilLilLa;
                PlanEffect flag = red ? PlanEffect.RedRevive : blue ? PlanEffect.BlueRevive : PlanEffect.HeistRevive;
                if (!p.Fresh(flag) || !CanActivate(p, card)
                    || red && p.Field.Any(IsBlueTwin) || blue && p.Field.Any(IsRedTwin))
                    continue;
                foreach (int target in p.Grave.Distinct().Where(id =>
                    (red && IsBlueTwin(id) || blue && IsRedTwin(id) || !red && !blue && (IsRedTwin(id) || IsBlueTwin(id)))
                    && p.CanRevive(id)))
                {
                    var next = UseEffect(p, flag, Effect(card, 1, target));
                    next.Grave.Remove(target);
                    next.Fiends |= red || blue;
                    next.Twins |= !red && !blue;
                    foreach (var result in PlaceMain(next, target, CardLocation.Grave)) yield return result;
                }
            }
            if (p.Fresh(PlanEffect.Abao) && CanActivate(p, Abao) && p.Hand.Count > 0)
            {
                int position = Array.IndexOf(p.Field, Abao);
                foreach (int target in p.Grave.Distinct().Where(id => IsLightOrDark(id) && p.CanRevive(id) && id != Requiem))
                foreach (int discard in AbaoDiscards(p).Take(1))
                {
                    var move = Effect(Abao, 0, target);
                    move.Discard = discard;
                    move.Option = 3;
                    var next = UseEffect(p, PlanEffect.Abao, move);
                    next.Hand.Remove(discard);
                    next.Grave.Add(discard);
                    next.Grave.Remove(target);
                    next.Field[position] = 0;
                    next.ReturningAbao = 1;
                    foreach (var result in PlaceMain(next, target, CardLocation.Grave)) yield return result;
                }
                if (p.Trouble && p.Fresh(PlanEffect.Yama) && p.Grave.Contains(Yama))
                foreach (int discard in AbaoDiscards(p).Take(1))
                {
                    var move = Effect(Abao, 0, Trouble);
                    move.Discard = discard;
                    move.Option = 2;
                    var next = UseEffect(p, PlanEffect.Abao, move);
                    next.Hand.Remove(discard);
                    next.Grave.Add(discard);
                    next.Trouble = false;
                    next.SpellSlots--;
                    next.Grave.Add(Trouble);
                    next.Pending |= Flag(PlanEffect.Yama);
                    yield return next;
                }
            }

            if (p.Fresh(PlanEffect.Sunny) && CanActivate(p, Sunny))
            {
                int source = Array.IndexOf(p.Field, Sunny);
                foreach (int red in p.Grave.Distinct().Where(id => IsRedTwin(id) && p.CanRevive(id)))
                foreach (int blue in p.Grave.Distinct().Where(id => IsBlueTwin(id) && p.CanRevive(id)))
                {
                    var move = Effect(Sunny, 0);
                    move.Selection = new[] { red, blue };
                    var next = UseEffect(p, PlanEffect.Sunny, move);
                    next.Grave.Remove(red);
                    next.Grave.Remove(blue);
                    ToGrave(next, source);
                    foreach (var left in PlaceMain(next, red, CardLocation.Grave, chargeDraw: false))
                    foreach (var right in PlaceMain(left, blue, CardLocation.Grave, chargeDraw: false))
                    {
                        AddOpponentDraws(right, right.DrawEffects.FromSummon(CardLocation.Grave, false));
                        // Sunny summons both at once. Neither original Twin has
                        // an empty field for its on-summon starter effect.
                        right.Pending &= ~(Flag(PlanEffect.SmallRed) | Flag(PlanEffect.SmallBlue));
                        if (p.OwnSummons < NibiruThreshold && right.OwnSummons >= NibiruThreshold)
                            CaptureNibiruWindow(right);
                        if (red == EvilKiSikil && CanActivate(right, red))
                            right.Pending |= Flag(PlanEffect.RedDraw);
                        if (blue == EvilLilLa && (right.Trouble || right.Has(Gryphon)) && CanActivate(right, blue))
                            right.Pending |= Flag(PlanEffect.BluePop);
                        yield return right;
                    }
                }
            }

            if (p.Fresh(PlanEffect.NightfallRotate) && p.Fresh(PlanEffect.Nightfall) && CanActivate(p, Nightfall))
            {
                foreach (int cost in p.Grave.Where(id => Evil(id) && id != EvilKiSikil && id != EvilLilLa).Distinct())
                foreach (int target in p.Extra.Where(id => Evil(id) && id != Nightfall).Distinct())
                {
                    var move = Effect(Nightfall, 1, target);
                    move.Cost = cost;
                    var next = UseEffect(p, PlanEffect.NightfallRotate, move);
                    next.Grave.Remove(cost);
                    next.Extra.Remove(target);
                    next.Unrevivable |= ExtraFlag(target);
                    ToGrave(next, Array.IndexOf(next.Field, Nightfall));
                    int places = ExtraDeckZoneMask(next, link: true);
                    for (int zone = 0; zone < 7; zone++)
                    {
                        if ((places & (1 << zone)) == 0)
                            continue;
                        var result = next.Next(CopyMove(move, 1 << zone));
                        result.Previous = p;
                        result.Depth = next.Depth;
                        result.Move.SummonSource = CardLocation.Extra;
                        Put(result, target, zone);
                        AddOpponentDraws(result, result.DrawEffects.FromSummon(CardLocation.Extra, false));
                        yield return result;
                    }
                }
            }
        }

        private static bool LegalLink(int card, int[] materials)
        {
            int count = materials.Length;
            if (card == Deal) return count == 1 && IsRedTwin(materials[0]);
            if (card == Scout) return count == 1 && IsBlueTwin(materials[0]);
            if (card == Requiem) return count == 1 && LightFiend(materials[0]);
            if (count < 2) return false;
            if (card == Sequence) return count == 2 && materials.Any(LightFiend);
            if (card == Yama) return count == 2 && materials.All(Fiend);
            if (card == Heist) return count == 2 && materials.Any(IsTwin);
            if (card == Nightfall) return count == 2 && materials.Any(id => IsRedTwin(id) || IsBlueTwin(id));
            if (card == EvilKiSikil) return count == 2 && materials.Any(IsRedTwin);
            if (card == EvilLilLa) return count == 2 && materials.Any(IsBlueTwin);
            if (card == Abao) return materials.Any(Fiend);
            if (card == Sunny) return materials.Any(Evil);
            return card == Gryphon && count == materials.Distinct().Count();
        }

        private IEnumerable<PlanPosition> ExtraMoves(PlanPosition p, int onlyCard = 0)
        {
            int occupied = Occupied(p.Field);
            for (int mask = occupied; mask != 0; mask = (mask - 1) & occupied)
            {
                var slots = Enumerable.Range(0, 7).Where(i => (mask & (1 << i)) != 0).ToArray();
                if (slots.Length > 4)
                    continue;
                int[] materials = slots.Select(i => p.Field[i]).ToArray();
                int sums = 1 << slots.Length;
                foreach (int material in materials)
                    if (LinkRating(material) > 1)
                        sums |= sums << (LinkRating(material) - 1);

                foreach (int card in p.Extra)
                {
                    if (onlyCard != 0 && card != onlyCard)
                        continue;
                    if (p.Twins && !Evil(card) || p.Fiends && !Fiend(card)
                        || p.Has(Sweet) && !Fiend(card))
                        continue;
                    bool xyz = card == Caesar && slots.Length == 2
                        && materials.Contains(Engraver) && materials.Contains(LacrimaFusion);
                    if (!xyz && (LinkRating(card) == 0 || (sums & (1 << LinkRating(card))) == 0
                        || !LegalLink(card, materials) || card == Deal && p.DealLinked || card == Requiem && p.Requiem))
                        continue;

                    var move = new PlanMove { Card = card, Type = ExecutorType.SpSummon, Materials = slots, MaterialIds = materials };
                    var next = p.Next(move);
                    if (xyz && next.FirstCaesarSummonMove == int.MaxValue)
                    {
                        int summonMove = 1;
                        for (var prior = p; prior.Previous != null; prior = prior.Previous)
                            if (prior.Move != null)
                                summonMove++;
                        next.FirstCaesarSummonMove = summonMove;
                    }
                    if (LinkRating(card) == 1 && materials.Length == 1 && LinkRating(materials[0]) == 2)
                        next.LinkTwoForLinkOne++;
                    foreach (int i in slots)
                    {
                        ToGrave(next, i);
                        if (xyz)
                            next.Grave.Remove(p.Field[i]);
                    }
                    next.Extra.Remove(card);
                    next.Unrevivable &= ~ExtraFlag(card);
                    next.DealLinked |= card == Deal;
                    next.Requiem |= card == Requiem;
                    int places = ExtraDeckZoneMask(next, link: !xyz);
                    for (int zone = 0; zone < 7; zone++)
                    {
                        if ((places & (1 << zone)) == 0)
                            continue;
                        var result = next.Next(CopyMove(move, 1 << zone));
                        result.Previous = p;
                        result.Depth = next.Depth;
                        result.Move.SummonSource = CardLocation.Extra;
                        if (card == Requiem && result.OwnSummons < NibiruThreshold)
                            result.EarlyRequiem = true;
                        Put(result, card, zone);
                        AddOpponentDraws(result, result.DrawEffects.FromSummon(CardLocation.Extra, false));
                        if (card == Gryphon && CanActivateOnField(result.Field, zone, result.NormalZones))
                            result.Pending |= Flag(PlanEffect.GryphonSet);
                        yield return result;
                    }
                }
            }
        }

        private IEnumerable<PlanPosition> FusionMoves(PlanPosition p)
        {
            if (p.Fresh(PlanEffect.Fusion) && CanActivate(p, Sequence) && !p.Twins)
            {
                var light = p.Grave.Where(LightFiend).Distinct().ToArray();
                for (int i = 0; i < light.Length; i++)
                for (int j = i + 1; j < light.Length; j++)
                foreach (int fusion in new[] { Desirae, LacrimaFusion })
                {
                    if (!p.Extra.Contains(fusion)
                        || fusion == Desirae && (!light.Contains(Engraver) || light[i] == Engraver || light[j] == Engraver))
                        continue;
                    var move = Effect(Sequence, 0);
                    move.Fusion = fusion;
                    move.Selection = fusion == Desirae ? new[] { light[i], light[j], Engraver } : new[] { light[i], light[j] };
                    var next = UseEffect(p, PlanEffect.Fusion, move);
                    next.Extra.Remove(fusion);
                    foreach (int card in move.Selection)
                        ReturnCard(next, card);
                    foreach (var result in PlaceFusion(next, fusion)) yield return result;
                }
            }

            foreach (int equip in new[] { Requiem, Sequence })
            {
                var flag = equip == Requiem ? PlanEffect.RequiemEquip : PlanEffect.SequenceEquip;
                if (p.SpellSlots >= 5 || !p.Has(Desirae) || !p.Fresh(flag)
                    || !p.Grave.Contains(equip) && !CanActivate(p, equip))
                    continue;
                var next = UseEffect(p, flag, Effect(equip, 1, Desirae));
                if (p.Grave.Contains(equip))
                    next.Grave.Remove(equip);
                else
                    next.Field[Array.IndexOf(next.Field, equip)] = 0;
                next.Equipment |= equip == Requiem ? 1 : 2;
                next.SpellSlots++;
                yield return next;
            }
        }

        private static int ExtraFlag(int id)
        {
            int index = Array.IndexOf(PlanExtraCards, id);
            return index < 0 ? 0 : 1 << index;
        }

        private static IEnumerable<PlanPosition> SetMoves(PlanPosition p)
        {
            // Sets do not supply materials. Explore them as the suffix of a line.
            if (p.SpellSlots >= 5)
                yield break;
            foreach (int card in p.Hand.Distinct())
            {
                if (TrapBit(card) == 0 || (p.Set & TrapBit(card)) != 0)
                    continue;
                var next = p.Next(new PlanMove { Card = card, Type = ExecutorType.SpellSet });
                next.Sealing = true;
                next.Hand.Remove(card);
                next.Set |= TrapBit(card);
                next.SpellSlots++;
                yield return next;
            }
        }

        private static bool CanRestartWith(PlanPosition p, int card)
        {
            if (!p.Grave.Contains(card) || p.Hand.Count == 0 || !p.Fresh(PlanEffect.Abao))
                return false;
            return card == LilLa
                ? p.Fresh(PlanEffect.SmallBlue) && p.Deck.Any(IsRedTwin)
                : p.Fresh(PlanEffect.SmallRed) && p.Deck.Any(IsBlueTwin);
        }

        private static bool CanRestart(PlanPosition p) =>
            CanRestartWith(p, LilLa) || CanRestartWith(p, KiSikil);

        private IEnumerable<PlanPosition> NextPositions(PlanPosition p, BoardSearchPolicy policy = BoardSearchPolicy.Reference)
        {
            // An explicit tactic supplies its own next action. A search in hand does not
            // make a legal development step unavailable to that tactic.
            bool canDeferSearch = policy == BoardSearchPolicy.Tactical;
            if (canDeferSearch)
                policy = BoardSearchPolicy.Reference;
            ulong pending = p.Pending & ~p.Used;
            if (pending != 0)
            {
                // Sunny summons both twins at once. Choose Red's free draw in the
                // same trigger window as Blue's pop, before that pop can trigger Yama.
                var opportunities = PendingMoves(p).ToList();
                var draw = opportunities.FirstOrDefault(next =>
                    next.Move.Card == EvilKiSikil && next.Move.Effect == 0 || next.Move.Card == Frost);
                if (draw != null)
                {
                    yield return draw;
                    yield break;
                }
                if (policy == BoardSearchPolicy.HoldBackrow || policy == BoardSearchPolicy.ContinueEffects)
                {
                    // Two live traps are a reliable payoff, and setting them here
                    // leaves Nightfall available as material for the rest of the board.
                    var setBoth = opportunities.FirstOrDefault(next => next.Move.Traps != null
                        && next.Move.Traps.Length == 2);
                    if (setBoth != null)
                    {
                        yield return setBoth;
                        yield break;
                    }
                }
                foreach (var result in opportunities) yield return result;
                var skipped = p.Next(null);
                skipped.Pending = 0;
                yield return skipped;
                yield break;
            }

            if (!p.Sealing)
            {
                var searches = SearchMoves(p).ToList();
                // Keep the search available as a continuation when the Fiendsmith
                // starter can be exposed first. Ash can change which extender is needed.
                var handSearches = searches.Where(next => next.Move.Card == Password || next.Move.Card == Trouble).ToList();
                if (handSearches.Count != 0)
                {
                    foreach (var next in handSearches) yield return next;
                    bool canOpenRequiem = p.Fresh(PlanEffect.Requiem) && (CanActivate(p, Requiem)
                        || !p.Requiem && p.Extra.Contains(Requiem) && p.Field.Any(LightFiend));
                    bool canDevelopTwins = !p.DealLinked && p.Extra.Contains(Deal) && p.Field.Any(IsRedTwin)
                        || p.Extra.Contains(Scout) && p.Field.Any(IsBlueTwin);
                    if (!canDeferSearch && !canOpenRequiem && !canDevelopTwins && !CanTurnSweetIntoAbao(p))
                        yield break;
                }
                var linkSearches = searches.Where(next => next.Move.Card == Scout || next.Move.Card == Deal).ToList();
                if (linkSearches.Count != 0)
                {
                    foreach (var next in linkSearches) yield return next;
                    yield break;
                }

                if (policy != BoardSearchPolicy.Reference && p.Trouble && CanRestart(p))
                {
                    int monsters = p.Field.Count(id => id != 0);
                    // An original Twin can pull another body only on an otherwise empty field.
                    if (monsters == 1 && p.Has(Abao))
                    {
                        var restart = RotationMoves(p).Where(next => next.Move.Card == Abao
                            && (next.Move.Target == LilLa || next.Move.Target == KiSikil)
                            && CanRestartWith(p, next.Move.Target)).ToList();
                        if (restart.Count != 0)
                        {
                            foreach (var result in restart) yield return result;
                            yield break;
                        }
                    }

                    if (p.Extra.Contains(Abao) && p.Has(EvilLilLa))
                    {
                        int bridge = monsters == 2 && p.Has(Yama) ? Abao
                            : monsters == 3 && p.Field.Count(Fiend) == 3 && p.Extra.Contains(Yama) ? Yama : 0;
                        if (bridge != 0)
                        {
                            var nextLinks = ExtraMoves(p, bridge).Where(next => bridge == Abao
                                || next.Has(EvilLilLa)).ToList();
                            if (nextLinks.Count != 0)
                            {
                                foreach (var result in nextLinks) yield return result;
                                yield break;
                            }
                        }
                    }
                }

                var bodyMoves = BodyMoves(p).ToList();
                var fromRequiem = bodyMoves.Where(next => next.Move.Card == Requiem).ToList();
                if (fromRequiem.Count != 0)
                {
                    foreach (var next in fromRequiem) yield return next;
                    yield break;
                }

                if (policy == BoardSearchPolicy.ResourceExchange && p.Has(Deal) && !p.Fresh(PlanEffect.Deal)
                    && !p.Requiem && p.Extra.Contains(Requiem) && p.Deck.Contains(Engraver)
                    && p.Hand.Contains(NightPhoto) && p.Fresh(PlanEffect.Photo) && p.Grave.Contains(Password))
                {
                    // Photo can replace a Twin body later. Open the unused Fiendsmith
                    // engine with Deal, after taking Sweet's free revival while Deal is red.
                    var sweet = bodyMoves.Where(next => next.Move.Card == Sweet).ToList();
                    if (sweet.Count != 0)
                    {
                        foreach (var result in sweet) yield return result;
                        yield break;
                    }
                    var requiem = ExtraMoves(p, Requiem).Where(next => next.Move.MaterialIds.Contains(Deal)).ToList();
                    if (requiem.Count != 0)
                    {
                        foreach (var result in requiem) yield return result;
                        yield break;
                    }
                }

                if (policy == BoardSearchPolicy.ResourceExchange && p.Has(LacrimaFusion)
                    && p.Extra.Contains(Caesar) && !p.Twins)
                {
                    // Pay a spent Link-1 to bring back the other level-six material.
                    // Leave live Red and Sunny in the graveyard.
                    var reviveForCaesar = bodyMoves.Where(next => next.Move.Card == Engraver
                        && (next.Move.Target == Requiem || next.Move.Target == Deal)).ToList();
                    if (reviveForCaesar.Count != 0)
                    {
                        foreach (var result in reviveForCaesar) yield return result;
                        yield break;
                    }
                }
                if ((policy == BoardSearchPolicy.DevelopEngines || policy == BoardSearchPolicy.ResourceExchange)
                    && p.Has(Sequence) && p.Fresh(PlanEffect.Fusion))
                {
                    // Turn an armed Photo into a live Link-4 before using it as material.
                    var photo = bodyMoves.Where(next => next.Move.Card == NightPhoto
                        && next.Move.Effect == 1 && next.Move.Target == Nightfall).ToList();
                    if (photo.Count != 0)
                    {
                        foreach (var result in photo) yield return result;
                        yield break;
                    }
                    // Keep the fusion choice open until a live level-six partner is available.
                    if (p.Has(Nightfall) && p.Extra.Contains(Caesar) && p.Grave.Contains(Engraver))
                    {
                        var fusions = FusionMoves(p).Where(next => next.Move.Fusion == LacrimaFusion).ToList();
                        if (fusions.Count != 0)
                        {
                            foreach (var result in fusions) yield return result;
                            yield break;
                        }
                    }
                }
                foreach (var result in bodyMoves.Where(next => next.Move.Card != Engraver)) yield return result;
                foreach (var result in RotationMoves(p)) yield return result;
                foreach (var result in FusionMoves(p)) yield return result;
                foreach (var result in ExtraMoves(p)) yield return result;
                // Keep the revival and its graveyard cost available while existing bodies can develop the board.
                foreach (var result in bodyMoves.Where(next => next.Move.Card == Engraver)) yield return result;
            }
            foreach (var result in SetMoves(p)) yield return result;
        }
    }
}
