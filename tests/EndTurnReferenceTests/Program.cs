using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using GloomhavenPartyAI;
using HarmonyLib;
using ScenarioRuleLibrary;

internal static class Program
{
    private static int Main()
    {
        string gameRoot = Assembly.GetExecutingAssembly().GetCustomAttributes<AssemblyMetadataAttribute>()
            .Single(attribute => attribute.Key == "GameRoot").Value;
        AssemblyLoadContext.Default.Resolving += (context, name) =>
        {
            foreach (string folder in new[] { "GH_Data/Managed", "BepInEx/core" })
            {
                string path = Path.Combine(gameRoot, folder, name.Name + ".dll");
                if (File.Exists(path)) return context.LoadFromAssemblyPath(path);
            }
            return null;
        };
        // Load test types (including their game-constrained generics) only after installing resolution.
        return (int)Assembly.GetExecutingAssembly().GetType("EndTurnTests")
            .GetMethod("Run", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, [gameRoot]);
    }
}

internal static class EndTurnTests
{
    private static string gameRoot;
    private static int checks;
    private const BindingFlags Fields = BindingFlags.Static | BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;

    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static int Run(string root)
    {
        gameRoot = root;
        Action[] tests = [ActualReadyButtonIlHasFourTranspilerMatches, PhaseBoundCallbacksCannotBorrowAuthority,
            OwnedCallbackAllowsOnlyOnePass, FailureStatesDistinguishSafeManualRecovery,
            EndTurnPhaseRejectsStalePassButNotOtherConfirmations, OnlineGuardsLeaveManualInputUntouched,
            UnreadyCallbackCannotSubmitOrResumeAfterExplicitFailure];
        int failures = 0;
        foreach (Action test in tests)
        {
            object phase = typeof(PhaseManager).GetField("s_CurrentPhase", Fields).GetValue(null);
            object actor = typeof(GameState).GetField("s_CurrentActor", Fields).GetValue(null);
            bool ready = AutomationController.Ready;
            try
            {
                EndTurnController.Reset();
                AutomationController.Ready = true;
                test();
                Console.WriteLine("PASS CASE " + test.Method.Name);
            }
            catch (Exception exception)
            {
                failures++;
                Console.Error.WriteLine("FAIL CASE " + test.Method.Name + ": " + exception);
            }
            finally
            {
                EndTurnController.Reset();
                Set(typeof(PhaseManager), null, "s_CurrentPhase", phase);
                Set(typeof(GameState), null, "s_CurrentActor", actor);
                AutomationController.Ready = ready;
            }
        }
        Console.WriteLine($"{tests.Length - failures}/{tests.Length} EndTurn reference cases passed; {checks} managed checks passed; {failures} cases failed.");
        Console.WriteLine("Isolation: actual linked EndTurnController, actual installed ReadyButton IL, real inert actor/phase/button fields; " +
            "synthetic callbacks, rule-return values and AutomationController readiness shim, not a patched running game. " +
            "No TryEndTurn UI acceptance, Unity lifecycle, effect queue, rule submission, network session, or live handshake exercised.");
        return failures == 0 ? 0 : 1;
    }

    private static void ActualReadyButtonIlHasFourTranspilerMatches()
    {
        using var assembly = Mono.Cecil.AssemblyDefinition.ReadAssembly(Path.Combine(gameRoot, "GH_Data/Managed/GH.Runtime.dll"));
        var click = assembly.MainModule.Types.Single(type => type.Name == "ReadyButton").Methods.Single(method => method.Name == "OnClickInternal");
        var stores = click.Body.Instructions.Where(instruction => instruction.OpCode == Mono.Cecil.Cil.OpCodes.Stfld &&
            ((Mono.Cecil.FieldReference)instruction.Operand).Name == "actionDelayed").ToList();
        Check(stores.Count == 4, "Installed ReadyButton IL has exactly four actionDelayed stores");
        var opcodes = typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(field => field.FieldType == typeof(OpCode)).Select(field => (OpCode)field.GetValue(null))
            .ToDictionary(opcode => opcode.Value);
        // Feed the full real instruction stream, resolving callback fields to the FieldInfo shape
        // Harmony supplies. Other Cecil operands are opaque to this transpiler; no IL is emitted.
        var input = click.Body.Instructions.Select(instruction => new CodeInstruction(opcodes[instruction.OpCode.Value],
            stores.Contains(instruction) ? typeof(ReadyButton).Module.ResolveField(((Mono.Cecil.FieldReference)instruction.Operand).MetadataToken.ToInt32()) :
            instruction.Operand)).ToList();
        var output = ((IEnumerable<CodeInstruction>)Call("CaptureEndTurnCallback", input)).ToList();
        MethodInfo capture = typeof(EndTurnController).GetMethod("CaptureCallback", BindingFlags.Static | BindingFlags.NonPublic);
        Check(output.Count(instruction => instruction.opcode == OpCodes.Call && Equals(instruction.operand, capture)) == 4 &&
            output.Count == input.Count + 8, "Actual transpiler injects four captures into the complete installed method");
        Check(input.SequenceEqual(output.Where(instruction => input.Contains(instruction))), "Original IL instruction order is preserved");
        Check(stores.All(store =>
        {
            int index = output.IndexOf(input[click.Body.Instructions.IndexOf(store)]);
            return output[index - 2].opcode == OpCodes.Ldarg_0 && output[index - 1].opcode == OpCodes.Call &&
                Equals(output[index - 1].operand, capture);
        }), "Every callback store receives the owning button argument before capture");
        CodeInstruction firstStore = input[click.Body.Instructions.IndexOf(stores[0])];
        foreach (var malformed in new[] { new List<CodeInstruction>(), input.Where(instruction => instruction != firstStore).ToList(),
            input.Concat(new[] { firstStore }).ToList() })
        {
            try
            {
                Call("CaptureEndTurnCallback", malformed);
                throw new InvalidOperationException("Incompatible layout was accepted");
            }
            catch (TargetInvocationException exception) when (exception.InnerException is InvalidOperationException)
            {
                Check(!(bool)typeof(EndTurnController).GetField("_callbackPatchReady", Fields).GetValue(null),
                    "Zero/three/five callback layouts fail closed and clear patch readiness");
            }
        }
    }

