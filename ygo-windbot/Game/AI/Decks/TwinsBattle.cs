using System.Collections.Generic;
using System.Linq;
using YGOSharp.OCGWrapper;
using YGOSharp.OCGWrapper.Enums;

namespace WindBot.Game.AI.Decks
{
    public partial class TwinsExecutor
    {
        private bool _nightfallEffectUsed;
        private bool _sunnyFieldEffectUsed;
        private bool _ownNightfallRotation;
        private bool _passwordUsed;
        private bool _troubleUsed;
        private bool _searchLocked;
        private int _nightfallTrapSearch;

        private void ResetBattleRoute()
        {
            _nightfallEffectUsed = false;
            _sunnyFieldEffectUsed = false;
            _ownNightfallRotation = false;
            _passwordUsed = false;
            _troubleUsed = false;
            _searchLocked = false;
            _nightfallTrapSearch = 0;
        }

        private void RecordBattleRouteEffects()
        {
            for (int i = 0; i < Duel.CurrentChainInfo.Count; ++i)
            {
                ChainInfo chain = Duel.CurrentChainInfo[i];
                if (chain.ActivatePlayer != 0)
                {
                    _searchLocked |= chain.IsActivateCode(Droll) && !Duel.NegatedChainIndexList.Contains(i + 1);
                    continue;
                }
                if (chain.ActivateLocation == CardLocation.MonsterZone)
                {
                    _nightfallEffectUsed |= chain.IsActivateCode(Nightfall);
                    _sunnyFieldEffectUsed |= chain.IsActivateCode(Sunny);
                }
                _passwordUsed |= chain.IsActivateCode(Password);
                _troubleUsed |= chain.IsActivateCode(Trouble);
            }
            if (_passwordUsed || _searchLocked)
                _nightfallTrapSearch = 0;
        }

        private bool InBattle()
        {
            return Duel.Phase == DuelPhase.BattleStart || Duel.Phase == DuelPhase.BattleStep
                || Duel.Phase == DuelPhase.Battle;
        }

        private bool CanFightThisTurn()
        {
            return Duel.Player == 0 && Duel.Turn > 1 && (Duel.Phase == DuelPhase.Main1 || InBattle());
        }

        private bool AfterAttack(ClientCard card)
        {
            return InBattle() && card.Attacked && !Enemy.UnderAttack && !Bot.UnderAttack
                && Duel.CurrentChainInfo.Count == 0;
        }

        private IEnumerable<int> AdditionalNightfallTraps(IList<ClientCard> grave)
        {
            if (!Bot.SpellZone.Take(5).Any(card => card == null))
                yield break;
            if (!HasSetTrap(Disconnect) && !Bot.HasInHand(Disconnect)
                && (Bot.HasInDeck(Disconnect) || grave.Any(card => card.IsCode(Disconnect))))
                yield return Disconnect;
            if (!HasSetTrap(Challenge) && !Bot.HasInHand(Challenge)
                && (Bot.HasInDeck(Challenge) || grave.Any(card => card.IsCode(Challenge)))
                && grave.Any(card => card.IsCanRevive() && (card.HasSetcode(KiSikilSet) || card.HasSetcode(LilLaSet))))
                yield return Challenge;
        }

        private bool PasswordCanSpareTrouble()
        {
            return _troubleUsed || Bot.HasInHand(Trouble) || Bot.HasInSpellZone(Trouble);
        }

        private int PasswordCanCoverTrap(IList<int> traps)
        {
            return Duel.Player == 0 && !_passwordUsed && !_searchLocked && Bot.HasInHand(Password)
                && PasswordCanSpareTrouble() && traps.Count == 1 && Bot.HasInDeck(traps[0]) ? traps[0] : 0;
        }

        private bool ShouldSetNightfallTraps(ClientCard nightfall)
        {
            if (_abaoReloadNightfall)
                return NightfallReloadTraps().Count != 0;
            if (CanFinishWithNightfall(nightfall))
                return false;
            var traps = AdditionalNightfallTraps(Bot.Graveyard).ToList();
            if (traps.Count == 0)
                return false;
            var remaining = Bot.GetMonsters().Where(card => card != nightfall).ToList();
            if (CanFightThisTurn() && RotationAllowed(nightfall) && ReadyForSunny(remaining, Bot.Graveyard)
                && HasTwoTwinAttackers(Bot.Graveyard, remaining))
            {
                _nightfallTrapSearch = PasswordCanCoverTrap(traps);
                if (_nightfallTrapSearch != 0)
                    return false;
            }
            return true;
        }

        private bool RotationAllowed(ClientCard monster)
        {
            if (monster.IsDisabled())
                return false;
            if (!monster.IsSpecialSummoned || (!Bot.HasInMonstersZone(Gryphon, notDisabled: true, faceUp: true)
                && !Enemy.HasInMonstersZone(Gryphon, notDisabled: true, faceUp: true)))
                return true;

            var own = Duel.Fields[monster.Controller].GetMonsters();
            var other = Duel.Fields[1 - monster.Controller].GetMonsters();
            int incoming = LinkedZones(own) | (LinkedZones(other) >> 16);
            int occupied = own.Aggregate(0, (zones, card) => zones | (1 << card.Sequence));
            occupied |= other.Aggregate(0, (zones, card) => zones | (1 << (card.Sequence + 16)));
            return (incoming & (1 << monster.Sequence)) != 0 || (monster.GetLinkedZones() & occupied) != 0;
        }

