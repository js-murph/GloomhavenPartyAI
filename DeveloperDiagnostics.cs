using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using BepInEx;
using BepInEx.Configuration;
using ScenarioRuleLibrary;

namespace GloomhavenPartyAI
{
    // Wire Initialize(Plugin.DeveloperMode) after binding the default-false config entry.
    // Call Record/ObserveMessage/ObserveState on the game thread, before automation gates.
    // EndSession must run before scenario teardown; Shutdown runs on plugin destruction.
    internal static class DeveloperDiagnostics
    {
        internal const int MaxFiles = 5;
        internal const long MaxFileBytes = 5 * 1024 * 1024;
        private const int MaxLineBytes = 64 * 1024;
        private static readonly object Sync = new object();
        private static readonly Stopwatch Clock = Stopwatch.StartNew();
        private static readonly Encoding Utf8 = new UTF8Encoding(false);
        private static readonly string[] CounterNames =
        {
            "decisions", "failures", "handoffs", "attacks_submitted", "heals_submitted",
            "moves_submitted", "attacks_observed", "heals_observed", "moves_observed",
            "damage_notifications", "explicit_damage_total", "explicit_damage_samples",
            "heal_amount_reported_total", "death_notifications", "exhausted_player_notifications",
            "invalid_action_notifications", "undo_notifications", "restart_round_notifications",
            "capture_errors", "records_dropped", "items_submitted", "recoveries_submitted",
            "recoveries_observed", "cards_recovered", "short_rests_observed"
        };
        private static readonly long[] Counters = new long[CounterNames.Length];
        private static ConfigEntry<bool> _mode;
        private static Func<CActor, bool?> _isAutomated;
        private static bool _readingAutomation;
        private static volatile bool _enabled;
        private static FileStream _stream;
        private static string _directory;
        private static string _session;
        private static string _startedUtc;
        private static long _sequence;
        private static CScenario _scenario;
        private static WeakReference<CScenario> _finishedScenario;
        private static string _result = "unknown";
        private static bool _explicitResult;
        private static double _retryAfter;
        private static double _warnAfter;
        private static ConditionalWeakTable<CMessageData, object> _seen =
            new ConditionalWeakTable<CMessageData, object>();

        internal static bool Enabled
        {
            get
            {
                lock (Sync)
                {
                    try { return CanCapture(); }
                    catch (Exception) { CaptureError(); return false; }
                }
            }
        }

        // The optional resolver must only read automation state. IsAutomated currently registers
        // the party and can log; diagnostics must not trigger that work merely to take a snapshot.
        internal static void Initialize(ConfigEntry<bool> developerMode, Func<CActor, bool?> isAutomated = null)
        {
            lock (Sync)
            {
                if (_readingAutomation) return;
                try
                {
                    _enabled = false;
                    if (_mode != null) _mode.SettingChanged -= ModeChanged;
                    ClearSession();
                    _mode = developerMode;
                    _isAutomated = isAutomated;
                    if (_mode != null) _mode.SettingChanged += ModeChanged;
                    _enabled = _mode != null && _mode.Value;
                    CanCapture();
                }
                catch (Exception) { CaptureError(); }
            }
        }

        private static void ModeChanged(object sender, EventArgs args)
        {
            lock (Sync)
            {
                try
                {
                    _enabled = _mode != null && _mode.Value;
                    // No final write on opt-out. Every preceding record already contains counters.
                    CanCapture();
                }
                catch (Exception) { CaptureError(); }
            }
        }