    private static void PhaseBoundCallbacksCannotBorrowAuthority()
    {
        var actor = Actor();
        var other = Actor();
        var phase = Phase<CPhaseActionSelection>(CPhase.PhaseType.ActionSelection);
        Set(typeof(GameState), null, "s_CurrentActor", actor);
        object request = Claim(phase, actor);
        Check(EndTurnController.IsPending(actor), "Pending matches original actor reference");
        Check(!EndTurnController.IsPending(other), "Different actor cannot inherit claim");
        int invoked = 0;
        Action wrapped = (Action)Call("CaptureCallback", (Action)(() => invoked++));
        Check(!ReferenceEquals(wrapped, Get(request, "Callback")), "Owned callback receives a phase-bound wrapper");
        EndTurnController.MarkFailed(actor);
        Check(!EndTurnController.IsPending(actor) && EndTurnController.HasFailed(actor), "Failure exits pending with explicit handoff state");
        wrapped();
        Check(invoked == 0, "Failed callback cannot submit");
        Claim(phase, actor);
        wrapped = (Action)Call("CaptureCallback", (Action)(() => invoked++));
        var next = Phase<CPhaseActionSelection>(CPhase.PhaseType.ActionSelection);
        Check(!EndTurnController.IsPending(actor), "New phase reference clears pending for the same actor");
        Claim(next, actor);
        wrapped();
        Check(invoked == 0 && EndTurnController.IsPending(actor), "Stale callback cannot borrow the new same-actor phase claim");
        EndTurnController.Reset();
        wrapped();
        Check(invoked == 0 && !EndTurnController.IsPending(actor), "Reset invalidates captured callbacks");
        Action manual = () => invoked++;
        Check(ReferenceEquals(Call("CaptureCallback", manual), manual), "Unowned manual/ability callbacks are unchanged");
        Check((bool)Call("PassPrefix"), "Offline unowned manual Pass remains legal outside EndTurn");
    }

    private static void OwnedCallbackAllowsOnlyOnePass()
    {
        var actor = Actor();
        Set(typeof(GameState), null, "s_CurrentActor", actor);
        object request = Claim(Phase<CPhaseActionSelection>(CPhase.PhaseType.ActionSelection), actor);
        var button = (ReadyButton)Get(request, "Button");
        Check(!(bool)Call("ReadyClickPrefix", button), "Second end-turn click is blocked before callback execution");
        Check(!(bool)Call("PassPrefix"), "Direct Pass cannot overtake the owned callback");
        int invoked = 0;
        Action wrapped = (Action)Call("CaptureCallback", (Action)(() =>
        {
            Check((bool)Call("PassPrefix"), "Owned callback authorizes its first Pass");
            Call("RulePassPostfix", 1u);
            Check(!(bool)Call("PassPrefix"), "Second Pass within the same callback is rejected");
            invoked++;
        }));
        wrapped();
        wrapped();
        Check(invoked == 1, "Owned callback executes once");
        Check(!(bool)Call("ReadyClickPrefix", button), "Duplicate click stays blocked after accepted synthetic submission");
        Set(typeof(ReadyButton), button, "buttonState", ReadyButton.EButtonState.EREADYBUTTONCONFIRM);
        Check((bool)Call("ReadyClickPrefix", button), "Other ability confirmation remains untouched with pending end turn");
        object[] nestedArgs = [button, null];
        typeof(EndTurnController).GetMethod("ReadyClickPrefix", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, nestedArgs);
        Action manual = () => invoked++;
        Check(ReferenceEquals(Call("CaptureCallback", manual), manual), "Nested ability callback does not inherit end-turn capture");
        Call("ReadyClickFinalizer", nestedArgs[1]);
        Check(ReferenceEquals(Call("CaptureCallback", manual, Button()), manual), "Another ready button cannot inherit ownership");
        EndTurnController.MarkFailed(actor);
        Check(!(bool)Call("PassPrefix") && EndTurnController.HasFailed(actor), "Uncertain accepted submission cannot authorize a second Pass");
    }

