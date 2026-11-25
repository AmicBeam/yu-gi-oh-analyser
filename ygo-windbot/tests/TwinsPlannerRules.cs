using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using WindBot.Game;
using WindBot.Game.AI;
using WindBot.Game.AI.Decks;
using YGOSharp.OCGWrapper;
using YGOSharp.OCGWrapper.Enums;

internal static class TwinsPlannerRules
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private const int Gryphon = 65330383;
    private const int Desirae = 82135803;
    private const int Abao = 4731783;
    private const int Heist = 25153;

    private static void Check(bool actual, string label)
    {
        if (!actual)
            throw new InvalidOperationException(label);
    }

    public static void Main(string[] args)
    {
        NamedCardsManager.Init(args[0]);
        var duel = new Duel();
        duel.Fields[0].Init(0, 0, 0);
        duel.Fields[0].SetInitialDeck(new NamedCard[0]);
        duel.Fields[1].Init(0, 0, 1);
        var executor = new TwinsExecutor(null, duel);
        var type = typeof(TwinsExecutor);
        var activate = type.GetMethod("CanActivateOnField", Private);
        var arrows = type.GetMethod("Arrows", Private);
        Func<int[], int, int, bool> canActivate = (field, zone, normal) =>
            (bool)activate.Invoke(executor, new object[] { field, zone, normal });

        int[] lone = { 0, 0, Gryphon, 0, 0, 0, 0 };
        Check(!canActivate(lone, 2, 0), "Gryphon cannot activate while its own arrows point at empty zones");
        int[] oneWay = { 0, Desirae, Gryphon, 0, 0, 0, 0 };
        Check(canActivate(oneWay, 1, 0) && canActivate(oneWay, 2, 0), "both ends of a one-way link");
        int[] unlinked = { Desirae, 0, Gryphon, 0, 0, 0, 0 };
        Check(!canActivate(unlinked, 0, 0) && canActivate(unlinked, 0, 1), "Gryphon only locks special summons");
        int[] outgoing = { 0, Desirae, Gryphon, 0, 0, Heist, 0 };
        Check(canActivate(outgoing, 5, 0), "a Link's outgoing arrow can enable its effect");
        Check((int)arrows.Invoke(executor, new object[] { Gryphon, 5 }) == ((1 << 1) | (1 << 19)),
            "left Extra Monster Zone has one arrow to each player");

        var positionType = type.GetNestedType("PlanPosition", BindingFlags.NonPublic);
        object position = Activator.CreateInstance(positionType, true);
        foreach (string name in new[] { "Hand", "Deck", "Grave", "Extra" })
            positionType.GetField(name).SetValue(position, new List<int>());
        var score = type.GetMethod("EndBoardScore", Private);
        Func<int[], int> terminal = field => {
            positionType.GetField("Field").SetValue(position, field);
            return (int)score.Invoke(executor, new object[] { position, true });
        };
        Check(terminal(lone) == 20, "Gryphon with no opponent arrows scores 2");
        int[] loneAbao = { Abao, 0, 0, 0, 0, 0, 0 };
        Check(terminal(loneAbao) == 0, "Abao cannot destroy without a discard");
        positionType.GetField("Hand").SetValue(position, new List<int> { 23434538 });
        Check(terminal(loneAbao) == 10, "Abao earns one interaction point with a hand trap as its only discard");
        positionType.GetField("Hand").SetValue(position, new List<int>());
        Check(terminal(new[] { 0, 0, Gryphon, 0, 0, Abao, 0 }) == 23, "unopened occupied Extra Monster Zone");
        Check(terminal(new[] { 0, 0, 0, 0, 0, Gryphon, 0 }) == 0, "arrow to opponent turns off lock award");
        positionType.GetField("Equipment").SetValue(position, 2);
        Check(terminal(oneWay) == 30 && terminal(unlinked) == 20, "locked Desirae provides no interruption");
        int[] linkedExtra = { Abao, Gryphon, 0, 0, 0, Desirae, 0 };
        int[] lockedExtra = { 0, 0, Gryphon, 0, 0, Desirae, 0 };
        Check(terminal(linkedExtra) == 33 && terminal(lockedExtra) == 23,
            "Desirae in the Extra Monster Zone counts only if linked under Gryphon");
        const int caesar = 79559912;
        Check(terminal(new[] { Abao, Gryphon, 0, 0, 0, caesar, 0 }) == 36,
            "Caesar in the Extra Monster Zone keeps its interruption when linked");
        const int nightfall = 25154, deal = 6636319, scout = 25152;
        int[] deferredLock = { 0, Gryphon, 0, 0, 0, nightfall, 0 };
        positionType.GetField("Grave").SetValue(position, new List<int> { scout });
        Check(terminal(deferredLock) == 0, "Nightfall in the Extra Monster Zone opens the opponent's arrow");
        positionType.GetField("Extra").SetValue(position, new List<int> { deal });
        Check(terminal(deferredLock) == 23, "Nightfall can replace itself with Deal to close the arrow");
        positionType.GetField("Extra").SetValue(position, new List<int>());
        positionType.GetField("Grave").SetValue(position, new List<int> { Gryphon });
        positionType.GetField("Hand").SetValue(position, new List<int> { 27204311 });
        Check(terminal(new[] { Abao, 0, 0, 0, 0, 0, 0 }) == 20,
            "Gryphon revived by Abao scores its normal locked board");
        positionType.GetField("Hand").SetValue(position, new List<int> { 37582948 });
        Check(terminal(new[] { Abao, 0, 0, 0, 0, 0, 0 }) == 20,
            "Abao cannot count both its destroy and revival branches");
        positionType.GetField("Set").SetValue(position, 3);
        positionType.GetField("Extra").SetValue(position, new List<int> { deal });
        positionType.GetField("Grave").SetValue(position, new List<int> { Gryphon, scout, 9205573, 36609518 });
        int[] newYearBoard = { Abao, 0, 0, 0, 0, 0, nightfall };
        Check(terminal(newYearBoard) == 47, "Deal and Challenge can enable Blue alongside the revived Gryphon");
        ((List<int>)positionType.GetField("Grave").GetValue(position)).Add(25150);
        Check(terminal(newYearBoard) == 51,
            "Challenge and New Year add Red's draw alongside the revived Gryphon");
        positionType.GetField("Set").SetValue(position, 1);
        positionType.GetField("Extra").SetValue(position, new List<int>());
        positionType.GetField("Grave").SetValue(position, new List<int> { Heist });
        int[] heistAndTrade = { Abao, Gryphon, 36609518, 0, 0, Desirae, 0 };
        positionType.GetField("Hand").SetValue(position, new List<int> { 27204311 });
        Check(terminal(heistAndTrade) == 63,
            "Abao can revive linked Heist and Disconnect can trade Desirae");
        positionType.GetField("Hand").SetValue(position, new List<int> { 37582948 });
        Check(terminal(heistAndTrade) == 63, "Abao's Heist revival replaces its destruction point");
        positionType.GetField("Set").SetValue(position, 0);
        positionType.GetField("Extra").SetValue(position, new List<int>());
        const int blue = 36609518;
        const int frost = 54257392, photo = 25151;
        positionType.GetField("Grave").SetValue(position, new List<int> { frost });
        Check(terminal(new[] { blue, 0, 0, 0, 0, 0, 0 }) == 2 && terminal(new int[7]) == 0,
            "Frost's conditional graveyard draw scores half a draw when an Evil Twin is on the field");
        positionType.GetField("Field").SetValue(position, new[] { 0, 0, 0, 0, 0, Abao, 0 });
        positionType.GetField("Grave").SetValue(position, new List<int> { blue });
        positionType.GetField("Hand").SetValue(position, new List<int> { 27204311 });
        var moves = ((IEnumerable)type.GetMethod("NextPositions", Private).Invoke(executor, new[] { position, Enum.ToObject(type.GetNestedType("BoardSearchPolicy", BindingFlags.NonPublic), 0) })).Cast<object>();
        var revivals = moves.Where(next => ((int[])positionType.GetField("Field").GetValue(next)).Contains(blue)).ToList();
        Check(revivals.Count != 0 && revivals.All(next => !((List<int>)positionType.GetField("Grave").GetValue(next)).Contains(blue)),
            "a revived monster leaves the projected graveyard");
        const int sunny = 93672138, smallRed = 36326160, smallBlue = 73810864;
        positionType.GetField("Field").SetValue(position, new[] { 0, 0, 0, 0, 0, sunny, 0 });
        positionType.GetField("Grave").SetValue(position, new List<int> { smallRed, smallBlue });
        var effects = type.GetNestedType("PlanEffect", BindingFlags.NonPublic);
        ulong starters = (1UL << (int)Enum.Parse(effects, "SmallRed")) | (1UL << (int)Enum.Parse(effects, "SmallBlue"));
        var bothTwins = ((IEnumerable)type.GetMethod("RotationMoves", Private).Invoke(executor, new[] { position })).Cast<object>().ToList();
        Check(bothTwins.Count != 0 && bothTwins.All(next =>
                ((ulong)positionType.GetField("Pending").GetValue(next) & starters) == 0),
            "Sunny's simultaneous revival does not trigger either small Twin's empty-field effect");

        var ready = type.GetMethod("CanPlanBoard", Private);
        duel.Turn = 2;
        duel.Player = 0;
        duel.MainPhase = new MainPhase { CanBattlePhase = true };
        duel.Phase = DuelPhase.Main1;
        Check(!(bool)ready.Invoke(executor, null), "combat policy owns a Main 1 with a battle phase");
        const int password = 61976639;
        duel.CurrentChainInfo.Add(new ChainInfo(new ClientCard(password, CardLocation.SpellZone, 0, 0), 0, 0));
        type.GetMethod("RecordBoardEffects", Private).Invoke(executor, null);
        duel.CurrentChainInfo.Clear();
        duel.Phase = DuelPhase.Main2;
        Check((bool)ready.Invoke(executor, null), "the same board policy takes over a cleared Main 2");
        var live = type.GetMethod("LivePlanPosition", Private).Invoke(executor, null);
        Check((ulong)positionType.GetField("Used").GetValue(live) != 0, "Main 2 carries over Main 1 effect use");
        duel.Fields[1].MonsterZone[0] = new ClientCard(14558128, CardLocation.MonsterZone, 0, 1);
        Check(!(bool)ready.Invoke(executor, null), "the board policy defers while the opposing board remains");

        var targetHint = type.GetMethod("PlanTargetHint", BindingFlags.Static | BindingFlags.NonPublic);
        Check((bool)targetHint.Invoke(null, new object[] { Gryphon, HintMsg.Set }),
            "Gryphon accepts an engine set selection");
        var scoreKey = type.GetMethod("BoardScoreKey", BindingFlags.Static | BindingFlags.NonPublic);
        var keyBeforeDraw = scoreKey.Invoke(null, new[] { position });
        positionType.GetField("Draws").SetValue(position, 1);
        Check(Equals(scoreKey.Invoke(null, new[] { position }), keyBeforeDraw),
            "the board cache leaves out the additive draw score");
        positionType.GetField("Hand").SetValue(position, new List<int> { 27204311 });
        var payableAbaoKey = scoreKey.Invoke(null, new[] { position });
        positionType.GetField("Hand").SetValue(position, new List<int> { 37582948 });
        Check(Equals(scoreKey.Invoke(null, new[] { position }), payableAbaoKey),
            "the board cache gives a hand trap and a spare card the same Abao value");
        positionType.GetField("Hand").SetValue(position, new List<int>());
        Check(!Equals(scoreKey.Invoke(null, new[] { position }), payableAbaoKey),
            "the board cache still distinguishes an empty hand for Abao");
        var tableKey = type.GetMethod("TableKey", BindingFlags.Static | BindingFlags.NonPublic);
        int ash = new ClientCard(14558128, CardLocation.Hand, 0, 0).GetOriginCode();
        positionType.GetField("Hand").SetValue(position, new List<int> { ash });
        var inertKey = tableKey.Invoke(null, new object[] { position, false });
        positionType.GetField("Hand").SetValue(position, new List<int> { 27204311 });
        Check(Equals(tableKey.Invoke(null, new object[] { position, false }), inertKey),
            "table keys share spare hand traps");
        int imperm = new ClientCard(10045474, CardLocation.Hand, 0, 0).GetOriginCode();
        positionType.GetField("Hand").SetValue(position, new List<int> { imperm });
        Check(!Equals(tableKey.Invoke(null, new object[] { position, false }), inertKey),
            "a settable hand trap stays distinct in a table key");
        Check(!Equals(tableKey.Invoke(null, new object[] { position, true }),
                tableKey.Invoke(null, new object[] { position, false })),
            "the Ash recovery table uses a separate key");
        var extraZones = type.GetMethod("ExtraDeckZoneMask", Private);
        positionType.GetField("Field").SetValue(position, new int[7]);
        Check((int)extraZones.Invoke(executor, new[] { position, (object)false }) == 127,
            "fusion and Xyz can use a free Extra Monster Zone");
        positionType.GetField("Field").SetValue(position, new[] { 0, 0, 0, 0, 0, Desirae, 0 });
        Check(((int)extraZones.Invoke(executor, new[] { position, (object)false }) & (3 << 5)) == 0,
            "one occupied Extra Monster Zone prevents using the other without an Extra Link");
        duel.IsNewRule = duel.IsNewRule2020 = true;
        var challengeZones = type.GetMethod("ChallengeDesiraeZones", Private);
        var faceUp = (int)CardPosition.FaceUpAttack;
        var extraDesirae = new ClientCard(Desirae, CardLocation.MonsterZone, 5, faceUp);
        var liveField = duel.Fields[0].MonsterZone;
        liveField[5] = extraDesirae;
        liveField[1] = new ClientCard(Gryphon, CardLocation.MonsterZone, 1, faceUp);
        Check(((int)challengeZones.Invoke(executor, new object[] { extraDesirae, null, Heist }) & (1 << 5)) != 0,
            "Challenge can use the Extra Monster Zone vacated by Desirae while linked to Gryphon");
        liveField[1] = null;
        liveField[2] = new ClientCard(Gryphon, CardLocation.MonsterZone, 2, faceUp);
        Check(((int)challengeZones.Invoke(executor, new object[] { extraDesirae, null, Heist }) & (1 << 5)) == 0,
            "Challenge avoids an Extra Monster Zone where Gryphon would block Heist");
        var heistFollowup = type.GetMethod("CanFollowBlueWithHeist", Private);
        Check((bool)heistFollowup.Invoke(executor, new object[] { lone, 0 }),
            "linked Red and Blue can resolve before Challenge replaces Blue with Heist");
        Check(!(bool)heistFollowup.Invoke(executor, new object[] { new[] { 0, 0, 0, 0, 0, Gryphon, 0 }, 0 }),
            "the follow-up needs linked spaces for both twins");
        var newYearPlace = type.GetMethod("NewYearTwinCyclePlace", Private);
        const int bigRed = 9205573;
        Check((int)newYearPlace.Invoke(executor, new object[] { new[] { blue, nightfall, Gryphon, bigRed, 0, 0, 0 }, 0 }) == 1,
            "New Year releases Blue and leaves a usable revival zone under Gryphon");
        Check((int)newYearPlace.Invoke(executor, new object[] { new[] { 0, blue, Gryphon, bigRed, nightfall, 0, 0 }, 1 }) == 0,
            "New Year must leave Blue a linked place to return");
        var preferEnd = type.GetMethod("PreferEnd", BindingFlags.Static | BindingFlags.NonPublic);
        var nextPosition = positionType.GetMethod("Next");
        object early = nextPosition.Invoke(position, new object[] { null });
        object late = nextPosition.Invoke(position, new object[] { null });
        var revivalMove = positionType.GetField("EngraverRevivalMove");
        revivalMove.SetValue(early, 2);
        revivalMove.SetValue(late, 6);
        Check((bool)preferEnd.Invoke(null, new[] { late, early })
            && !(bool)preferEnd.Invoke(null, new[] { early, late }),
            "equal end boards prefer spending Engraver's graveyard revival later");
        Check(Equals(positionType.GetField("ActionCount").GetValue(position),
            positionType.GetField("ActionCount").GetValue(early)),
            "skipping an optional trigger does not delay Engraver by an action");
        positionType.GetField("LinkTwoForLinkOne").SetValue(late, 1);
        Check((bool)preferEnd.Invoke(null, new[] { early, late })
            && !(bool)preferEnd.Invoke(null, new[] { late, early }),
            "equal end boards preserve Link-2 material");
        var searched = nextPosition.Invoke(early, new object[] { null });
        positionType.GetField("Used").SetValue(searched, 1UL << (int)Enum.Parse(effects, "Password"));
        positionType.GetField("FirstTwinSearchMove").SetValue(searched, 1);
        Check((bool)preferEnd.Invoke(null, new[] { early, searched }) &&
            !(bool)preferEnd.Invoke(null, new[] { searched, early }), "equal boards prefer fewer Twin searches");
        var searchedLater = nextPosition.Invoke(searched, new object[] { null });
        positionType.GetField("FirstTwinSearchMove").SetValue(searchedLater, 4);
        Check((bool)preferEnd.Invoke(null, new[] { searchedLater, searched }),
            "when both lines need the same search, develop the board before spending it");
        positionType.GetField("LinkTwoForLinkOne").SetValue(searchedLater, 1);
        Check((bool)preferEnd.Invoke(null, new[] { searched, searchedLater }),
            "keeping Link-2 as material takes precedence over delaying a search");
        var firstSweet = positionType.GetField("FirstSweetSendMove");
        var firstNewYear = positionType.GetField("FirstNewYearSendWithoutSearch");
        var sweetFirst = nextPosition.Invoke(early, new object[] { null });
        var newYearFirst = nextPosition.Invoke(early, new object[] { null });
        firstSweet.SetValue(sweetFirst, 2);
        firstSweet.SetValue(newYearFirst, 4);
        firstNewYear.SetValue(sweetFirst, 4);
        firstNewYear.SetValue(newYearFirst, 2);
        Check((bool)preferEnd.Invoke(null, new[] { sweetFirst, newYearFirst }),
            "the earlier Sweet send takes precedence over the earlier New Year send");
        firstSweet.SetValue(newYearFirst, 2);
        Check((bool)preferEnd.Invoke(null, new[] { newYearFirst, sweetFirst }),
            "with Sweet sent at the same time, prefer the earlier eligible New Year send");
        var compareReserve = type.GetMethod("CompareNightfallReserve", BindingFlags.Static | BindingFlags.NonPublic);
        positionType.GetField("Field").SetValue(early, new[] { nightfall, 0, 0, 0, 0, 0, 0 });
        positionType.GetField("Field").SetValue(late, new[] { nightfall, 0, 0, 0, 0, 0, 0 });
        positionType.GetField("Grave").SetValue(early, new List<int> { scout });
        positionType.GetField("Grave").SetValue(late, new List<int>());
        Check((int)compareReserve.Invoke(null, new[] { early, late }) > 0,
            "Nightfall prefers keeping Scout in the grave as its cost");
        positionType.GetField("Grave").SetValue(late, new List<int> { scout });
        positionType.GetField("Extra").SetValue(early, new List<int> { Heist });
        positionType.GetField("Extra").SetValue(late, new List<int>());
        Check((int)compareReserve.Invoke(null, new[] { early, late }) > 0,
            "with the same Nightfall cost, keep Heist available in the Extra Deck");
        const int requiem = 2463794;
        positionType.GetField("Field").SetValue(position, new[] { 0, 0, 0, 0, 0, deal, 0 });
        positionType.GetField("Extra").SetValue(position, new List<int> { requiem });
        var ownSummons = positionType.GetField("OwnSummons");
        var extraMoves = type.GetMethod("ExtraMoves", Private);
        var earlyRequiem = positionType.GetField("EarlyRequiem");
        var hasDefense = type.GetMethod("HasNibiruDefense", BindingFlags.Static | BindingFlags.NonPublic);
        const int nibiruNewYear = 25150;
        positionType.GetField("Hand").SetValue(position, new List<int>());
        positionType.GetField("Grave").SetValue(position, new List<int>());
        ownSummons.SetValue(position, 4);
        var requiemAlone = ((IEnumerable)extraMoves.Invoke(executor, new[] { position, requiem })).Cast<object>().First();
        Check((bool)earlyRequiem.GetValue(requiemAlone) && !(bool)hasDefense.Invoke(null, new[] { requiemAlone }),
            "Requiem alone earns no defense point");
        var reserveWithoutTribute = nextPosition.Invoke(position, new object[] { null });
        earlyRequiem.SetValue(reserveWithoutTribute, true);
        positionType.GetField("Field").SetValue(reserveWithoutTribute, new int[7]);
        positionType.GetField("Hand").SetValue(reserveWithoutTribute, new List<int> { nibiruNewYear });
        Check(!(bool)hasDefense.Invoke(null, new[] { reserveWithoutTribute }),
            "a held New Year without a tribute cannot qualify");
        positionType.GetField("Hand").SetValue(position, new List<int> { nibiruNewYear });
        var fifth = ((IEnumerable)extraMoves.Invoke(executor, new[] { position, requiem })).Cast<object>().First();
        var cachedDefense = type.GetMethod("TableHasNibiruDefense", BindingFlags.Static | BindingFlags.NonPublic);
        var cachedSummon = positionType.GetField("Move").GetValue(fifth);
        var cachedMoves = Array.CreateInstance(cachedSummon.GetType(), 1);
        cachedMoves.SetValue(cachedSummon, 0);
        Check((bool)cachedDefense.Invoke(null, new object[] { position, cachedMoves }),
            "cached early Requiem recognizes the held New Year");
        positionType.GetField("Hand").SetValue(position, new List<int>());
        Check(!(bool)cachedDefense.Invoke(null, new object[] { position, cachedMoves }),
            "cached early Requiem does not carry the former unconditional point");
        positionType.GetField("Grave").SetValue(position, new List<int> { nibiruNewYear });
        var graveNewYear = ((IEnumerable)extraMoves.Invoke(executor, new[] { position, requiem })).Cast<object>().First();
        Check((bool)hasDefense.Invoke(null, new[] { graveNewYear }),
            "an unused New Year can qualify from the graveyard");
        var spentNewYear = nextPosition.Invoke(position, new object[] { null });
        positionType.GetField("Used").SetValue(spentNewYear, 1UL << (int)Enum.Parse(effects, "NewYearJump"));
        var spentFifth = ((IEnumerable)extraMoves.Invoke(executor, new[] { spentNewYear, requiem })).Cast<object>().First();
        Check(!(bool)hasDefense.Invoke(null, new[] { spentFifth }), "a spent New Year cannot qualify");
        positionType.GetField("Hand").SetValue(requiemAlone, new List<int> { nibiruNewYear });
        Check(!(bool)hasDefense.Invoke(null, new[] { requiemAlone }),
            "New Year acquired after the fifth summon does not qualify");
        ownSummons.SetValue(position, 5);
        var sixth = ((IEnumerable)extraMoves.Invoke(executor, new[] { position, requiem })).Cast<object>().First();
        Check((bool)earlyRequiem.GetValue(fifth) && !(bool)earlyRequiem.GetValue(sixth)
            && (int)score.Invoke(executor, new[] { fifth, (object)true })
                == (int)score.Invoke(executor, new[] { sixth, (object)true }) + 10,
            "Requiem and live New Year earn one point through the fifth summon");
        Check(!(bool)hasDefense.Invoke(null, new[] { requiemAlone })
            && !(bool)hasDefense.Invoke(null, new[] { sixth })
            && (int)score.Invoke(executor, new[] { requiemAlone, (object)true })
                == (int)score.Invoke(executor, new[] { sixth, (object)true })
            && (bool)preferEnd.Invoke(null, new[] { requiemAlone, sixth })
            && !(bool)preferEnd.Invoke(null, new[] { sixth, requiemAlone }),
            "with equal scores and neither defense live, prefer Requiem by the fifth summon");
        var bigRedBeforeRequiem = nextPosition.Invoke(position, new object[] { null });
        positionType.GetField("Field").SetValue(bigRedBeforeRequiem, new[] { 0, 0, 0, 0, 0, bigRed, 0 });
        var bigRedIntoRequiem = ((IEnumerable)extraMoves.Invoke(executor, new[] { bigRedBeforeRequiem, (object)requiem }))
            .Cast<object>().First();
        Check((int)positionType.GetField("LinkTwoForLinkOne").GetValue(fifth) == 0
            && (int)positionType.GetField("LinkTwoForLinkOne").GetValue(bigRedIntoRequiem) == 1,
            "Requiem follows the same Link-2-to-Link-1 cost rule as other Link-1 monsters");
        const int trouble = 37582948;
        positionType.GetField("Deck").SetValue(position, new List<int> { photo, trouble });
        positionType.GetField("Hand").SetValue(position, new List<int> { password });
        ownSummons.SetValue(position, 4);
        var fieldProperty = positionType.GetField("Field");
        fieldProperty.SetValue(position, new int[7]);
        positionType.GetField("Grave").SetValue(position, new List<int>());
        Check(!(bool)hasDefense.Invoke(null, new[] { position }),
            "searching Photo is insufficient without a legal graveyard Twin");
        positionType.GetField("Grave").SetValue(position, new List<int> { scout });
        Check(!(bool)hasDefense.Invoke(null, new[] { position }),
            "Photo cannot return the only Twin it could revive");
        positionType.GetField("Grave").SetValue(position, new List<int> { smallRed, scout });
        Check((bool)hasDefense.Invoke(null, new[] { position }), "Password can still search Photo via Trouble");
        var fifthBlue = nextPosition.Invoke(position, new object[] { null });
        ownSummons.SetValue(fifthBlue, 5);
        positionType.GetField("Grave").SetValue(fifthBlue, new List<int> { scout });
        fieldProperty.SetValue(fifthBlue, new[] { smallRed, 0, nibiruNewYear, 0, 0, deal, 0 });
        positionType.GetField("NewYearZone").SetValue(fifthBlue, 2);
        var captureDefense = type.GetMethod("CaptureNibiruWindow", BindingFlags.Static | BindingFlags.NonPublic);
        captureDefense.Invoke(null, new object[] { fifthBlue, false });
        Check((bool)hasDefense.Invoke(null, new[] { fifthBlue })
            && ((List<int>)positionType.GetField("Grave").GetValue(fifthBlue)).SequenceEqual(new[] { scout })
            && ((int[])fieldProperty.GetValue(fifthBlue))[5] == deal,
            "the fifth-summon defense snapshot credits Photo after Nibiru tributes the field without changing the live position");
        positionType.GetField("Grave").SetValue(fifthBlue, new List<int>());
        fieldProperty.SetValue(fifthBlue, new[] { 0, 0, nibiruNewYear, 0, 0, deal, 0 });
        captureDefense.Invoke(null, new object[] { fifthBlue, false });
        Check(!(bool)hasDefense.Invoke(null, new[] { fifthBlue }),
            "a grave-summoned New Year is banished and cannot be Photo's second graveyard card");
        positionType.GetField("SearchLocked").SetValue(position, true);
        Check(!(bool)hasDefense.Invoke(null, new[] { position }), "a locked search cannot protect the route");
        positionType.GetField("Hand").SetValue(position, new List<int> { photo });
        Check(!(bool)hasDefense.Invoke(null, new[] { position }), "Photo still needs its revival cost");
        ((List<int>)positionType.GetField("Grave").GetValue(position)).Add(password);
        Check((bool)hasDefense.Invoke(null, new[] { position }), "Photo in hand with both effects live counts");
        ownSummons.SetValue(position, 6);
        Check(!(bool)hasDefense.Invoke(null, new[] { position }), "Photo drawn after the window is too late");
        positionType.GetField("PhotoAtNibiruWindow").SetValue(fifth, true);
        Check((int)score.Invoke(executor, new[] { fifth, (object)true })
            == (int)score.Invoke(executor, new[] { sixth, (object)true }) + 10,
            "Photo and early Requiem share one defense point");
        var compareDefense = type.GetMethod("CompareNibiruDefense", BindingFlags.Static | BindingFlags.NonPublic);
        var earlyYama = nextPosition.Invoke(fifth, new object[] { null });
        var lateYama = nextPosition.Invoke(fifth, new object[] { null });
        positionType.GetField("FirstYamaGraveMove").SetValue(earlyYama, 4);
        positionType.GetField("FirstYamaGraveMove").SetValue(lateYama, 7);
        positionType.GetField("FirstSweetSendMove").SetValue(lateYama, 1);
        Check((bool)preferEnd.Invoke(null, new[] { earlyYama, lateYama }),
            "with the same defense, sending Yama early precedes the earliest Sweet send");
        positionType.GetField("PhotoAtNibiruWindow").SetValue(earlyYama, false);
        Check((bool)preferEnd.Invoke(null, new[] { lateYama, earlyYama }),
            "the preferred defense precedes sending Yama early");
        var earlyCaesar = nextPosition.Invoke(fifth, new object[] { null });
        var lateCaesar = nextPosition.Invoke(fifth, new object[] { null });
        var caesarTiming = positionType.GetField("FirstCaesarSummonMove");
        positionType.GetField("Field").SetValue(earlyCaesar, new[] { caesar, 0, 0, 0, 0, 0, 0 });
        positionType.GetField("Field").SetValue(lateCaesar, new[] { caesar, 0, 0, 0, 0, 0, 0 });
        caesarTiming.SetValue(earlyCaesar, 12);
        caesarTiming.SetValue(lateCaesar, 15);
        positionType.GetField("FirstYamaGraveMove").SetValue(earlyCaesar, 7);
        positionType.GetField("FirstYamaGraveMove").SetValue(lateCaesar, 4);
        Check((bool)preferEnd.Invoke(null, new[] { earlyCaesar, lateCaesar }),
            "equal Caesar boards prefer the earlier Caesar over the earlier Yama send");
        positionType.GetField("PhotoAtNibiruWindow").SetValue(earlyCaesar, false);
        Check((bool)preferEnd.Invoke(null, new[] { lateCaesar, earlyCaesar }),
            "the preferred Nibiru defense takes precedence over the earlier Caesar");
        positionType.GetField("PhotoAtNibiruWindow").SetValue(earlyCaesar, true);
        positionType.GetField("Field").SetValue(earlyCaesar, new[] { Desirae, 0, 0, 0, 0, 0, 0 });
        Check((bool)preferEnd.Invoke(null, new[] { lateCaesar, earlyCaesar }),
            "Caesar timing only breaks ties between plans that both end with Caesar");
        var caesarSetup = Activator.CreateInstance(positionType, true);
        positionType.GetField("Field").SetValue(caesarSetup, new[] { 60764609, 46640168, Desirae, 0, 0, 0, 0 });
        positionType.GetField("Hand").SetValue(caesarSetup, new List<int>());
        positionType.GetField("Deck").SetValue(caesarSetup, new List<int>());
        positionType.GetField("Grave").SetValue(caesarSetup, new List<int> { requiem });
        positionType.GetField("Extra").SetValue(caesarSetup, new List<int> { caesar });
        var referencePolicy = Enum.Parse(type.GetNestedType("BoardSearchPolicy", BindingFlags.NonPublic), "Reference");
        var caesarPlan = type.GetMethod("FindBoard", Private).Invoke(executor,
            new object[] { caesarSetup, null, 24, 500, null, 0, referencePolicy });
        var caesarMoves = (IEnumerable)caesarPlan.GetType().GetField("Moves").GetValue(caesarPlan);
        var caesarCards = caesarMoves.Cast<object>().Select(move => (int)move.GetType().GetField("Card").GetValue(move));
        Check(caesarCards.SequenceEqual(new[] { caesar, requiem }),
            "the planner summons Caesar before equipping Desirae when both orders have the same end board");
        var earlyOnly = nextPosition.Invoke(fifth, new object[] { null });
        positionType.GetField("PhotoAtNibiruWindow").SetValue(sixth, true);
        positionType.GetField("PhotoAtNibiruWindow").SetValue(requiemAlone, true);
        Check((int)compareDefense.Invoke(null, new[] { requiemAlone, sixth }) > 0,
            "among Photo defenses, prefer an early Requiem even without a live New Year");
        positionType.GetField("PhotoAtNibiruWindow").SetValue(earlyOnly, false);
        positionType.GetField("Hand").SetValue(earlyOnly, new List<int> { 27204311, password });
        positionType.GetField("Hand").SetValue(sixth, new List<int> { 27204311 });
        Check((int)compareDefense.Invoke(null, new[] { fifth, sixth }) > 0
            && (int)compareDefense.Invoke(null, new[] { sixth, earlyOnly }) > 0,
            "among defended boards, prefer both routes, then Photo alone, then early Requiem alone");
        positionType.GetField("Field").SetValue(position, new int[7]);
        type.GetMethod("Put", Private).Invoke(executor, new object[] { position, Gryphon, 1 });
        var gryphonSet = 1UL << (int)Enum.Parse(effects, "GryphonSet");
        Check(((ulong)positionType.GetField("Pending").GetValue(position) & gryphonSet) == 0,
            "reviving Gryphon does not trigger its Link Summon effect");

        // Replay 03:58:24. Veiler stopped Blue, then Droll made the drawn Password unusable.
        const int engraver = 60764609, sequence = 49867899, lacrimaFusion = 46640168;
        var lockedOpening = Activator.CreateInstance(positionType, true);
        positionType.GetField("Field").SetValue(lockedOpening, new[] { 0, 0, 0, 0, engraver, blue, 0 });
        positionType.GetField("Hand").SetValue(lockedOpening, new List<int> { password, 14558127, 97268401 });
        positionType.GetField("Deck").SetValue(lockedOpening, new List<int> { trouble, 98360333, 25156 });
        positionType.GetField("Grave").SetValue(lockedOpening, new List<int> { smallRed, smallBlue, deal, requiem, 82699999 });
        positionType.GetField("Extra").SetValue(lockedOpening, new List<int> { sequence, Desirae, lacrimaFusion, caesar });
        positionType.GetField("Normal").SetValue(lockedOpening, true);
        positionType.GetField("Requiem").SetValue(lockedOpening, true);
        positionType.GetField("SearchLocked").SetValue(lockedOpening, true);
        positionType.GetField("Used").SetValue(lockedOpening, 1UL << (int)Enum.Parse(effects, "BlueRevive"));
        var searchPolicy = type.GetNestedType("BoardSearchPolicy", BindingFlags.NonPublic);
        var findBoard = type.GetMethod("FindBoard", Private);
        var continuation = findBoard.Invoke(executor, new[] { lockedOpening, null, (object)144, 24000, null, 0,
            Enum.Parse(searchPolicy, "HoldBackrow") });
        var actions = ((IEnumerable)continuation.GetType().GetField("Moves").GetValue(continuation)).Cast<object>().ToArray();
        Check(actions.All(move => (int)move.GetType().GetField("Card").GetValue(move) != password),
            "Droll excludes the unusable Password search from the plan");
        Check(actions.Length > 1 && actions.Any(move => (int)move.GetType().GetField("Card").GetValue(move) == sequence),
            "Droll cannot make an unavailable Password search hide the legal Sequence continuation");
        Check(actions.Any(move => (int)move.GetType().GetField("Fusion").GetValue(move) == lacrimaFusion),
            "Sequence can fuse Lacrima without adding cards to the hand");

        // Replay 18:00:41. A held search must stay available while the legal Blue bridge is built.
        var heldSearch = Activator.CreateInstance(positionType, true);
        positionType.GetField("Field").SetValue(heldSearch, new[] { nibiruNewYear, 0, engraver, 0, 82699999, 0, 0 });
        positionType.GetField("Hand").SetValue(heldSearch, new List<int> { trouble, password });
        positionType.GetField("Deck").SetValue(heldSearch, new List<int> { photo, trouble });
        positionType.GetField("Grave").SetValue(heldSearch, new List<int> { deal, scout, requiem });
        positionType.GetField("Extra").SetValue(heldSearch, new List<int> { blue });
        positionType.GetField("Requiem").SetValue(heldSearch, true);
        positionType.GetField("DealLinked").SetValue(heldSearch, true);
        positionType.GetField("Used").SetValue(heldSearch, 1UL << (int)Enum.Parse(effects, "Requiem"));
        var nextPositions = type.GetMethod("NextPositions", Private);
        Check(((IEnumerable)nextPositions.Invoke(executor, new[] { heldSearch, Enum.Parse(searchPolicy, "Tactical") }))
            .Cast<object>().Select(next => positionType.GetField("Move").GetValue(next))
            .Any(move => move != null && (int)move.GetType().GetField("Card").GetValue(move) == blue),
            "held Trouble and Password do not hide a legal Blue summon from a tactic");
        positionType.GetField("Extra").SetValue(heldSearch, new List<int> { blue, 24269961, Abao });
        positionType.GetField("Hand").SetValue(heldSearch, new List<int> { password, nibiruNewYear, nibiruNewYear, 27204311 });
        Check(((IEnumerable)nextPositions.Invoke(executor, new[] { heldSearch, Enum.Parse(searchPolicy, "Reference") }))
            .Cast<object>().Select(next => positionType.GetField("Move").GetValue(next))
            .Any(move => move != null && (int)move.GetType().GetField("Card").GetValue(move) == blue),
            "spare cards do not hide the Blue bridge when Password is available");

        var discardDuel = new Duel { Player = 0 };
        discardDuel.Fields[0].Init(0, 0, 0);
        var discardExecutor = new TwinsExecutor(null, discardDuel);
        var discardHand = discardDuel.Fields[0].Hand;
        discardHand.Add(new ClientCard(trouble, CardLocation.Hand, 0, 0));
        discardHand.Add(new ClientCard(smallBlue, CardLocation.Hand, 1, 0));
        discardHand.Add(new ClientCard(smallBlue, CardLocation.Hand, 2, 0));
        type.GetField("_planUsed", Private).SetValue(discardExecutor, 1UL << (int)Enum.Parse(effects, "Trouble"));
        var plannedDiscard = type.GetMethod("PlannedAbaoDiscard", Private);
        Check(ReferenceEquals(plannedDiscard.Invoke(discardExecutor, new object[] { discardHand, smallBlue }),
            discardHand[1]), "Abao keeps the planned discard when the first hand card only ties it");
        discardHand.Add(new ClientCard(42141493, CardLocation.Hand, 3, 0));
        Check(plannedDiscard.Invoke(discardExecutor, new object[] { discardHand, smallBlue }) == null,
            "Abao still prefers the higher-priority discard");
        discardDuel.Player = 1;
        var impermInHand = new ClientCard(10045474, CardLocation.Hand, 4, 0);
        discardHand.Add(impermInHand);
        Check(ReferenceEquals(plannedDiscard.Invoke(discardExecutor, new object[] { discardHand, 0 }), discardHand[3]),
            "Abao discards Fuwalos before Impermanence on the opponent turn");
        discardHand.RemoveAt(3);
        Check(ReferenceEquals(plannedDiscard.Invoke(discardExecutor, new object[] { discardHand, 0 }), impermInHand),
            "without Fuwalos, Abao discards Impermanence before spare Twin cards on the opponent turn");

        // Replay 17:45:58. Sunny has already returned the pair and Red has drawn.
        var afterSunny = Activator.CreateInstance(positionType, true);
        positionType.GetField("Field").SetValue(afterSunny, new[] { bigRed, sequence, 0, 0, blue, 0, 0 });
        positionType.GetField("Hand").SetValue(afterSunny, new List<int> { photo, frost, smallBlue, 23434538, 10045474 });
        positionType.GetField("Deck").SetValue(afterSunny, new List<int> { 98360333, 25156 });
        positionType.GetField("Grave").SetValue(afterSunny, new List<int> {
            smallRed, smallBlue, frost, scout, requiem, engraver, 93672138, trouble, password
        });
        positionType.GetField("Extra").SetValue(afterSunny, new List<int> { Desirae, Gryphon, nightfall, Heist });
        positionType.GetField("Used").SetValue(afterSunny, (1UL << (int)Enum.Parse(effects, "Sunny"))
            | (1UL << (int)Enum.Parse(effects, "RedDraw")));
        positionType.GetField("ReturningAbao").SetValue(afterSunny, 1);
        positionType.GetField("Draws").SetValue(afterSunny, 1);
        ownSummons.SetValue(afterSunny, 6);
        var tryTactic = type.GetMethod("TryTacticalContinuation", Private);
        var firstMoveType = tryTactic.GetParameters()[3].ParameterType;
        var legalMove = System.Linq.Expressions.Expression.Lambda(firstMoveType,
            System.Linq.Expressions.Expression.Constant(true),
            System.Linq.Expressions.Expression.Parameter(firstMoveType.GetGenericArguments()[0])).Compile();
        var redWithHeldBlues = Activator.CreateInstance(positionType, true);
        positionType.GetField("Field").SetValue(redWithHeldBlues, new[] { 0, smallBlue, smallRed, 0, 0, 0, 0 });
        positionType.GetField("Hand").SetValue(redWithHeldBlues, new List<int> { 10045474, smallBlue, 10045474, smallBlue });
        positionType.GetField("Deck").SetValue(redWithHeldBlues, new List<int> {
            smallRed, smallRed, frost, frost, frost, 81078880, 82699999, 25150, 25150, 25150,
            password, trouble, trouble, photo, engraver, 25156, 98360333
        });
        positionType.GetField("Grave").SetValue(redWithHeldBlues, new List<int>());
        var extraCards = (int[])type.GetField("PlanExtraCards", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null);
        positionType.GetField("Extra").SetValue(redWithHeldBlues, extraCards.ToList());
        positionType.GetField("Used").SetValue(redWithHeldBlues, 1UL << (int)Enum.Parse(effects, "SmallRed"));
        positionType.GetField("Normal").SetValue(redWithHeldBlues, true);
        positionType.GetField("NormalZones").SetValue(redWithHeldBlues, 4);
        ownSummons.SetValue(redWithHeldBlues, 2);
        var redPlan = tryTactic.Invoke(executor, new object[] {
            redWithHeldBlues, System.Diagnostics.Stopwatch.StartNew(), 2000, legalMove
        });
        Check(redPlan != null && (int)redPlan.GetType().GetField("Score").GetValue(redPlan) >= 107,
            "the starter tactics reach the full board with the remaining two Blue Twins in hand");
        var resumedPlan = tryTactic.Invoke(executor, new object[] {
            afterSunny, System.Diagnostics.Stopwatch.StartNew(), 2000, legalMove
        });
        Check(resumedPlan != null, "the post-Sunny position can start a tactic");
        var resumed = resumedPlan.GetType().GetField("Terminal").GetValue(resumedPlan);
        var resumedBoard = (int[])positionType.GetField("Field").GetValue(resumed);
        Check(resumedBoard.Skip(5).Contains(Desirae) && resumedBoard.Contains(Gryphon)
            && resumedBoard.Contains(nightfall) && ((int)positionType.GetField("Set").GetValue(resumed) & 3) == 3,
            "spent Sunny and Red draw still permit the Desirae, Gryphon and Nightfall finish");

        var negateDuel = new Duel { Player = 1, Phase = DuelPhase.Main1, LastChainPlayer = 1 };
        for (int player = 0; player < 2; player++)
            negateDuel.Fields[player].Init(0, 0, player);
        string dialogPath = Path.Combine(Path.GetDirectoryName(typeof(GameAI).Assembly.Location), "Dialogs", "default.json");
        var ai = new GameAI(new GameClient(new WindBot.WindBotInfo { Dialog = dialogPath }), negateDuel);
        ai.Executor = new TwinsExecutor(ai, negateDuel);
        var tamayori = new ClientCard(101307018, CardLocation.MonsterZone, 2, faceUp) { Controller = 1 };
        var susa = new ClientCard(17020474, CardLocation.MonsterZone, 5, faceUp) { Controller = 1 };
        typeof(ClientCard).GetProperty("Attack").SetValue(susa, susa.Data.Attack);
        negateDuel.Fields[1].MonsterZone[2] = tamayori;
        negateDuel.Fields[1].MonsterZone[5] = susa;
        negateDuel.CurrentChain.Add(tamayori);
        negateDuel.CurrentChainInfo.Add(new ChainInfo(tamayori, 1, 101307018 * 16));
        var defaultNegateTarget = type.GetMethod("DefaultGetDisableMonsterTarget", Private);
        var chosen = (ClientCard)defaultNegateTarget.Invoke(ai.Executor, null);
        Check(chosen == tamayori, "default negation selects the monster that activated its effect");
        var veiler = new ClientCard(97268402, CardLocation.Hand, 0, 0);
        ai.SelectCard(chosen);
        negateDuel.CurrentChain.Add(veiler);
        negateDuel.CurrentChainInfo.Add(new ChainInfo(veiler, 0, 97268402 * 16));
        Check(ai.OnSelectCard(new[] { susa, tamayori }, 1, 1, HintMsg.Disable, false).Single() == tamayori,
            "Veiler keeps the default target instead of selecting the higher-ATK monster");

        var photoCost = type.GetMethod("PhotoRevivalCost", BindingFlags.Static | BindingFlags.NonPublic);
        positionType.GetField("Grave").SetValue(position, new List<int> { password, trouble });
        Check((int)photoCost.Invoke(null, new[] { position }) == trouble,
            "Photo spends Trouble first to preserve the sole Password");
        var comparePreservation = type.GetMethod("CompareBoardPreservation", BindingFlags.Static | BindingFlags.NonPublic);
        var retained = nextPosition.Invoke(position, new object[] { null });
        positionType.GetField("Field").SetValue(retained, new[] { 0, Gryphon, lacrimaFusion, 0, 0, 0, 0 });
        positionType.GetField("Set").SetValue(retained, 0);
        var spentPassword = nextPosition.Invoke(retained, new object[] { null });
        positionType.GetField("PasswordBanishes").SetValue(spentPassword, 1);
        Check((int)comparePreservation.Invoke(null, new[] { retained, spentPassword }) > 0,
            "equal-score routes preserve Password");
        var additionalChallenge = nextPosition.Invoke(spentPassword, new object[] { null });
        positionType.GetField("Set").SetValue(additionalChallenge, (int)positionType.GetField("Set").GetValue(retained) | 2);
        Check((bool)preferEnd.Invoke(null, new[] { additionalChallenge, retained }),
            "an additional set Challenge takes priority over retaining Password at equal scores");
        var withoutLacrima = nextPosition.Invoke(retained, new object[] { null });
        positionType.GetField("Field").SetValue(withoutLacrima, new[] { 0, Gryphon, 0, 0, 0, 0, 0 });
        Check((int)comparePreservation.Invoke(null, new[] { retained, withoutLacrima }) > 0,
            "equal-score Gryphon boards prefer keeping Lacrima");
        var used = positionType.GetField("Used");
        used.SetValue(retained, 0UL);
        foreach (string effect in new[] { "Photo", "PhotoRevive" })
        {
            var spentPhoto = nextPosition.Invoke(retained, new object[] { null });
            used.SetValue(spentPhoto, 1UL << (int)Enum.Parse(effects, effect));
            Check((bool)preferEnd.Invoke(null, new[] { retained, spentPhoto }),
                "equal-score boards prefer keeping Photo when " + effect + " could be used");
        }

        positionType.GetField("Set").SetValue(position, 0);
        positionType.GetField("Equipment").SetValue(position, 0);
        positionType.GetField("Draws").SetValue(position, 0);
        positionType.GetField("Grave").SetValue(position, new List<int> { scout });
        positionType.GetField("Extra").SetValue(position, new List<int> { Heist });
        Check(terminal(deferredLock) == 20, "Heist summoned in the draw phase gives the lock but no disruption point");
        negateDuel.CurrentChain.Clear();
        negateDuel.CurrentChainInfo.Clear();
        negateDuel.Fields[1].MonsterZone[2] = null;
        negateDuel.Fields[1].MonsterZone[5] = null;
        negateDuel.Fields[0].MonsterZone[1] = new ClientCard(Gryphon, CardLocation.MonsterZone, 1, faceUp);
        var extraNightfall = new ClientCard(nightfall, CardLocation.MonsterZone, 5, faceUp);
        negateDuel.Fields[0].MonsterZone[5] = extraNightfall;
        negateDuel.Fields[0].ExtraDeck.Add(new ClientCard(deal, CardLocation.Extra, 0, 0));
        negateDuel.Fields[0].Graveyard.Add(new ClientCard(scout, CardLocation.Grave, 0, 0));
        negateDuel.Phase = DuelPhase.Draw;
        ai.Executor.SetCard(ExecutorType.Activate, extraNightfall, nightfall * 16 + 1);
        Check((bool)type.GetMethod("NightfallActivate", Private).Invoke(ai.Executor, null),
            "Nightfall rotates on an empty opponent field in the draw phase");

        var positions = new[] { CardPosition.FaceUpAttack, CardPosition.FaceUpDefence };
        negateDuel.Player = 1;
        negateDuel.Turn = 2;
        negateDuel.Phase = DuelPhase.Main1;
        Check(ai.OnSelectPosition(27204312, positions) == CardPosition.FaceUpDefence,
            "Nibiru gives the opponent a face-up Defense Position Token");
        Check(ai.OnSelectPosition(27204311, positions) == CardPosition.FaceUpAttack,
            "Nibiru itself retains its normal position selection");

        negateDuel.Player = 0;
        negateDuel.Turn = 1;
        negateDuel.Phase = DuelPhase.Main1;
        Check(ai.Executor.OnSelectPosition(engraver, positions) == CardPosition.FaceUpDefence,
            "under-2000 Engraver prefers defense while building on turn one");
        Check(ai.Executor.OnSelectPosition(lacrimaFusion, positions) == CardPosition.FaceUpDefence,
            "2400/2400 Lacrima prefers defense while building on turn one");
        const int powerToolDragon = 2403771;
        Check(ai.Executor.OnSelectPosition(powerToolDragon, positions) == CardPosition.FaceUpDefence,
            "2300/2500 Power Tool Dragon prefers defense while building on turn one");
        negateDuel.Turn = 3;
        Check(ai.Executor.OnSelectPosition(engraver, positions) != CardPosition.FaceUpDefence,
            "combat Main 1 retains the ordinary position policy");
        negateDuel.Phase = DuelPhase.Main2;
        Check(ai.Executor.OnSelectPosition(engraver, positions) == CardPosition.FaceUpDefence,
            "under-2000 Engraver prefers defense while building in Main 2");
        Check(ai.Executor.OnSelectPosition(powerToolDragon, positions) == CardPosition.FaceUpDefence,
            "2300/2500 Power Tool Dragon prefers defense while building in Main 2");

        var drawEffects = type.GetNestedType("OpponentDrawEffects", BindingFlags.NonPublic);
        var pressure = Activator.CreateInstance(drawEffects);
        foreach (string effect in new[] { "MaxxC", "Fuwalos", "Purulia", "Nyalus", "Moon" })
            drawEffects.GetField(effect).SetValue(pressure, 1);
        var fromSummon = drawEffects.GetMethod("FromSummon");
        Func<CardLocation, bool, int> givesDraws = (source, normal) =>
            (int)fromSummon.Invoke(pressure, new object[] { source, normal });
        Check(givesDraws(CardLocation.Hand, true) == 1, "only Purulia taxes normal summons");
        foreach (var source in new[] { CardLocation.Hand, CardLocation.Deck, CardLocation.Extra,
            CardLocation.Grave, CardLocation.Removed })
            Check(givesDraws(source, false) == 2, "Maxx C and the matching Mulcharmy stack for " + source);
        drawEffects.GetField("Fuwalos").SetValue(pressure, 2);
        Check(givesDraws(CardLocation.Extra, false) == 3, "two resolved Fuwalos draw independently");

        positionType.GetField("Hand").SetValue(position, new List<int>());
        positionType.GetField("Grave").SetValue(position, new List<int>());
        positionType.GetField("Extra").SetValue(position, new List<int>());
        positionType.GetField("Set").SetValue(position, 1);
        Check(terminal(new[] { smallBlue, 0, 0, 0, 0, 0, 0 }) == 0, "Disconnect requires an Evil Twin");
        Check(terminal(new[] { blue, 0, 0, 0, 0, 0, 0 }) == 10, "a live Disconnect earns one point");
        positionType.GetField("OpponentDraws").SetValue(position, 1);
        Check(terminal(new[] { blue, 0, 0, 0, 0, 0, 0 }) == 0, "one opponent draw deducts a full interruption point");

        var moveType = type.GetNestedType("PlanMove", BindingFlags.NonPublic);
        var resultType = type.GetNestedType("PlanResult", BindingFlags.NonPublic);
        var plan = Activator.CreateInstance(resultType, true);
        var planMoves = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(moveType));
        var summon = Activator.CreateInstance(moveType, true);
        moveType.GetField("Type").SetValue(summon, ExecutorType.SpSummon);
        moveType.GetField("SummonSource").SetValue(summon, CardLocation.Extra);
        planMoves.Add(summon);
        resultType.GetField("Moves").SetValue(plan, planMoves);
        type.GetField("_boardPlan", Private).SetValue(ai.Executor, plan);
        var maxxC = new ClientCard(23434538, CardLocation.Hand, 0, 0) { Controller = 1 };
        negateDuel.CurrentChainInfo.Add(new ChainInfo(maxxC, 1, 23434538 * 16));
        var hold = type.GetMethod("ShouldSaveCounterForDrawEffect", Private);
        Check((bool)hold.Invoke(ai.Executor, null), "one projected draw saves Ash and Crossout");
        planMoves.Add(summon);
        Check(!(bool)hold.Invoke(ai.Executor, null), "the counter remains available when two draws are projected");

        var drawDuel = new Duel { MainPhase = new MainPhase() };
        var drawExecutor = new TwinsExecutor(null, drawDuel);
        var afterDraw = Activator.CreateInstance(positionType, true);
        const int disconnect = 25156;
        positionType.GetField("Field").SetValue(afterDraw, new[] { blue, 0, 0, 0, 0, 0, 0 });
        positionType.GetField("Hand").SetValue(afterDraw, new List<int> { disconnect });
        foreach (string zone in new[] { "Deck", "Grave", "Extra" })
            positionType.GetField(zone).SetValue(afterDraw, new List<int>());
        positionType.GetField("Draws").SetValue(afterDraw, 1);
        drawDuel.MainPhase.SpellSetableCards.Add(new ClientCard(disconnect, CardLocation.Hand, 0, 0));
        var drawPlan = Activator.CreateInstance(resultType, true);
        resultType.GetField("Moves").SetValue(drawPlan, Activator.CreateInstance(typeof(List<>).MakeGenericType(moveType)));
        var drawPlanner = type.GetMethod("ImprovePlanAfterDraw", Private);
        var boardPlanField = type.GetField("_boardPlan", Private);
        boardPlanField.SetValue(drawExecutor, drawPlan);
        var upgrade = drawPlanner.Invoke(drawExecutor, new[] { afterDraw, System.Diagnostics.Stopwatch.StartNew() });
        Check(upgrade != null && (int)resultType.GetField("Score").GetValue(upgrade) == 14,
            "a drawn Disconnect can add a scored interruption with an Evil Twin in play");
        boardPlanField.SetValue(drawExecutor, upgrade);
        Check(drawPlanner.Invoke(drawExecutor, new[] { afterDraw, System.Diagnostics.Stopwatch.StartNew() }) == null,
            "an equal-scoring replan keeps the existing suffix");
        var nightfallSet = Activator.CreateInstance(moveType, true);
        moveType.GetField("Card").SetValue(nightfallSet, nightfall);
        moveType.GetField("Traps").SetValue(nightfallSet, new[] { disconnect, 98360333 });
        var invalidatesDraw = type.GetMethod("DrawInvalidatesDeckMove", BindingFlags.Static | BindingFlags.NonPublic);
        Check((bool)invalidatesDraw.Invoke(null, new object[] { nightfallSet, disconnect, false })
            && !(bool)invalidatesDraw.Invoke(null, new object[] { nightfallSet, disconnect, true }),
            "drawing the last Disconnect updates Nightfall's set plan unless a grave copy is available");
        positionType.GetField("Field").SetValue(afterDraw, new[] { nightfall, 0, 0, 0, 0, 0, 0 });
        positionType.GetField("Set").SetValue(afterDraw, 1);
        boardPlanField.SetValue(drawExecutor, drawPlan);
        Check(drawPlanner.Invoke(drawExecutor, new[] { afterDraw, System.Diagnostics.Stopwatch.StartNew() }) == null,
            "Nightfall's already-set Disconnect and a drawn copy share one interaction");
        const int challenge = 98360333;
        positionType.GetField("Hand").SetValue(afterDraw, new List<int> { challenge });
        drawDuel.MainPhase.SpellSetableCards.Clear();
        drawDuel.MainPhase.SpellSetableCards.Add(new ClientCard(challenge, CardLocation.Hand, 0, 0));
        var challengeUpgrade = drawPlanner.Invoke(drawExecutor, new[] { afterDraw, System.Diagnostics.Stopwatch.StartNew() });
        Check(challengeUpgrade != null && (int)resultType.GetField("Score").GetValue(challengeUpgrade) == 14,
            "a drawn Challenge can be set as a tie-break without awarding another point");

        var sendOrder = type.GetMethod("PreferredTwinSend", BindingFlags.Static | BindingFlags.NonPublic);
        const int sweet = 82699999, treat = 81078880, newYear = 25150;
        var beforeRedDraw = Activator.CreateInstance(positionType, true);
        positionType.GetField("Field").SetValue(beforeRedDraw, new[] { bigRed, 0, 0, 0, blue, 0, 0 });
        positionType.GetField("Hand").SetValue(beforeRedDraw, new List<int>());
        positionType.GetField("Grave").SetValue(beforeRedDraw, new List<int>());
        positionType.GetField("Extra").SetValue(beforeRedDraw, new List<int>());
        positionType.GetField("Pending").SetValue(beforeRedDraw, 1UL << (int)Enum.Parse(effects, "RedDraw"));
        var pendingMoves = type.GetMethod("PendingMoves", Private);
        var netDrawPoints = type.GetMethod("NetDrawPoints", BindingFlags.Static | BindingFlags.NonPublic);
        Func<int[], int> redDrawPoints = deck =>
        {
            positionType.GetField("Deck").SetValue(beforeRedDraw, deck.ToList());
            var afterRedDraw = ((IEnumerable)pendingMoves.Invoke(executor, new[] { beforeRedDraw })).Cast<object>().Single();
            return (int)netDrawPoints.Invoke(null, new[] { afterRedDraw });
        };
        Check(redDrawPoints(new[] { sweet, newYear }) == 2 && redDrawPoints(new[] { newYear }) == 4,
            "Red's planned draw loses two tenths only while Sweet is still in the deck");
        var inDeck = new HashSet<int> { sweet, treat, newYear, frost };
        Func<int, bool, bool, bool, int> sendTarget = (sender, canSweet, canNewYear, spareNewYear) =>
            (int)sendOrder.Invoke(null, new object[] { sender, new Func<int, bool>(inDeck.Contains),
                canSweet, canNewYear, spareNewYear });
        Check(sendTarget(deal, true, false, false) == sweet && sendTarget(deal, false, false, false) == 0,
            "Deal forces the live Sweet send and leaves other sends available when Sweet is spent");
        Check(sendTarget(scout, false, true, false) == newYear
            && sendTarget(scout, false, false, false) == frost
            && sendTarget(scout, false, false, true) == newYear,
            "Scout sends live New Year, Frost, or a second New Year when Frost is stocked");
        var scoutSend = Activator.CreateInstance(positionType, true);
        positionType.GetField("Field").SetValue(scoutSend, new[] { smallRed, 0, 0, 0, 0, scout, 0 });
        positionType.GetField("Grave").SetValue(scoutSend, new List<int>());
        positionType.GetField("Extra").SetValue(scoutSend, new List<int>());
        positionType.GetField("Deck").SetValue(scoutSend, new List<int> { newYear, frost });
        var searchMoves = type.GetMethod("SearchMoves", Private);
        Func<int[], int[]> scoutTargets = hand =>
        {
            positionType.GetField("Hand").SetValue(scoutSend, hand.ToList());
            return ((IEnumerable)searchMoves.Invoke(executor, new[] { scoutSend })).Cast<object>()
                .Select(next => positionType.GetField("Move").GetValue(next))
                .Select(move => (int)move.GetType().GetField("Target").GetValue(move)).ToArray();
        };
        Check(scoutTargets(new int[0]).SequenceEqual(new[] { newYear }),
            "Scout keeps the New Year send when no copy can jump");
        Check(scoutTargets(new[] { newYear }).SequenceEqual(new[] { newYear, frost }),
            "Scout considers both sends when New Year can jump from the hand");
        var discardPriority = type.GetMethod("AbaoDiscardPriority", BindingFlags.Static | BindingFlags.NonPublic);
        var order = new[] { engraver, 25156, 42141493, frost, 65681983, 94145022, 27204311, 97268402,
            59438930, 10045474, 14558128, 23434538 };
        Func<int, bool, int> priority = (id, opponentTurn) =>
            (int)discardPriority.Invoke(null, new object[] { id, id == frost, opponentTurn });
        Check(order.OrderBy(id => priority(id, false)).SequenceEqual(order),
            "Abao discards Fiendsmith, traps, Fuwalos, spare Twins and the named hand traps in order");
        var opponentDiscardOrder = order.Where(id => id != 10045474).ToList();
        opponentDiscardOrder.Insert(3, 10045474);
        Check(order.OrderBy(id => priority(id, true)).SequenceEqual(opponentDiscardOrder),
            "on the opponent turn Abao discards Impermanence immediately after Fuwalos");
        var targeted = type.GetMethod("NewYearTargetToDodge", Private);
        var negate = new ChainInfo(veiler, 1, 97268402 * 16);
        var dealOnField = new ClientCard(deal, CardLocation.MonsterZone, 0, faceUp);
        var scoutOnField = new ClientCard(scout, CardLocation.MonsterZone, 1, faceUp);
        var redOnField = new ClientCard(bigRed, CardLocation.MonsterZone, 2, faceUp);
        foreach (var card in new[] { dealOnField, scoutOnField, redOnField })
            typeof(ClientCard).GetProperty("Attribute").SetValue(card, card.Data.Attribute);
        negate.Targets.Add(dealOnField);
        negate.Targets.Add(scoutOnField);
        Check(targeted.Invoke(ai.Executor, new object[] { negate }) == null,
            "New Year does not spend Deal or Scout to dodge targeted negation");
        negate.Targets.Add(redOnField);
        Check(targeted.Invoke(ai.Executor, new object[] { negate }) == redOnField,
            "New Year can remove another threatened monster from the target's reach");

        var continuationMethod = type.GetMethod("FreeDrawPressureContinuation", BindingFlags.Static | BindingFlags.NonPublic);
        var afterScout = Activator.CreateInstance(positionType, true);
        var fuwalosPressure = Activator.CreateInstance(drawEffects);
        drawEffects.GetField("Fuwalos").SetValue(fuwalosPressure, 1);
        positionType.GetField("DrawEffects").SetValue(afterScout, fuwalosPressure);
        positionType.GetField("Hand").SetValue(afterScout, new List<int>());
        positionType.GetField("Grave").SetValue(afterScout, new List<int> { newYear });
        positionType.GetField("Deck").SetValue(afterScout, new List<int> { trouble });
        positionType.GetField("Pending").SetValue(afterScout, 1UL << (int)Enum.Parse(effects, "NewYearJump"));
        Check(continuationMethod.Invoke(null, new[] { afterScout }).ToString() == "NewYear",
            "the draw-pressure frontier reserves Scout's immediate New Year continuation under Fuwalos");
        drawEffects.GetField("MaxxC").SetValue(fuwalosPressure, 1);
        positionType.GetField("DrawEffects").SetValue(afterScout, fuwalosPressure);
        Check(continuationMethod.Invoke(null, new[] { afterScout }).ToString() == "None",
            "the free continuation reserve excludes a New Year summon that costs a draw");

        var frostBoard = Activator.CreateInstance(positionType, true);
        positionType.GetField("Field").SetValue(frostBoard, new[] { deal, 0, 0, 0, 0, 0, 0 });
        positionType.GetField("Grave").SetValue(frostBoard, new List<int> { frost, frost });
        foreach (string zone in new[] { "Hand", "Deck", "Extra" })
            positionType.GetField(zone).SetValue(frostBoard, new List<int>());
        var opponentDraw = type.GetMethod("AddOpponentDraws", BindingFlags.Static | BindingFlags.NonPublic);
        opponentDraw.Invoke(null, new[] { frostBoard, (object)1 });
        var frostDraw = ((IEnumerable)type.GetMethod("PendingMoves", Private).Invoke(executor, new[] { frostBoard }))
            .Cast<object>().Single();
        Check((int)positionType.GetField("Draws").GetValue(frostDraw) == 1
            && ((List<int>)positionType.GetField("Grave").GetValue(frostDraw)).SequenceEqual(new[] { frost }),
            "a pressured own turn draws once and leaves the other Frost in grave");
        Check((int)score.Invoke(executor, new[] { frostDraw, (object)true })
            == (int)score.Invoke(executor, new[] { frostBoard, (object)true }) + 4,
            "the guaranteed own draw adds 0.4 while the other Frost retains its next-turn value");
        opponentDraw.Invoke(null, new[] { frostDraw, (object)1 });
        Check(!((IEnumerable)type.GetMethod("PendingMoves", Private).Invoke(executor, new[] { frostDraw })).Cast<object>().Any(),
            "an additional opponent draw does not retrigger Frost in the same turn");
        positionType.GetField("Field").SetValue(frostDraw, new[] { smallBlue, 0, 0, 0, 0, 0, 0 });
        positionType.GetField("Hand").SetValue(frostDraw, new List<int> { frost });
        positionType.GetField("Used").SetValue(frostDraw, 1UL << (int)Enum.Parse(effects, "FrostSummon"));
        var bodies = ((IEnumerable)type.GetMethod("BodyMoves", Private).Invoke(executor, new[] { frostDraw }))
            .Cast<object>().Select(state => positionType.GetField("Move").GetValue(state));
        Check(!bodies.Any(move => (ExecutorType)move.GetType().GetField("Type").GetValue(move) == ExecutorType.SpSummon),
            "drawing another Frost cannot repeat its once-per-turn hand summon");
        CheckChallengeRevival();
        CheckChallengeKeepsRedForBluePop(0);
        CheckChallengeKeepsRedForBluePop(1);
        CheckChallengeKeepsRedForBluePop(2);
        CheckChallengeBattleDefense();
        CheckChallengeWaitsForHeistTarget();
        CheckPlanSetsRemainingHandTraps();
        CheckLethalReposition();
        Console.WriteLine("Twins planner rules passed");
    }

    private static void CheckChallengeRevival()
    {
        const int challengeId = 98360333, red = 9205573, blue = 36609518;
        int faceUp = (int)CardPosition.FaceUpAttack;
        var duel = new Duel { Player = 1, Turn = 2, Phase = DuelPhase.Main1 };
        for (int player = 0; player < 2; player++)
            duel.Fields[player].Init(0, 0, player);
        duel.Fields[0].SetInitialDeck(new NamedCard[0]);
        string dialogPath = Path.Combine(Path.GetDirectoryName(typeof(GameAI).Assembly.Location), "Dialogs", "default.json");
        var ai = new GameAI(new GameClient(new WindBot.WindBotInfo { Chat = false, Dialog = dialogPath }), duel);
        var executor = new TwinsExecutor(ai, duel);
        ai.Executor = executor;
        var trap = new ClientCard(challengeId, CardLocation.SpellZone, 0, (int)CardPosition.FaceDownDefence);
        var blueInGrave = new ClientCard(blue, CardLocation.Grave, 0, faceUp);
        var redInGrave = new ClientCard(red, CardLocation.Grave, 1, faceUp);
        foreach (var card in new[] { redInGrave, blueInGrave })
            typeof(ClientCard).GetProperty("ProcCompleted").SetValue(card, 8);
        duel.Fields[0].Graveyard.Add(blueInGrave);
        var enemy = new ClientCard(14558127, CardLocation.MonsterZone, 0, faceUp) { Controller = 1 };
        duel.Fields[1].MonsterZone[0] = enemy;
        Func<bool> activate = () => ai.OnSelectChain(new[] { trap }, new[] { 0 }, new[] { false }, 0) >= 0;
        Check(!activate(), "Challenge is held when Blue alone has no effect to contribute");
        duel.Fields[0].MonsterZone[0] = new ClientCard(red, CardLocation.MonsterZone, 0, faceUp);
        Check(activate(), "Challenge revives Blue while Red is present and an enemy card can be destroyed");
        var pop = new ChainInfo(blueInGrave, 0, blue * 16);
        pop.Targets.Add(enemy);
        duel.CurrentChainInfo.Add(pop);
        Check(!activate(), "Challenge is held if the only target is already covered by a pending removal");
        duel.CurrentChainInfo.Clear();
        duel.Fields[1].MonsterZone[0] = null;
        var normalSpell = new ClientCard(53129443, CardLocation.SpellZone, 0, faceUp) { Controller = 1 };
        duel.Fields[1].SpellZone[0] = normalSpell;
        duel.CurrentChainInfo.Add(new ChainInfo(normalSpell, 1, 0));
        Check(!activate(), "Challenge does not spend Blue's pop on a spell that leaves after the chain");
        duel.CurrentChainInfo.Clear();
        duel.Fields[1].SpellZone[0] = null;
        duel.Fields[0].MonsterZone[0] = new ClientCard(blue, CardLocation.MonsterZone, 0, faceUp);
        duel.Fields[0].Graveyard.Clear();
        duel.Fields[0].Graveyard.Add(redInGrave);
        Check(activate(), "Challenge revives Red to draw even when the opponent has no cards on the field");
        typeof(TwinsExecutor).GetField("_searchLocked", Private).SetValue(executor, true);
        Check(!activate(), "Challenge is held if Droll blocks the only benefit");
        duel.ChainTargets.Add(trap);
        Check(activate(), "Challenge can salvage a revival when it is targeted for removal");

        duel.ChainTargets.Clear();
        duel.Phase = DuelPhase.Draw;
        duel.Fields[0].SpellZone[0] = trap;
        duel.Fields[0].MonsterZone[0] = new ClientCard(25154, CardLocation.MonsterZone, 0, faceUp);
        duel.Fields[0].Graveyard.Add(blueInGrave);
        duel.Fields[0].Graveyard.Add(new ClientCard(25150, CardLocation.Grave, 2, faceUp));
        duel.Fields[0].SetInitialDeck(new[] { NamedCard.Get(61976639) });
        typeof(TwinsExecutor).GetField("_searchLocked", Private).SetValue(executor, false);
        Check(!activate(), "Challenge reserves the complete New Year cycle instead of reviving New Year for an early search");
    }

    private static void CheckChallengeKeepsRedForBluePop(int desiraeTradeTargets)
    {
        const int challenge = 98360333, red = 9205573, blue = 36609518, deal = 6636319, heist = 25153;
        int faceUp = (int)CardPosition.FaceUpAttack;
        var duel = new Duel { Player = 1, Turn = 2, Phase = DuelPhase.Main1 };
        for (int player = 0; player < 2; player++)
            duel.Fields[player].Init(0, 0, player);
        var own = duel.Fields[0];
        own.SetInitialDeck(new NamedCard[0]);
        string dialogPath = Path.Combine(Path.GetDirectoryName(typeof(GameAI).Assembly.Location), "Dialogs", "default.json");
        var ai = new GameAI(new GameClient(new WindBot.WindBotInfo { Chat = false, Dialog = dialogPath }), duel);
        var executor = new TwinsExecutor(ai, duel);
        ai.Executor = executor;

        var trap = new ClientCard(challenge, CardLocation.SpellZone, 0, (int)CardPosition.FaceDownDefence);
        var redInGrave = new ClientCard(red, CardLocation.Grave, 0, faceUp);
        var blueInGrave = new ClientCard(blue, CardLocation.Grave, 1, faceUp);
        foreach (var card in new[] { redInGrave, blueInGrave })
        {
            typeof(ClientCard).GetProperty("ProcCompleted").SetValue(card, 8);
            own.Graveyard.Add(card);
        }
        own.SpellZone[0] = trap;
        own.ExtraDeck.Add(new ClientCard(deal, CardLocation.Extra, 0, 0));
        own.ExtraDeck.Add(new ClientCard(heist, CardLocation.Extra, 1, 0));
        var enemy = new ClientCard(60764609, CardLocation.MonsterZone, 0, faceUp) { Controller = 1 };
        typeof(ClientCard).GetProperty("Type").SetValue(enemy, enemy.Data.Type);
        duel.Fields[1].MonsterZone[0] = enemy;
        var newYear = new ClientCard(25150, CardLocation.Grave, 2, faceUp);
        if (desiraeTradeTargets > 0)
        {
            own.MonsterZone[3] = new ClientCard(Desirae, CardLocation.MonsterZone, 3, faceUp);
            typeof(TwinsExecutor).GetField("_desiraeNegateUsed", Private).SetValue(executor, true);
            var cost = new ClientCard(2463794, CardLocation.Grave, 3, faceUp);
            typeof(ClientCard).GetProperty("Race").SetValue(cost, cost.Data.Race);
            typeof(ClientCard).GetProperty("Attribute").SetValue(cost, cost.Data.Attribute);
            own.Graveyard.Add(cost);
            own.Graveyard.Add(newYear);
        }
        if (desiraeTradeTargets == 2)
        {
            var second = new ClientCard(14558128, CardLocation.MonsterZone, 1, faceUp) { Controller = 1 };
            typeof(ClientCard).GetProperty("Type").SetValue(second, second.Data.Type);
            duel.Fields[1].MonsterZone[1] = second;
        }

        Check(ai.OnSelectChain(new[] { trap }, new[] { 0 }, new[] { false }, 0) >= 0,
            "Challenge can activate when it can reach removal");
        var target = desiraeTradeTargets == 2 ? newYear : redInGrave;
        Check(ReferenceEquals(typeof(TwinsExecutor).GetField("_challengeRevivalTarget", Private).GetValue(executor), target),
            "Challenge prefers Red's removal unless Heist can also trade Desirae against another target");
        var link = new ChainInfo(trap, 0, 0);
        link.Targets.Add(target);
        duel.CurrentChainInfo.Add(link);
        duel.SolvingChainIndex = 1;
        own.Graveyard.Remove(target);
        own.MonsterZone[0] = target;
        target.Location = CardLocation.MonsterZone;
        if (desiraeTradeTargets == 2)
        {
            Check(executor.OnSelectYesNo(challenge * 16)
                && (int)typeof(TwinsExecutor).GetField("_challengeLink", Private).GetValue(executor) == heist,
                "Challenge keeps the planned Heist and Desirae interaction when they can cover separate targets");
            return;
        }
        Check(!executor.OnSelectYesNo(challenge * 16),
            "Challenge does not turn the only revived Red into Deal just because the opponent has a fresh target");

        duel.CurrentChainInfo.Clear();
        duel.SolvingChainIndex = 0;
        Check(ai.OnSelectChain(new[] { redInGrave }, new[] { red * 16 + 1 }, new[] { false }, 0) >= 0,
            "the preserved Red activates its Blue revival");
        own.Graveyard.Remove(blueInGrave);
        own.MonsterZone[1] = blueInGrave;
        blueInGrave.Location = CardLocation.MonsterZone;
        Check(ai.OnSelectChain(new[] { blueInGrave }, new[] { blue * 16 }, new[] { false }, 0) >= 0,
            "the revived Blue activates its removal");
    }

    private static void CheckChallengeWaitsForHeistTarget()
    {
        const int challenge = 98360333;
        int faceUp = (int)CardPosition.FaceUpAttack;
        var duel = new Duel { Player = 1, Turn = 2, Phase = DuelPhase.Main1 };
        for (int player = 0; player < 2; player++)
            duel.Fields[player].Init(0, 0, player);
        string dialogPath = Path.Combine(Path.GetDirectoryName(typeof(GameAI).Assembly.Location), "Dialogs", "default.json");
        var ai = new GameAI(new GameClient(new WindBot.WindBotInfo { Chat = false, Dialog = dialogPath }), duel);
        var executor = new TwinsExecutor(ai, duel);
        ai.Executor = executor;
        var own = duel.Fields[0];
        var newYear = new ClientCard(25150, CardLocation.Grave, 0, faceUp);
        own.Graveyard.Add(newYear);
        own.Graveyard.Add(new ClientCard(61976639, CardLocation.Grave, 1, faceUp));
        own.SetInitialDeck(new NamedCard[0]);
        own.MonsterZone[1] = new ClientCard(36609519, CardLocation.MonsterZone, 1, faceUp);
        own.MonsterZone[2] = new ClientCard(9205574, CardLocation.MonsterZone, 2, faceUp);
        own.ExtraDeck.Add(new ClientCard(25153, CardLocation.Extra, 0, 0));
        var effects = typeof(TwinsExecutor).GetNestedType("PlanEffect", BindingFlags.NonPublic);
        typeof(TwinsExecutor).GetField("_planUsed", Private).SetValue(executor,
            (1UL << (int)Enum.Parse(effects, "BluePop")) | (1UL << (int)Enum.Parse(effects, "RedDraw")));
        var trap = new ClientCard(challenge, CardLocation.SpellZone, 0, (int)CardPosition.FaceDownDefence);
        Func<bool> activate = () => ai.OnSelectChain(new[] { trap }, new[] { 0 }, new[] { false }, 0) >= 0;
        Check(!activate(), "Challenge is kept for Heist instead of spending it just to search Password");

        var fresh = new ClientCard(60764609, CardLocation.MonsterZone, 0, faceUp) { Controller = 1 };
        typeof(ClientCard).GetProperty("Type").SetValue(fresh, fresh.Data.Type);
        duel.Fields[1].MonsterZone[0] = fresh;
        Check(activate(), "Challenge is used once the opponent presents a fresh target for Heist");

        own.Graveyard.Remove(newYear);
        newYear.Location = CardLocation.MonsterZone;
        own.MonsterZone[0] = newYear;
        Check(executor.OnSelectYesNo(challenge * 16), "Challenge uses the revived Twin to make Heist into a live target");
        duel.Fields[1].MonsterZone[0] = null;
        Check(!executor.OnSelectYesNo(challenge * 16),
            "Challenge keeps the revived Twin when Blue cleared the last Heist target");
    }

    private static void CheckPlanSetsRemainingHandTraps()
    {
        const int imperm = 10045474, crossout = 65681983, maxxC = 23434538, fuwalos = 42141493;
        const CardLocation empty = (CardLocation)0;
        var scenarios = new[]
        {
            (field: empty, hand: new[] { imperm }, set: new int[0]),
            (field: empty, hand: new[] { imperm, crossout }, set: new[] { crossout, imperm }),
            (field: empty, hand: new[] { imperm, crossout, maxxC }, set: new int[0]),
            (field: empty, hand: new[] { imperm, crossout, fuwalos }, set: new int[0]),
            (field: CardLocation.MonsterZone, hand: new[] { imperm }, set: new[] { imperm }),
            (field: CardLocation.SpellZone, hand: new[] { imperm, crossout, maxxC, fuwalos }, set: new[] { imperm, crossout })
        };
        string dialogPath = Path.Combine(Path.GetDirectoryName(typeof(GameAI).Assembly.Location), "Dialogs", "default.json");
        foreach (var scenario in scenarios)
        {
            var duel = new Duel { Player = 0, Turn = 1, Phase = DuelPhase.Main1 };
            for (int player = 0; player < 2; player++)
                duel.Fields[player].Init(0, 0, player);
            var ai = new GameAI(new GameClient(new WindBot.WindBotInfo { Chat = false, Dialog = dialogPath }), duel);
            ai.Executor = new TwinsExecutor(ai, duel);
            var own = duel.Fields[0];
            own.SetInitialDeck(new NamedCard[0]);
            if (scenario.field == CardLocation.MonsterZone)
                own.MonsterZone[5] = new ClientCard(25154, CardLocation.MonsterZone, 5, (int)CardPosition.FaceUpAttack);
            else if (scenario.field == CardLocation.SpellZone)
                own.SpellZone[5] = new ClientCard(101307059, CardLocation.SpellZone, 5, (int)CardPosition.FaceUpAttack);
            foreach (int id in scenario.hand)
                own.Hand.Add(new ClientCard(id, CardLocation.Hand, 0, 0));

            var expected = new HashSet<int>(scenario.set);
            for (int step = 0; step <= scenario.set.Length; step++)
            {
                var main = new MainPhase { CanEndPhase = true };
                duel.MainPhase = main;
                foreach (var card in own.Hand.Where(card => card.IsCode(imperm, crossout)))
                {
                    card.ActionIndex[4] = main.SpellSetableCards.Count;
                    main.SpellSetableCards.Add(card);
                }
                var action = ai.OnSelectIdleCmd(main);
                if (step == scenario.set.Length)
                {
                    Check(action.Action == MainPhaseAction.MainAction.ToEndPhase,
                        "the end phase preserves hand traps when the field and hand call for it");
                    break;
                }
                Check(action.Action == MainPhaseAction.MainAction.SetSpell,
                    "the completed plan sets the next usable backrow card before ending");
                var set = main.SpellSetableCards[action.Index];
                Check(expected.Remove(set.Id), "the planned backrow follows the hand and field rules");
                if (scenario.field == empty)
                    Check(set.Id == scenario.set[step], "Crossout is set before Impermanence on an empty field");
                own.Hand.Remove(set);
                set.Location = CardLocation.SpellZone;
                set.Position = (int)CardPosition.FaceDownDefence;
                own.SpellZone[step] = set;
            }
        }
    }

    private static void CheckLethalReposition()
    {
        int attack = (int)CardPosition.FaceUpAttack, defense = (int)CardPosition.FaceUpDefence;
        var main = new MainPhase { CanBattlePhase = true, CanEndPhase = true };
        var duel = new Duel { Player = 0, Turn = 3, Phase = DuelPhase.Main1, MainPhase = main };
        for (int player = 0; player < 2; player++)
            duel.Fields[player].Init(0, 0, player);
        string dialogPath = Path.Combine(Path.GetDirectoryName(typeof(GameAI).Assembly.Location), "Dialogs", "default.json");
        var ai = new GameAI(new GameClient(new WindBot.WindBotInfo { Chat = false, Dialog = dialogPath }), duel);
        ai.Executor = new TwinsExecutor(ai, duel);

        var attacker = new ClientCard(27204311, CardLocation.MonsterZone, 0, attack);
        var needed = new ClientCard(60764609, CardLocation.MonsterZone, 1, defense);
        var spare = new ClientCard(31305911, CardLocation.MonsterZone, 2, defense);
        var blocker = new ClientCard(74677422, CardLocation.MonsterZone, 0, attack) { Controller = 1 };
        foreach (var card in new[] { attacker, needed, spare, blocker })
        {
            typeof(ClientCard).GetProperty("Attack").SetValue(card, card.Data.Attack);
            typeof(ClientCard).GetProperty("Defense").SetValue(card, card.Data.Defense);
        }
        duel.Fields[0].MonsterZone[0] = attacker;
        duel.Fields[0].MonsterZone[1] = needed;
        duel.Fields[0].MonsterZone[2] = spare;
        duel.Fields[1].MonsterZone[0] = blocker;
        duel.Fields[1].LifePoints = 1800;
        var search = new ClientCard(37582948, CardLocation.Hand, 0, 0);
        search.ActionActivateIndex[0] = 0;
        main.ActivableCards.Add(search);
        main.ActivableDescs.Add(0);
        main.ReposableCards.Add(spare);

        Check(ai.OnSelectIdleCmd(main).Action == MainPhaseAction.MainAction.Activate,
            "a defender missing from the engine's position-change options cannot supply lethal damage");
        main.ReposableCards.Add(needed);
        needed.ActionIndex[2] = 1;
        var action = ai.OnSelectIdleCmd(main);
        Check(action.Action == MainPhaseAction.MainAction.Repos && action.Index == 1 && needed.IsDefense(),
            "prioritize the one defender needed for lethal without changing the live board during projection");
        needed.Position = attack;
        main.ReposableCards.Remove(needed);
        Check(ai.OnSelectIdleCmd(main).Action == MainPhaseAction.MainAction.ToBattlePhase,
            "enter battle after the engine confirms the lethal attacker changed position");
        needed.Position = defense;
        main.ReposableCards.Add(needed);
        duel.Fields[1].LifePoints = 600;
        Check(ai.OnSelectIdleCmd(main).Action == MainPhaseAction.MainAction.ToBattlePhase,
            "enter battle directly if the existing attackers already have lethal");
    }

    private static void CheckChallengeBattleDefense()
    {
        const int challenge = 98360333, red = 36326160, blue = 73810864;
        int faceUp = (int)CardPosition.FaceUpAttack;
        var positions = new[] { CardPosition.FaceUpAttack, CardPosition.FaceUpDefence };
        string dialogPath = Path.Combine(Path.GetDirectoryName(typeof(GameAI).Assembly.Location), "Dialogs", "default.json");
        foreach (var starter in new[] { red, blue })
        {
            int partner = starter == red ? blue : red;
            var duel = new Duel { Player = 1, Turn = 2, Phase = DuelPhase.BattleStep };
            for (int player = 0; player < 2; player++)
                duel.Fields[player].Init(0, 0, player);
            var ai = new GameAI(new GameClient(new WindBot.WindBotInfo { Chat = false, Dialog = dialogPath }), duel);
            var executor = new TwinsExecutor(ai, duel);
            ai.Executor = executor;
            var own = duel.Fields[0];
            var trap = new ClientCard(challenge, CardLocation.SpellZone, 0, (int)CardPosition.FaceDownDefence);
            var twin = new ClientCard(starter, CardLocation.Grave, 0, faceUp);
            own.Graveyard.Add(twin);
            own.SetInitialDeck(new NamedCard[0]);
            Func<bool> activate = () => ai.OnSelectChain(new[] { trap }, new[] { 0 }, new[] { false }, 0) >= 0;
            Check(!activate(), "Challenge preserves the trap if an empty board can only get one small blocker");

            if (starter == red)
                own.SetInitialDeck(new[] { NamedCard.Get(partner) });
            else
                own.Hand.Add(new ClientCard(partner, CardLocation.Hand, 0, faceUp));
            own.MonsterZone[0] = new ClientCard(partner, CardLocation.MonsterZone, 0, faceUp);
            Check(!activate(), "Challenge keeps the trap if the first small Twin would not be the only monster");
            own.MonsterZone[0] = null;
            duel.Phase = DuelPhase.Main1;
            Check(!activate(), "Challenge does not commit the defensive pair before the opponent's Battle Phase");
            duel.Phase = DuelPhase.BattleStep;
            var planUsed = typeof(TwinsExecutor).GetField("_planUsed", Private);
            var effects = typeof(TwinsExecutor).GetNestedType("PlanEffect", BindingFlags.NonPublic);
            planUsed.SetValue(executor, 1UL << (int)Enum.Parse(effects, starter == red ? "SmallRed" : "SmallBlue"));
            Check(!activate(), "Challenge holds if the original Twin already used its summon effect this turn");
            planUsed.SetValue(executor, 0UL);
            Check(activate(), "Challenge revives an original Twin that can summon a second blocker");

            duel.CurrentChainInfo.Add(new ChainInfo(trap, 0, 0));
            duel.SolvingChainIndex = 1;
            Check(ai.OnSelectPosition(starter, positions) == CardPosition.FaceUpDefence,
                "Challenge summons the first blocker in face-up Defense Position");
            Check(!executor.OnSelectYesNo(challenge * 16), "Challenge preserves the blockers instead of Link Summoning");
            twin.Location = CardLocation.MonsterZone;
            own.MonsterZone[0] = twin;
            executor.OnChainEnd();
            duel.CurrentChainInfo[0] = new ChainInfo(twin, 0, starter * 16);
            Check(ai.OnSelectPosition(partner, positions) == CardPosition.FaceUpDefence,
                "the original Twin summons the second blocker in face-up Defense Position after Challenge resolves");
        }
    }
}