        private int RotationExtraZones(IList<ClientCard> remaining)
        {
            int zones = FreeLinkedMainZones(remaining);
            if (Bot.HasInMonstersZone(Gryphon, notDisabled: true, faceUp: true)
                || Enemy.HasInMonstersZone(Gryphon, notDisabled: true, faceUp: true)
                || remaining.Any(card => card.Sequence >= 5))
                return zones;
            if (Enemy.MonsterZone[6] == null)
                zones |= Zones.z5;
            if (Enemy.MonsterZone[5] == null)
                zones |= Zones.z6;
            return zones;
        }

        private static int OpenMainZonesAfter(IList<ClientCard> remaining)
        {
            return 5 - remaining.Count(card => card.Sequence >= 0 && card.Sequence < 5);
        }

        private static int PrintedAttack(int id)
        {
            return NamedCard.Get(id)?.Attack ?? 0;
        }

        private int TwinAttackFromGrave(IList<ClientCard> grave, int slots)
        {
            if (_sunnyFieldEffectUsed || slots <= 0)
                return 0;
            return new[] { EvilKiSikil, EvilLilLa }
                .Where(id => grave.Any(card => card.IsCode(id) && card.IsCanRevive()))
                .Select(PrintedAttack).OrderByDescending(attack => attack).Take(slots).Sum();
        }

        private bool HasTwoTwinAttackers(IList<ClientCard> grave, IList<ClientCard> remaining)
        {
            return TwinAttackFromGrave(grave, OpenMainZonesAfter(remaining))
                >= PrintedAttack(EvilKiSikil) + PrintedAttack(EvilLilLa);
        }

        private bool ReadyForSunny(IList<ClientCard> remaining, IList<ClientCard> grave)
        {
            return !_nightfallEffectUsed && Bot.HasInExtra(Sunny) && RotationExtraZones(remaining) != 0
                && grave.Any(card => card.HasSetcode(EvilTwinSet) && !card.IsCode(EvilKiSikil, EvilLilLa));
        }

        private static int ReadyAttackDamage(IEnumerable<ClientCard> cards)
        {
            return cards.Where(card => card.IsFaceup() && card.IsAttack() && !card.Attacked)
                .Sum(card => System.Math.Max(0, card.GetAttackPower()));
        }

        private bool TryLeadAttack(int attack, int tailDamage, out ClientCard defender)
        {
            return TryLeadAttack(attack, tailDamage, Enemy.GetMonsters(), out defender);
        }

        private bool TryLeadAttack(int attack, int tailDamage, IList<ClientCard> defenders, out ClientCard defender)
        {
            defender = null;
            if (attack <= 0 || Enemy.LifePoints <= 0)
                return false;
            if (defenders.Count == 0)
                return attack + tailDamage >= Enemy.LifePoints;
            if (defenders.Count != 1)
                return false;

            ClientCard blocker = defenders[0];
            if (!blocker.IsFaceup() || blocker.IsMonsterDangerous() || blocker.IsMonsterInvincible()
                || attack <= blocker.GetDefensePower())
                return false;
            int damage = blocker.IsAttack() ? attack - blocker.GetDefensePower() : 0;
            if (damage + tailDamage < Enemy.LifePoints)
                return false;
            defender = blocker;
            return true;
        }

        private bool PrepareNightfallRotation()
        {
            if (Duel.Player != 0 || Duel.Turn <= 1 || Duel.Phase != DuelPhase.Main1)
                return false;
            var field = Bot.GetMonsters();
            var materials = field.Where(card => card.IsFaceup() && card.LinkCount == 2).ToList();
            foreach (var pair in Util.GetLinkMaterials(materials, 4, 2, 2))
            {
                if (!pair.Any(card => card.HasSetcode(KiSikilSet) || card.HasSetcode(LilLaSet)))
                    continue;
                var remaining = field.Except(pair).ToList();
                var grave = Bot.Graveyard.Concat(pair).ToList();
                if (!ReadyForSunny(remaining, grave))
                    continue;
                bool lethal = TryLeadAttack(PrintedAttack(Nightfall),
                    PrintedAttack(Sunny) + TwinAttackFromGrave(grave, OpenMainZonesAfter(remaining))
                    + ReadyAttackDamage(remaining), out _);
                var traps = AdditionalNightfallTraps(grave).ToList();
                bool reserveRotation = HasTwoTwinAttackers(grave, remaining)
                    && (traps.Count == 0 || PasswordCanCoverTrap(traps) != 0);
                if (!lethal && !reserveRotation)
                    continue;
                AI.SelectMaterials(pair, HintMsg.LinkMaterial);
                AI.SelectPlace(RotationExtraZones(remaining));
                return true;
            }
            return false;
        }