    private static void FailureStatesDistinguishSafeManualRecovery()
    {
        var actor = Actor();
        Set(typeof(GameState), null, "s_CurrentActor", actor);
        var phase = Phase<CPhaseActionSelection>(CPhase.PhaseType.ActionSelection);
        Claim(phase, actor);
        EndTurnController.MarkFailed(actor);
        Check((bool)Call("PassPrefix") && EndTurnController.HasFailed(actor), "Failure before callback permits manual recovery but remains failed");
        Claim(phase, actor);
        Action wrapped = (Action)Call("CaptureCallback", (Action)(() =>
        {
            Check((bool)Call("PassPrefix"), "Rejected submission initially had one-use authority");
            Call("RulePassPostfix", 0u);
        }));
        wrapped();
        Check(!EndTurnController.IsPending(actor) && EndTurnController.HasFailed(actor) && (bool)Call("PassPrefix"),
            "Explicit rule rejection permits manual recovery, unlike uncertain submission");
        Claim(phase, actor);
        wrapped = (Action)Call("CaptureCallback", (Action)(() => throw new InvalidOperationException("fixture")));
        try { wrapped(); } catch (InvalidOperationException exception) when (exception.Message == "fixture") { }
        Check(!EndTurnController.IsPending(actor) && EndTurnController.HasFailed(actor), "Callback exception becomes failure, not forever-pending");
        Check(typeof(EndTurnController).GetField("_executing", Fields).GetValue(null) == null, "Callback exception clears execution authority in finally");
        Check(!(bool)Call("PassPrefix"), "Callback-started exception is uncertain and cannot authorize another Pass");
    }

    private static void EndTurnPhaseRejectsStalePassButNotOtherConfirmations()
    {
        Phase<CPhaseEndTurn>(CPhase.PhaseType.EndTurn);
        var button = Button();
        Check(!(bool)Call("PassPrefix"), "Offline EndTurn rejects illegal/double Pass even without a claim");
        Check(!(bool)Call("ReadyClickPrefix", button), "Offline EndTurn rejects stale END TURN click before cleanup");
        Set(typeof(ReadyButton), button, "buttonState", ReadyButton.EButtonState.EREADYBUTTONCONFIRM);
        Check((bool)Call("ReadyClickPrefix", button), "Offline EndTurn does not blanket-block other confirmations");
    }

    private static void OnlineGuardsLeaveManualInputUntouched()
    {
        // BoltCore.IsRunning includes Shutdown even without a socket. Seed that enum only,
        // with FFSNetwork's separate shutdown flag false, to exercise its real online getter.
        Type core = Assembly.Load("bolt").GetType("Photon.Bolt.Internal.BoltCore", throwOnError: true);
        FieldInfo running = core.GetField("_mode", Fields) ?? throw new MissingFieldException(core.FullName, "_mode");
        object saved = running.GetValue(null);
        bool shuttingDown = FFSNetwork.IsShuttingDown;
        try
        {
            running.SetValue(null, Enum.Parse(running.FieldType, "Shutdown"));
            FFSNetwork.IsShuttingDown = false;
            Check(FFSNetwork.IsOnline, "Real FFSNetwork getter observes synthetic online state without a session");
            Phase<CPhaseEndTurn>(CPhase.PhaseType.EndTurn);
            Check((bool)Call("PassPrefix") && (bool)Call("ReadyClickPrefix", Button()), "Online Pass and END TURN click bypass offline guards");
            Action manual = () => throw new InvalidOperationException("must not execute");
            Check(ReferenceEquals(Call("CaptureCallback", manual), manual), "Online unowned callback remains unchanged");
            var actor = Actor();
            Set(typeof(GameState), null, "s_CurrentActor", actor);
            Claim(Phase<CPhaseActionSelection>(CPhase.PhaseType.ActionSelection), actor);
            int invoked = 0;
            Action wrapped = (Action)Call("CaptureCallback", (Action)(() => invoked++));
            wrapped();
            Check(invoked == 0 && (bool)Call("PassPrefix"), "Online state suppresses an owned callback without blocking manual Pass");
        }
        finally
        {
            running.SetValue(null, saved);
            FFSNetwork.IsShuttingDown = shuttingDown;
        }
    }