        // Kinds: decision, card_selection, action_selection, rest, damage_response, failure,
        // handoff, attack_submitted, heal_submitted, move_submitted, toggle, snapshot,
        // item_candidate, item_evaluation, item_submitted, item_confirmed,
        // pair_candidate, action_candidate, target_candidate, move_candidate.
        // Detail is NOT free text: semicolon-separated allowlisted numeric key=value pairs,
        // plus action/reason codes (see SafeDetail). Names, exception text, paths and arbitrary
        // strings are discarded. Numbers use invariant culture (not localized interpolation).
        // Example: "action=attack;score=12.5;targets=1;reason=best_score".
        internal static void Record(string kind, CActor actor, string detail)
        {
            lock (Sync)
            {
                try
                {
                    if (!CanCapture() || !EnsureSession()) return;
                    switch (kind)
                    {
                        case "decision": case "card_selection": case "action_selection":
                        case "rest": case "damage_response": Counters[0]++; break;
                        case "failure": Counters[1]++; break;
                        case "handoff": Counters[2]++; break;
                        case "attack_submitted": Counters[3]++; break;
                        case "heal_submitted": Counters[4]++; break;
                        case "move_submitted": Counters[5]++; break;
                        case "item_submitted": Counters[20]++; break;
                        case "recovery_submitted": Counters[21]++; break;
                        case "item_candidate": case "item_evaluation": case "item_confirmed":
                        case "pair_candidate": case "action_candidate": case "target_candidate":
                        case "move_candidate": case "retreat_candidate": case "door_decision":
                        case "toggle": case "snapshot": break;
                        default: kind = "other"; break;
                    }
                    if (!CanWrite()) return;
                    Write(kind, actor, SafeDetail(detail));
                }
                catch (Exception) { CaptureError(); }
            }
        }

