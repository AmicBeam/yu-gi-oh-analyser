using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using WindBot.Game;
using WindBot.Game.AI.Decks;
using YGOSharp.OCGWrapper;

// Shares the terminal scoring rules with WindBot. Input consists only of
// snapshots of the real engine, so no planner action or search is invoked.
internal static class TwinsEngineScore
{
    [DataContract]
    private sealed class EnginePosition
    {
        [DataMember] public int[] board, hand, grave, deck, extra, set, equip, unrevivable;
        [DataMember] public int[][] activations;
        [DataMember] public int scoring_draws, summons, normal_zones, blocked_zones, spell_slots, new_year_zone;
        [DataMember] public bool early_requiem, search_locked;
    }

    [DataContract]
    private sealed class Candidate
    {
        [DataMember] public EnginePosition position, nibiru_window;
    }

    private const BindingFlags Hidden = BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
    private static readonly Type ExecutorType = typeof(TwinsExecutor);
    private static readonly Type PositionType = ExecutorType.GetNestedType("PlanPosition", BindingFlags.NonPublic);
    private static readonly Type MoveType = ExecutorType.GetNestedType("PlanMove", BindingFlags.NonPublic);
    private static readonly MethodInfo Score = ExecutorType.GetMethod("EndBoardScore", Hidden);
    private static readonly MethodInfo Capture = ExecutorType.GetMethod("CaptureNibiruWindow", Hidden);
    private static readonly MethodInfo PlanFlag = ExecutorType.GetMethod("PlanFlag", Hidden);
    private static readonly MethodInfo TrapBit = ExecutorType.GetMethod("TrapBit", Hidden);
    private static readonly MethodInfo ExtraFlag = ExecutorType.GetMethod("ExtraFlag", Hidden);

    private static void Set(object position, string key, object value) =>
        PositionType.GetField(key).SetValue(position, value);

    private static int CardCode(int id)
    {
        var data = NamedCard.Get(id);
        return data != null && data.Alias > 0 ? data.Alias : id;
    }

    private static ulong UsedEffects(int[][] activations)
    {
        ulong result = 0;
        foreach (var activation in activations)
        {
            int card = CardCode(activation[0]);
            int location = activation[1];
            int description = activation[2];
            int effect = 0;
            if (description > 0 && (description / 16 == card || description / 16 == activation[0]))
                effect = description % 16;
            else if (card == 25150)
                effect = location == 4 ? 1 : 0;
            else if (card == 25151)
                effect = location == 2 ? 0 : 1;
            var move = Activator.CreateInstance(MoveType, true);
            MoveType.GetField("Card").SetValue(move, card);
            MoveType.GetField("Effect").SetValue(move, effect);
            var flag = PlanFlag.Invoke(null, new[] { move });
            if (flag != null)
                result |= 1UL << Convert.ToInt32(flag);
        }
        return result;
    }

    private static object ToPosition(EnginePosition snapshot)
    {
        var position = Activator.CreateInstance(PositionType, true);
        Set(position, "Field", snapshot.board);
        Set(position, "Hand", snapshot.hand.ToList());
        Set(position, "Grave", snapshot.grave.ToList());
        Set(position, "Deck", snapshot.deck.ToList());
        Set(position, "Extra", snapshot.extra.ToList());
        Set(position, "Draws", snapshot.scoring_draws);
        Set(position, "Set", snapshot.set.Aggregate(0, (bits, id) => bits | (int)TrapBit.Invoke(null, new object[] { id })));
        Set(position, "Unrevivable", snapshot.unrevivable.Aggregate(0, (bits, id) =>
            bits | (int)ExtraFlag.Invoke(null, new object[] { id })));
        Set(position, "Equipment", (snapshot.equip.Contains(2463794) ? 1 : 0) | (snapshot.equip.Contains(49867899) ? 2 : 0));
        Set(position, "NormalZones", snapshot.normal_zones);
        Set(position, "BlockedExtraZones", snapshot.blocked_zones);
        Set(position, "NewYearZone", snapshot.new_year_zone);
        Set(position, "SpellSlots", snapshot.spell_slots);
        Set(position, "OwnSummons", snapshot.summons);
        Set(position, "EarlyRequiem", snapshot.early_requiem);
        Set(position, "SearchLocked", snapshot.search_locked);
        Set(position, "Used", UsedEffects(snapshot.activations));
        return position;
    }

    public static void Main(string[] args)
    {
        NamedCardsManager.Init(args[0]);
        var duel = new Duel();
        duel.Fields[0].Init(0, 0, 0);
        duel.Fields[0].SetInitialDeck(new NamedCard[0]);
        duel.Fields[1].Init(0, 0, 1);
        var executor = new TwinsExecutor(null, duel);
        var serializer = new DataContractJsonSerializer(typeof(Candidate));
        string line;
        while ((line = Console.ReadLine()) != null)
        {
            using (var stream = new MemoryStream(Encoding.UTF8.GetBytes(line)))
            {
                var candidate = (Candidate)serializer.ReadObject(stream);
                var terminal = ToPosition(candidate.position);
                if (candidate.nibiru_window != null)
                {
                    var window = ToPosition(candidate.nibiru_window);
                    bool pendingNewYear = PositionType.GetField("NewYearZone").GetValue(window) is int zone && zone >= 0;
                    Capture.Invoke(null, new[] { window, (object)pendingNewYear });
                    foreach (string flag in new[] { "RequiemDefenseAtNibiruWindow", "PhotoAtNibiruWindow" })
                        Set(terminal, flag, PositionType.GetField(flag).GetValue(window));
                }
                Console.WriteLine(Score.Invoke(executor, new[] { terminal, (object)true }));
            }
        }
    }
}