        private bool CanFinishWithNightfall(ClientCard nightfall)
        {
            if (!CanFightThisTurn() || !nightfall.IsFaceup() || !nightfall.IsAttack() || nightfall.Attacked
                || !RotationAllowed(nightfall))
                return false;
            var remaining = Bot.GetMonsters().Where(card => card != nightfall).ToList();
            return ReadyForSunny(remaining, Bot.Graveyard)
                && TryLeadAttack(nightfall.GetAttackPower(), PrintedAttack(Sunny)
                    + TwinAttackFromGrave(Bot.Graveyard, OpenMainZonesAfter(remaining))
                    + ReadyAttackDamage(remaining), out _);
        }

        private bool EnterBattleForNightfall()
        {
            return Duel.Phase == DuelPhase.Main1 && Bot.GetMonsters()
                .Any(card => card.IsCode(Nightfall) && CanFinishWithNightfall(card));
        }

        private bool OwnNightfallActivate()
        {
            if (!RotationAllowed(Card))
                return false;
            var remaining = Bot.GetMonsters().Where(card => card != Card).ToList();
            if (!ReadyForSunny(remaining, Bot.Graveyard))
                return false;

            int twinsDamage = TwinAttackFromGrave(Bot.Graveyard, OpenMainZonesAfter(remaining));
            int directDamage = ReadyAttackDamage(remaining.Where(card => card.CanDirectAttack));
            if (AfterAttack(Card) && Enemy.GetMonsters().Count == 0 && directDamage >= Enemy.LifePoints)
                return false;
            bool extraDamage = AfterAttack(Card) && TryLeadAttack(PrintedAttack(Sunny), twinsDamage + directDamage, out _);
            bool extraValue = !AdditionalNightfallTraps(Bot.Graveyard).Any(id => id != _nightfallTrapSearch)
                && HasTwoTwinAttackers(Bot.Graveyard, remaining)
                && (AfterAttack(Card) && Enemy.GetMonsters().Count == 0 || Duel.Phase == DuelPhase.Main2 && HasVisibleEnemyCard());
            if (!extraDamage && !extraValue)
                return false;
            _ownNightfallRotation = true;
            AI.SelectCard(NightfallCosts().ToList());
            AI.SelectPlace(RotationExtraZones(remaining));
            return true;
        }

        private bool OwnSunnyActivate()
        {
            if (_sunnyFieldEffectUsed || !RotationAllowed(Card))
                return false;
            var remaining = Bot.GetMonsters().Where(card => card != Card).ToList();
            int damage = TwinAttackFromGrave(Bot.Graveyard, OpenMainZonesAfter(remaining));
            int directDamage = ReadyAttackDamage(remaining.Where(card => card.CanDirectAttack));
            bool pair = damage >= PrintedAttack(EvilKiSikil) + PrintedAttack(EvilLilLa);
            return AfterAttack(Card) && Enemy.GetMonsters().Count == 0
                    && damage > 0 && directDamage < Enemy.LifePoints
                    && (damage + directDamage >= Enemy.LifePoints || _ownNightfallRotation && pair)
                || Duel.Phase == DuelPhase.Main2 && _ownNightfallRotation && pair && HasVisibleEnemyCard();
        }

        public override BattlePhaseAction OnBattle(IList<ClientCard> attackers, IList<ClientCard> defenders)
        {
            foreach (ClientCard attacker in attackers.Where(card => card.IsCode(Nightfall, Sunny) && RotationAllowed(card)))
            {
                var remaining = Bot.GetMonsters().Where(card => card != attacker).ToList();
                int support = ReadyAttackDamage(attackers.Where(card => card != attacker
                    && (defenders.Count > 0 || card.CanDirectAttack)));
                int tail = TwinAttackFromGrave(Bot.Graveyard, OpenMainZonesAfter(remaining)) + support;
                if (attacker.IsCode(Nightfall))
                {
                    if (!ReadyForSunny(remaining, Bot.Graveyard))
                        continue;
                    tail += PrintedAttack(Sunny);
                }
                if (TryLeadAttack(attacker.GetAttackPower(), tail, out ClientCard target)
                    && (target == null ? attacker.CanDirectAttack : OnPreBattleBetween(attacker, target)))
                    return AI.Attack(attacker, target);
            }
            if (defenders.Count == 1)
            {
                int burn = CanPlanLacrimaBurn(CurrentBattlePosition()) ? LacrimaBurnDamage : 0;
                foreach (ClientCard attacker in attackers)
                {
                    int support = ReadyAttackDamage(attackers.Where(card => card != attacker)) + burn;
                    if (TryLeadAttack(attacker.GetAttackPower(), support, defenders, out ClientCard target)
                        && OnPreBattleBetween(attacker, target))
                        return AI.Attack(attacker, target);
                }
            }
            return TrySecondTurnAttack(attackers, defenders) ?? base.OnBattle(attackers, defenders);
        }
    }
}