        internal static void ObserveMessage(CMessageData message)
        {
            lock (Sync)
            {
                try
                {
                    if (!CanCapture() || message == null || !EnsureSession()) return;
                    if (_seen.TryGetValue(message, out object ignored)) return;
                    _seen.Add(message, new object());
                    CActor actor = message.m_ActorSpawningMessage;
                    CActor target = null;
                    int? hpBefore = null;
                    int? amount = null;
                    string kind;
                    switch (message.m_Type)
                    {
                        case CMessageData.MessageType.RecoverLostCards:
                            kind = "recovery_observed";
                            if (message is CRecoverLostCards_MessageData recovered &&
                                recovered.m_ActorRecoveringLostCards is CPlayerActor recovering &&
                                recovered.m_Ability is CAbilityRecoverLostCards recovery)
                            {
                                actor = recovering;
                                amount = Math.Max(0, recovery.StartLostCards - recovering.CharacterClass.LostAbilityCards.Count);
                                Counters[22]++;
                                Counters[23] += amount.Value;
                            }
                            break;
                        case CMessageData.MessageType.PlayerShortRested:
                            kind = "short_rest_observed";
                            actor = (message as CPlayerShortRested_MessageData)?.m_Player ?? actor;
                            Counters[24]++;
                            break;
                        case CMessageData.MessageType.ActorHasAttacked:
                            kind = "attack_observed";
                            Counters[6]++;
                            actor = (message as CActorHasAttacked_MessageData)?.m_AttackingActor ?? actor;
                            break;
                        case CMessageData.MessageType.ActorBeenHealed:
                            kind = "heal_observed";
                            Counters[7]++;
                            if (message is CActorBeenHealed_MessageData healed)
                            {
                                target = healed.m_ActorBeingHealed;
                                hpBefore = healed.m_ActorOriginalHealth;
                                amount = healed.m_HealAmount;
                                Counters[12] += Math.Max(0, healed.m_HealAmount);
                            }
                            break;
                        case CMessageData.MessageType.ActorHasMoved:
                            kind = "move_observed";
                            Counters[8]++;
                            actor = (message as CActorHasMoved_MessageData)?.m_MovingActor ?? actor;
                            break;
                        case CMessageData.MessageType.ActorBeenDamaged:
                            kind = "damage_observed";
                            Counters[9]++;
                            if (message is CActorBeenDamaged_MessageData damaged)
                            {
                                target = damaged.m_ActorBeingDamaged;
                                hpBefore = damaged.m_ActorOriginalHealth;
                                amount = damaged.m_ActualDamage;
                                if (amount.HasValue)
                                {
                                    Counters[10] += Math.Max(0, amount.Value);
                                    Counters[11]++;
                                }
                            }
                            break;
                        case CMessageData.MessageType.ActorBeenAttacked:
                            kind = "attack_target_observed";
                            if (message is CActorBeenAttacked_MessageData attacked)
                            {
                                actor = attacked.m_AttackingActor ?? actor;
                                target = attacked.m_ActorBeingAttacked;
                                hpBefore = attacked.m_ActorOriginalHealth;
                            }
                            break;
                        case CMessageData.MessageType.ActorBeenAttackedAndKilled:
                            kind = "attack_target_killed_observed";
                            if (message is CActorBeenAttackedAndKilled_MessageData killed)
                            {
                                actor = killed.m_AttackingActor ?? actor;
                                target = killed.m_ActorBeingAttacked;
                                hpBefore = killed.m_ActorOriginalHealth;
                            }
                            break;
                        case CMessageData.MessageType.ActorDead:
                            kind = "death_observed";
                            Counters[13]++;
                            actor = (message as CActorDead_MessageData)?.m_Actor ?? actor;
                            break;
                        case CMessageData.MessageType.PlayersExhausted:
                            kind = "exhaustion_observed";
                            if (message is CPlayersExhausted_MessageData exhausted && exhausted.m_Players != null)
                            {
                                Counters[14] += exhausted.m_Players.Count;
                                if (!CanWrite()) return;
                                foreach (CPlayerActor player in exhausted.m_Players)
                                    Write(kind, player, new DiagnosticJson().Add("message", message.m_Type.ToString()));
                                return;
                            }
                            break;
                        case CMessageData.MessageType.InvalidAttack:
                        case CMessageData.MessageType.InvalidHeal:
                        case CMessageData.MessageType.InvalidMove:
                            kind = "invalid_action_observed";
                            Counters[15]++;
                            break;
                        case CMessageData.MessageType.SRLExceptionMessage:
                        case CMessageData.MessageType.SRLWrongPhaseExceptionMessage:
                            kind = "game_failure_observed";
                            Counters[1]++;
                            break;
                        case CMessageData.MessageType.Undo:
                            kind = "undo_observed";
                            Counters[16]++;
                            break;
                        case CMessageData.MessageType.RestartRound:
                            kind = "restart_round_observed";
                            Counters[17]++;
                            break;
                        case CMessageData.MessageType.NextRound:
                        case CMessageData.MessageType.EndRound:
                        case CMessageData.MessageType.StartTurn:
                        case CMessageData.MessageType.PlayerToSelectAbilityCardsOrLongRest:
                        case CMessageData.MessageType.ActionSelection:
                            kind = "phase_observed";
                            break;
                        default: return;
                    }
                    if (!CanWrite()) return;
                    var data = new DiagnosticJson().Add("message", message.m_Type.ToString())
                        .Add("target", Snapshot(target));
                    if (hpBefore.HasValue) data.Add("target_hp_before", hpBefore.Value);
                    if (amount.HasValue) data.Add("amount_reported", amount.Value);
                    if (message is CActorBeenHealed_MessageData heal)
                        data.Add("poison_removed", heal.m_PoisonTokenRemoved).Add("wound_removed", heal.m_WoundTokenRemoved);
                    Write(kind, actor, data);
                }
                catch (Exception) { CaptureError(); }
            }
        }

        // Optional game-thread poll: Choreographer assigns Win/Lose AFTER EndScenarioSafely
        // calls ScenarioRuleClient.Stop. Resetting at that Stop leaves the result unknown;
        // keep this session open until assignment to observe it. No session is started or ended
        // here: the explicit lifecycle handler calls EndSession after observing the result.
        internal static void ObserveState()
        {
            lock (Sync)
            {
                try
                {
                    if (CanCapture() && _session != null) ObserveResult();
                }
                catch (Exception) { CaptureError(); }
            }
        }