    private static CPlayerActor Actor() => (CPlayerActor)RuntimeHelpers.GetUninitializedObject(typeof(CPlayerActor));

    private static void UnreadyCallbackCannotSubmitOrResumeAfterExplicitFailure()
    {
        var actor = Actor();
        Set(typeof(GameState), null, "s_CurrentActor", actor);
        var phase = Phase<CPhaseActionSelection>(CPhase.PhaseType.ActionSelection);
        object request = Claim(phase, actor);
        int invoked = 0;
        Action submit = () =>
        {
            invoked++;
            Check((bool)Call("PassPrefix"), "ready positive-control callback has one-use Pass authority");
        };
        Action wrapped = (Action)Call("CaptureCallback", submit);
        AutomationController.Ready = false;
        wrapped();
        wrapped();
        Check(invoked == 0 && !(bool)Get(request, "CallbackStarted"), "unready delayed callback never starts");
        Check(!(bool)Get(request, "PassIssued") && typeof(EndTurnController).GetField("_executing", Fields).GetValue(null) == null,
            "unready callback acquires neither PassIssued nor execution authority");
        Check(EndTurnController.IsPending(actor) && !(bool)Call("PassPrefix"), "readiness rejection does not let direct Pass overtake pending callback");

        // The real ObserveFault calls MarkFailed immediately. Invoke that boundary explicitly;
        // the readiness shim does not simulate the production fault latch or message handling.
        EndTurnController.MarkFailed(actor);
        Check(EndTurnController.HasFailed(actor) && !EndTurnController.IsPending(actor), "explicit failure invalidates the unstarted request");
        AutomationController.Ready = true;
        wrapped();
        wrapped();
        Check(invoked == 0 && !(bool)Get(request, "PassIssued") && !(bool)Get(request, "CallbackStarted"),
            "restored readiness cannot revive an explicitly failed callback");
        Check((bool)Call("PassPrefix"), "failure before callback still permits separate manual recovery");

        request = Claim(phase, actor);
        ((Action)Call("CaptureCallback", submit))();
        Check(invoked == 1 && (bool)Get(request, "PassIssued"), "fresh ready request can submit; failure belonged to the old request");
    }

    private static T Phase<T>(CPhase.PhaseType type) where T : CPhase
    {
        var phase = (T)RuntimeHelpers.GetUninitializedObject(typeof(T));
        Set(typeof(CPhase), phase, "m_PhaseType", type);
        Set(typeof(PhaseManager), null, "s_CurrentPhase", phase);
        return phase;
    }

    private static ReadyButton Button()
    {
        var button = (ReadyButton)RuntimeHelpers.GetUninitializedObject(typeof(ReadyButton));
        Set(typeof(ReadyButton), button, "buttonState", ReadyButton.EButtonState.EREADYBUTTONENDTURN);
        return button;
    }

    private static object Claim(CPhase phase, CPlayerActor actor)
    {
        Type type = typeof(EndTurnController).GetNestedType("Request", BindingFlags.NonPublic);
        object request = Activator.CreateInstance(type, true);
        Set(type, request, "Phase", phase);
        Set(type, request, "Actor", actor);
        Set(type, request, "Button", Button());
        Set(type, request, "ClickEntered", true);
        Set(typeof(EndTurnController), null, "_pending", request);
        Set(typeof(EndTurnController), null, "_clickRequest", request);
        return request;
    }

    private static object Call(string name, params object[] args)
    {
        if (name == "ReadyClickPrefix")
        {
            object[] prefixArgs = [args[0], null];
            object result = typeof(EndTurnController).GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, prefixArgs);
            Call("ReadyClickFinalizer", prefixArgs[1]);
            return result;
        }
        if (name == "CaptureCallback" && args.Length == 1)
        {
            object request = typeof(EndTurnController).GetField("_clickRequest", Fields).GetValue(null);
            args = [args[0], request == null ? null : Get(request, "Button")];
        }
        return (typeof(EndTurnController).GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic) ??
            throw new MissingMethodException(name)).Invoke(null, args);
    }

    private static void Set(Type type, object instance, string name, object value) =>
        (type.GetField(name, Fields) ?? throw new MissingFieldException(type.FullName, name)).SetValue(instance, value);
    private static object Get(object instance, string name) => instance.GetType().GetField(name, Fields).GetValue(instance);
    private static void Check(bool condition, string name)
    {
        if (!condition) throw new InvalidOperationException(name);
        checks++;
        Console.WriteLine("PASS CHECK " + name);
    }
}