        // Wire a main-thread prefix on Choreographer.LogScenarioResult(result). Abandon passes
        // Resign here without setting CurrentScenarioResult. This hook finalizes existing capture;
        // it never creates a session just to report a result, nor changes the game's result field.
        internal static void ObserveScenarioResult(SEventActorFinishedScenario.EScenarioResult result)
        {
            lock (Sync)
            {
                bool finalize = false;
                try
                {
                    if (!CanCapture() || _session == null || !ReferenceEquals(_scenario, ScenarioManager.Scenario)) return;
                    switch (result)
                    {
                        case SEventActorFinishedScenario.EScenarioResult.Win: _result = "win"; break;
                        case SEventActorFinishedScenario.EScenarioResult.Lose: _result = "lose"; break;
                        case SEventActorFinishedScenario.EScenarioResult.Resign: _result = "resign"; break;
                        default: return;
                    }
                    _finishedScenario = new WeakReference<CScenario>(_scenario);
                    _explicitResult = true;
                    finalize = true;
                    if (CanWrite()) Write("scenario_result_observed", null,
                        new DiagnosticJson().Add("source", "Choreographer.LogScenarioResult"));
                }
                catch (Exception) { CaptureError(); }
                finally
                {
                    if (finalize) EndSession("scenario_result");
                }
            }
        }

        internal static void Reset() { EndSession("reset"); }

        internal static void EndSession(string reason = "stop")
        {
            lock (Sync)
            {
                if (_readingAutomation) return;
                try
                {
                    if (CanCapture() && _session != null)
                    {
                        ObserveResult();
                        if (CanWrite())
                            Write("session_end", null, new DiagnosticJson().Add("reason", EndReason(reason)));
                    }
                }
                catch (Exception) { CaptureError(); }
                finally { ClearSession(); }
            }
        }

        internal static void Shutdown()
        {
            lock (Sync)
            {
                if (_readingAutomation) return;
                try
                {
                    EndSession("plugin_stop");
                    _enabled = false;
                    if (_mode != null) _mode.SettingChanged -= ModeChanged;
                    _mode = null;
                    _isAutomated = null;
                }
                catch (Exception) { CaptureError(); }
                finally { _enabled = false; ClearSession(); }
            }
        }

        // Called under Sync, including from Enabled and null-message polls. An online/menu
        // transition closes the file without a summary even if the caller forgot its own gate.
        private static bool CanCapture()
        {
            if (!_enabled || _mode == null || !_mode.Value || ScenarioManager.Scenario == null ||
                FFSNetwork.IsOnline || FFSNetwork.IsStartingUp || FFSNetwork.IsShuttingDown)
            {
                if (_session != null || _stream != null) ClearSession();
                return false;
            }
            return !_readingAutomation;
        }

        private static bool EnsureSession()
        {
            CScenario current = ScenarioManager.Scenario;
            if (current == null) return false;
            if (_finishedScenario != null)
            {
                if (_finishedScenario.TryGetTarget(out CScenario finished) && ReferenceEquals(current, finished)) return false;
                _finishedScenario = null;
            }
            if (_session != null && !ReferenceEquals(current, _scenario)) EndSession("scenario_changed");
            if (_session == null)
            {
                // Late outcome messages must not reopen an explicitly ended session.
                if (current.CurrentScenarioResult != SEventActorFinishedScenario.EScenarioResult.None) return false;
                _session = Guid.NewGuid().ToString("N");
                _startedUtc = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);
                _scenario = current;
                if (CanWrite()) Write("session_start", null, new DiagnosticJson().Add("counter_scope", "observed_notifications_not_unique_actions"));
            }
            ObserveResult();
            return _session != null;
        }

        private static void ObserveResult()
        {
            if (_explicitResult) return;
            // Read only the scenario associated with this capture, never a newly loaded result.
            string result = "unknown";
            if (_scenario != null)
            {
                switch (_scenario.CurrentScenarioResult)
                {
                    case SEventActorFinishedScenario.EScenarioResult.Win: result = "win"; break;
                    case SEventActorFinishedScenario.EScenarioResult.Lose: result = "lose"; break;
                    case SEventActorFinishedScenario.EScenarioResult.Resign: result = "resign"; break;
                }
            }
            if (result == "unknown" || result == _result) return;
            _result = result;
            if (CanWrite()) Write("scenario_result_observed", null,
                new DiagnosticJson().Add("source", "CScenario.CurrentScenarioResult"));
        }

        private static DiagnosticJson Snapshot(CActor actor)
        {
            if (actor == null) return null;
            var data = new DiagnosticJson().Add("guid", Limit(actor.ActorGuid))
                .Add("class", Limit(actor.Class?.ID)).Add("hp", actor.Health)
                .Add("max_hp", actor.MaxHealth).Add("dead", actor.IsDead)
                .Add("cause_of_death", actor.CauseOfDeath.ToString())
                .Add("type", actor.Type.ToString())
                .Add("position", new DiagnosticJson().Add("x", actor.ArrayIndex.X).Add("y", actor.ArrayIndex.Y));
            // CActor.ID is not implemented for object actors (doors, objectives).
            try { data.Add("actor_id", actor.ID); }
            catch (Exception) { data.Add("actor_id", (string)null); }
            try { data.Add("initiative", actor.Initiative()); }
            catch (Exception) { data.Add("initiative", (string)null); }
            bool? automated = null;
            if (_isAutomated != null)
            {
                _readingAutomation = true;
                try { automated = _isAutomated(actor); }
                catch (Exception) { data.Add("automated_unavailable", true); }
                finally { _readingAutomation = false; }
                if (!CanCapture()) return null;
            }
            if (automated.HasValue) data.Add("automated", automated.Value);
            else data.Add("automated", (string)null);
            if (actor.Tokens != null)
            {
                // HasKey mutates token lists. The Check properties return locked copies instead.
                int positive = 0, negative = 0;
                foreach (var token in actor.Tokens.CheckPositiveTokens)
                    if (token != null) positive |= (int)token.PositiveCondition;
                foreach (var token in actor.Tokens.CheckNegativeTokens)
                    if (token != null) negative |= (int)token.NegativeCondition;
                var positiveConditions = new DiagnosticJson();
                var negativeConditions = new DiagnosticJson();
                foreach (CCondition.EPositiveCondition condition in CCondition.PositiveConditions)
                    if ((positive & (int)condition) != 0) positiveConditions.Add(condition.ToString(), true);
                foreach (CCondition.ENegativeCondition condition in CCondition.NegativeConditions)
                    if ((negative & (int)condition) != 0) negativeConditions.Add(condition.ToString(), true);
                data.Add("conditions", new DiagnosticJson().Add("positive", positiveConditions).Add("negative", negativeConditions));
            }
            CInventory inventory = actor.Inventory;
            if (inventory != null)
            {
                int total = 0, usable = 0, spent = 0, consumed = 0;
                // Read slots directly: AllItems assumes initialized arrays; item YML getters can
                // load metadata and log errors. Only the stored SlotState is needed here.
                var slots = new[]
                {
                    new[] { inventory.HeadSlot, inventory.BodySlot, inventory.LegSlot, inventory.TwoHandSlot },
                    inventory.OneHandSlots, inventory.SmallItemSlots, inventory.QuestItemSlots
                };
                foreach (CItem[] slot in slots)
                {
                    if (slot == null) continue;
                    foreach (CItem item in slot)
                    {
                        if (item == null) continue;
                        total++;
                        switch (item.SlotState)
                        {
                            case CItem.EItemSlotState.Useable: usable++; break;
                            case CItem.EItemSlotState.Spent: spent++; break;
                            case CItem.EItemSlotState.Consumed: consumed++; break;
                        }
                    }
                }
                data.Add("inventory", new DiagnosticJson().Add("total", total).Add("usable", usable)
                    .Add("spent", spent).Add("consumed", consumed));
            }
            if (actor is CPlayerActor player && player.CharacterClass != null)
            {
                CCharacterClass cards = player.CharacterClass;
                data.Add("cards", new DiagnosticJson()
                    .Add("hand", cards.HandAbilityCards?.Count ?? 0)
                    .Add("round", cards.RoundAbilityCards?.Count ?? 0)
                    .Add("discarded", cards.DiscardedAbilityCards?.Count ?? 0)
                    .Add("lost", cards.LostAbilityCards?.Count ?? 0)
                    .Add("permanently_lost", cards.PermanentlyLostAbilityCards?.Count ?? 0)
                    .Add("activated", cards.ActivatedCards?.Count ?? 0));
            }
            return data;
        }

        private static DiagnosticJson SafeDetail(string detail)
        {
            var data = new DiagnosticJson();
            if (string.IsNullOrEmpty(detail)) return data;
            bool redacted = detail.Length > 1024;
            var keys = new HashSet<string>(StringComparer.Ordinal);
            // Bound parsing/allocation even if a caller accidentally passes a stack trace.
            foreach (string field in detail.Substring(0, Math.Min(detail.Length, 1024)).Split(';'))
            {
                int equals = field.IndexOf('=');
                if (equals < 1) { redacted = true; continue; }
                string key = field.Substring(0, equals).Trim();
                string value = field.Substring(equals + 1).Trim();
                if (!keys.Add(key)) { redacted = true; continue; }
                switch (key)
                {
                    case "score": case "top_score": case "bottom_score": case "initiative":
                    case "targets": case "distance": case "damage": case "heal": case "cards":
                    case "x": case "y": case "elapsed_ms": case "candidates": case "enabled":
                    case "top_card_id": case "bottom_card_id": case "card_id":
                    case "item_id": case "message_id": case "path_cost": case "movement":
                    case "bonus": case "target_id": case "first_card_id": case "second_card_id":
                    case "default_action": case "top": case "bottom": case "first":
                    case "threats": case "future_damage":
                        if (!data.TryAddNumber(key, value)) redacted = true;
                        break;
                    case "action":
                        switch (value)
                        {
                            case "attack": case "heal": case "move": case "rest": case "cards":
                            case "damage": case "skip": case "toggle": case "item": case "recover": data.Add(key, value); break;
                            default: redacted = true; break;
                        }
                        break;
                    case "reason":
                        switch (value)
                        {
                            case "best_score": case "emergency": case "no_target": case "no_path":
                            case "unsupported": case "exception": case "stale": case "timeout":
                            case "manual": case "disabled": case "online": case "cancelled":
                            case "fallback": case "lethal": case "nonlethal": case "no_cards":
                            case "invalid_state": case "no_action": case "door": case "user_toggle":
                            case "unavailable": case "unhelpful": case "supported": case "ui_not_ready":
                            case "retreat": case "party_not_ready": case "door_ready": case "exposure":
                            case "under_threat": case "short_rest": case "redraw":
                            case "short_rest_unavailable": case "no_recoverable_cards":
                                data.Add(key, value); break;
                            default: redacted = true; break;
                        }
                        break;
                    default: redacted = true; break;
                }
            }
            return data.Add("detail_redacted", redacted);
        }

        private static string Limit(string value)
        {
            return value == null || value.Length <= 128 ? value : value.Substring(0, 128);
        }

        private static string EndReason(string reason)
        {
            switch (reason)
            {
                case "stop": case "reset": case "plugin_stop": case "scenario_changed":
                case "scenario_start": case "scenario_result": return reason;
                default: return "other";
            }
        }

        private static bool CanWrite()
        {
            if (!CanCapture()) return false;
            if (Clock.Elapsed.TotalSeconds >= _retryAfter) return true;
            Counters[19]++;
            return false;
        }

        private static void Write(string kind, CActor actor, DiagnosticJson data)
        {
            if (!CanCapture() || _session == null) return;
            var counters = new DiagnosticJson();
            for (int i = 0; i < CounterNames.Length; i++) counters.Add(CounterNames[i], Counters[i]);
            var record = new DiagnosticJson().Add("schema_version", 1).Add("plugin_version", Plugin.Version)
                .Add("game_rules_version", typeof(CActor).Assembly.GetName().Version.ToString())
                .Add("session", _session).Add("session_started_utc", _startedUtc)
                .Add("utc", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture))
                .Add("sequence", ++_sequence).Add("kind", kind)
                .Add("result", _result).Add("phase", PhaseManager.PhaseType.ToString());
            if (ReferenceEquals(_scenario, ScenarioManager.Scenario) && ScenarioManager.CurrentScenarioState != null)
                record.Add("round", ScenarioManager.CurrentScenarioState.RoundNumber);
            else record.Add("round", (string)null);
            record.Add("scenario_level", _scenario.Level);
            record.Add("actor", Snapshot(actor)).Add("data", data).Add("counters", counters);
            byte[] bytes = Utf8.GetBytes(record + "\n");
            if (bytes.Length > MaxLineBytes) { Counters[19]++; return; }
            if (!CanCapture() || _session == null) return;
            if (_stream == null)
            {
                _directory = Path.Combine(Paths.BepInExRootPath, "PartyAI", "diagnostics");
                Directory.CreateDirectory(_directory);
                Rotate();
            }
            else if (_stream.Length + bytes.Length > MaxFileBytes) Rotate();
            _stream.Write(bytes, 0, bytes.Length);
            // No buffered JSON remains to be flushed after the user disables capture.
            _stream.Flush();
        }

        private static string FilePath(int index)
        {
            return Path.Combine(_directory, "capture-" + index.ToString(CultureInfo.InvariantCulture) + ".jsonl");
        }

        private static void Rotate()
        {
            Close();
            // Fixed owned filenames: never enumerate or delete unrelated user files.
            File.Delete(FilePath(MaxFiles - 1));
            for (int i = MaxFiles - 2; i >= 0; i--)
                if (File.Exists(FilePath(i))) File.Move(FilePath(i), FilePath(i + 1));
            _stream = new FileStream(FilePath(0), FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        }

        private static void CaptureError()
        {
            Counters[18]++;
            Counters[19]++;
            Close();
            BackOff();
        }

        private static void BackOff()
        {
            double now = Clock.Elapsed.TotalSeconds;
            _retryAfter = now + 60;
            if (now < _warnAfter) return;
            _warnAfter = now + 60;
            // Do not expose exception messages, local paths, or game object descriptions.
            try { Plugin.Log?.LogWarning("Party AI diagnostic capture failed; retrying in 60 seconds. Some diagnostic records may be missing."); }
            catch (Exception) { }
        }

        private static void Close()
        {
            FileStream stream = _stream;
            _stream = null;
            try { stream?.Dispose(); }
            catch (Exception)
            {
                // Resource cleanup must not escape into automation or config callbacks.
                Counters[18]++;
                BackOff();
            }
        }

        private static void ClearSession()
        {
            Close();
            _session = null;
            _scenario = null;
            _startedUtc = null;
            _result = "unknown";
            _explicitResult = false;
            _sequence = 0;
            Array.Clear(Counters, 0, Counters.Length);
            _seen = new ConditionalWeakTable<CMessageData, object>();
            // Keep the weak finished-scenario marker across resets/opt-out: Resign may leave
            // CurrentScenarioResult at None. EnsureSession drops it when a different scenario starts.
        }
    }
}
