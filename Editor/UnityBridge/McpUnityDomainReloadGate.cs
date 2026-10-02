using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using McpUnity.Utils;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.Compilation;
using UnityEngine;

namespace McpUnity.Unity
{
    /// <summary>
    /// Unity-specific (park, CMP-135 / INC-120): guards bridge requests against the domain reload window.
    ///
    /// Problem: <see cref="CompilationPipeline.compilationFinished"/> marks the end of COMPILATION, not the
    /// end of the domain reload that follows it (~30s in this project). A request that arrives inside that
    /// window is dispatched to the main thread, the main thread freezes for the reload, the managed domain is
    /// torn down together with the awaiting continuation — the client gets neither a response nor an error,
    /// only a socket close without a reason, and hangs until its own timeout.
    ///
    /// The gate does three things:
    ///  1) tracks the reload window on the main thread (compilation events + assembly reload events);
    ///  2) rejects requests that arrive inside the window with an explicit error instead of silence;
    ///  3) answers every still-pending request right before the domain goes down (<see cref="AbortPendingForReload"/>).
    ///
    /// A response cannot cross a domain reload (the server is stopped and the socket closed by
    /// McpUnityServer.OnBeforeAssemblyReload), so callers detect "the bridge is back" by
    /// <see cref="Generation"/>: it survives the reload in SessionState and is incremented after it.
    ///
    /// The state is per-instance on purpose. <see cref="Default"/> is the one wired to the editor events and
    /// used by the live bridge; tests take their own instance, otherwise they would flip the state of the
    /// running bridge and abort the very requests that carry them (seen once: a test run aborted its own
    /// run_tests request with domain_reload_aborted).
    ///
    /// Unity-specific (park, CMP-151 / INC-127): the same gate also carries a main-thread liveness watchdog.
    /// A domain reload is only one way the main thread can stop; a modal dialog (NSAlert runModal) or an
    /// endless loop in somebody's EditorApplication.update freezes it just as hard, and none of the editor
    /// events above fire for those. What all of them share is that <see cref="Tick"/> stops being called,
    /// while the WebSocket receive thread keeps accepting connections — the client sees a request logged and
    /// no answer, and retries (15 times in a row in INC-127). So the watchdog needs no dialog detection: the
    /// absence of ticks IS the proof that there is nowhere to dispatch the request to. See
    /// <see cref="TryRejectMainThreadBlocked"/>.
    /// </summary>
    [InitializeOnLoad]
    public class McpUnityDomainReloadGate
    {
        /// <summary>SessionState key: survives domain reload, resets on editor restart</summary>
        private const string GenerationKey = "McpUnity.DomainGeneration";

        /// <summary>How long the editor must stay quiet before a "reload pending" state is cleared without a reload</summary>
        private const double QuietSecondsToClear = 1.0;

        /// <summary>How long the abort path waits for in-flight responses to be sent naturally</summary>
        private const int AbortGraceMs = 500;

        /// <summary>Hint for the client: a domain reload takes tens of seconds in this project</summary>
        public const int RetryAfterMs = 5000;

        /// <summary>
        /// Unity-specific (park, CMP-151 / INC-127): how long the main thread may go without a tick before it
        /// is declared frozen. Deliberately generous — an unfocused editor throttles its update loop, and a
        /// false "blocked" would make the bridge refuse a perfectly healthy editor. Ten seconds is still two
        /// orders of magnitude below the client timeout (120s) that this replaces, and the state is
        /// self-correcting: one tick and requests are accepted again.
        /// </summary>
        public const double MainThreadStallSeconds = 10.0;

        /// <summary>
        /// Hint for the client after a stall. A frozen main thread is not a wait-and-it-passes condition like a
        /// domain reload — somebody has to dismiss the dialog (or the cause is fixed by CMP-150), so the hint is
        /// long on purpose: retrying sooner cannot help.
        /// </summary>
        public const int MainThreadStallRetryAfterMs = 30000;

        /// <summary>
        /// Unity-specific (park, INC-328): the one in-flight method that legitimately keeps the main thread busy
        /// for minutes. A full EditMode <c>run_tests</c> runs synchronously between update ticks, so a stall with it
        /// in flight is the gate working, not a freeze. Same rule as <c>scripts/unity-editor-restart.sh</c>
        /// («inFlightRequests содержит run_tests — допустимая долгая синхронная операция»).
        /// </summary>
        public const string LongSyncOperationMethod = "run_tests";

        /// <summary>
        /// Unity-specific (park, INC-328): true when the stall is explained by a running test pass. The refusal
        /// is then logged as a Warning: NUnit fails whichever test is running on an unexpected Error, so an Error
        /// here turned a neighbour's status probe into a red gate. Any other in-flight method (a modal opened by
        /// <c>execute_menu_item</c> — INC-127) or none at all still logs an Error.
        /// </summary>
        public static bool IsLongSyncOperationInFlight(string[] inFlight) =>
            inFlight != null && Array.IndexOf(inFlight, LongSyncOperationMethod) >= 0;

        /// <summary>
        /// Wall clock readable from any thread. <see cref="EditorApplication.timeSinceStartup"/> — the clock the
        /// gate ticks with — is main-thread only, and the freeze has to be detected from the socket receive
        /// thread, which is the whole point.
        /// </summary>
        private static readonly System.Diagnostics.Stopwatch MonotonicClock = System.Diagnostics.Stopwatch.StartNew();

        /// <summary>
        /// Tools that stay callable while the editor is merely compiling: recompile_scripts is exactly the tool
        /// a caller uses to wait for a compilation it has just triggered, so refusing it would break the only
        /// synchronization point the bridge has. It is still refused once the reload itself has started.
        /// </summary>
        private static readonly string[] AllowedWhileCompiling = { "recompile_scripts" };

        private static bool _initialized;

        private readonly ConcurrentDictionary<long, PendingRequest> _pendingRequests =
            new ConcurrentDictionary<long, PendingRequest>();

        private readonly bool _persistGeneration;
        private readonly Func<double> _monotonicNow;

        private long _nextTicketId;
        private volatile string _busyReason;
        private volatile bool _reloadInProgress;
        private volatile int _generation;
        private double _quietSince = -1;

        /// <summary>Monotonic seconds of the last main-thread tick. Written on the main thread, read on the
        /// socket thread — always through Interlocked, a plain double read is not atomic.</summary>
        private double _lastMainThreadTick;

        private volatile bool _mainThreadTickSeen;
        private volatile bool _stallLogged;

        static McpUnityDomainReloadGate()
        {
            Initialize();
        }

        public McpUnityDomainReloadGate(bool persistGeneration)
            : this(persistGeneration, null)
        {
        }

        /// <param name="monotonicNow">thread-safe clock in seconds; tests drive the watchdog with a fake one
        /// instead of sleeping for <see cref="MainThreadStallSeconds"/></param>
        public McpUnityDomainReloadGate(bool persistGeneration, Func<double> monotonicNow)
        {
            _persistGeneration = persistGeneration;
            _monotonicNow = monotonicNow ?? (() => MonotonicClock.Elapsed.TotalSeconds);
        }

        /// <summary>The gate of the live bridge — the only instance wired to the editor events</summary>
        public static McpUnityDomainReloadGate Default { get; } = new McpUnityDomainReloadGate(true);

        /// <summary>
        /// Subscribe the default gate to the editor events that delimit the reload window. Idempotent: called
        /// both from the static constructor and from the McpUnityServer constructor (before the server
        /// subscribes its own beforeAssemblyReload handler, so pending requests are answered before the socket
        /// server is stopped).
        /// </summary>
        public static void Initialize()
        {
            if (_initialized || Application.isBatchMode)
            {
                return;
            }

            _initialized = true;
            Default._generation = SessionState.GetInt(GenerationKey, 0);

            CompilationPipeline.compilationStarted -= OnCompilationStarted;
            CompilationPipeline.compilationStarted += OnCompilationStarted;
            CompilationPipeline.compilationFinished -= OnCompilationFinished;
            CompilationPipeline.compilationFinished += OnCompilationFinished;

            AssemblyReloadEvents.beforeAssemblyReload -= OnBeforeAssemblyReload;
            AssemblyReloadEvents.beforeAssemblyReload += OnBeforeAssemblyReload;
            AssemblyReloadEvents.afterAssemblyReload -= OnAfterAssemblyReload;
            AssemblyReloadEvents.afterAssemblyReload += OnAfterAssemblyReload;

            EditorApplication.update -= OnEditorUpdate;
            EditorApplication.update += OnEditorUpdate;
        }

        /// <summary>
        /// Domain generation: incremented after every domain reload, survives the reload itself.
        /// A caller that saw generation N before a recompile knows the bridge is back when it sees N+1.
        /// </summary>
        public int Generation => _generation;

        /// <summary>Why the bridge cannot serve requests right now, or null when it can</summary>
        public string BusyReason => _busyReason;

        /// <summary>True while a compilation / domain reload makes main-thread dispatch unsafe</summary>
        public bool IsBusy => _busyReason != null;

        /// <summary>Number of requests currently in flight (diagnostics and tests)</summary>
        public int PendingCount => _pendingRequests.Count;

        /// <summary>Methods of the requests currently in flight — named in the stall report as the prime
        /// suspect: whatever froze the main thread was most likely started by one of them.</summary>
        public string[] PendingMethods
        {
            get
            {
                var methods = new List<string>();
                foreach (var pair in _pendingRequests)
                {
                    methods.Add(pair.Value.Method ?? "(no method)");
                }

                return methods.ToArray();
            }
        }

        /// <summary>
        /// Seconds since the last main-thread tick, or -1 while no tick has been seen at all. Callable from any
        /// thread.
        /// </summary>
        public double SecondsSinceMainThreadTick
        {
            get
            {
                if (!_mainThreadTickSeen)
                {
                    return -1;
                }

                double last = Interlocked.CompareExchange(ref _lastMainThreadTick, 0.0, 0.0);
                double elapsed = _monotonicNow() - last;
                return elapsed < 0 ? 0 : elapsed;
            }
        }

        /// <summary>
        /// State transitions. Public so that both the editor events and the EditMode tests drive the gate
        /// through the very same entry points — a test never pokes private state.
        /// </summary>
        public void NoteCompilationStarted() => SetBusy("compiling", false);

        public void NoteCompilationFinished() => SetBusy("domain reload pending after compilation", false);

        public void NoteReloadStarted() => SetBusy("domain reload in progress", true);

        /// <summary>Reload finished: the bridge is serving again and the generation moved forward</summary>
        public void NoteReloadFinished()
        {
            _generation = _generation + 1;
            if (_persistGeneration && !Application.isBatchMode)
            {
                SessionState.SetInt(GenerationKey, _generation);
            }

            NoteIdle();
            McpLogger.LogInfo($"Domain reload finished, bridge generation is now {_generation}");
        }

        /// <summary>Clear the busy state without a reload (compilation with errors never reloads the domain)</summary>
        public void NoteIdle()
        {
            _busyReason = null;
            _reloadInProgress = false;
            _quietSince = -1;
        }

        /// <summary>
        /// Called from the socket handler before dispatching to the main thread. When the bridge is inside the
        /// reload window the request cannot be served at all, so it is refused explicitly and immediately.
        /// </summary>
        /// <returns>true when the request must be refused; <paramref name="error"/> holds the response</returns>
        public bool TryRejectDuringReload(string method, out JObject error)
        {
            string reason = _busyReason;
            if (reason == null)
            {
                error = null;
                return false;
            }

            if (!_reloadInProgress && Array.IndexOf(AllowedWhileCompiling, method) >= 0)
            {
                error = null;
                return false;
            }

            McpLogger.LogWarning($"Rejecting '{method}': bridge is busy ({reason}), generation {_generation}");

            error = McpUnitySocketHandler.CreateErrorResponse(
                $"Unity bridge is unavailable: {reason}. The request was not executed — retry after the domain " +
                $"reload completes (bridge generation is {_generation} now; it increments once the reload is done).",
                "domain_reloading");
            error["error"]["retryAfterMs"] = RetryAfterMs;
            error["error"]["domainGeneration"] = _generation;
            return true;
        }

        /// <summary>
        /// Unity-specific (park, CMP-151 / INC-127): the main thread is alive — called from every
        /// <see cref="Tick"/>, i.e. from EditorApplication.update.
        /// </summary>
        public void NoteMainThreadAlive()
        {
            Interlocked.Exchange(ref _lastMainThreadTick, _monotonicNow());
            _mainThreadTickSeen = true;

            if (_stallLogged)
            {
                _stallLogged = false;
                McpLogger.LogInfo("Main thread is ticking again — bridge requests are accepted");
            }
        }

        /// <summary>
        /// Unity-specific (park, CMP-151 / INC-127): called from the socket handler right after
        /// <see cref="TryRejectDuringReload"/>. Refuses a request that has nowhere to run: the main thread has
        /// not ticked for <see cref="MainThreadStallSeconds"/>, so dispatching it would only reproduce the
        /// INC-127 pattern — request logged, no answer, client waits out its 120s timeout and retries.
        ///
        /// Deliberately does NOT try to recognize the dialog: the missing ticks cover the whole class (modal
        /// dialog, endless loop in an update handler, any other freeze), and a text detector would only cover
        /// the one case someone happened to think of.
        ///
        /// Honest boundary: this does not unfreeze the editor (that is CMP-150). It turns a silent hang into a
        /// diagnosis and stops the pointless retries.
        /// </summary>
        /// <returns>true when the request must be refused; <paramref name="error"/> holds the response</returns>
        public bool TryRejectMainThreadBlocked(string method, out JObject error)
        {
            double stalled = SecondsSinceMainThreadTick;

            // -1 (no tick seen yet) lands here too, on purpose: a gate that has never ticked knows nothing
            // about the main thread, and refusing on ignorance would kill the bridge right after a domain
            // reload — before the first EditorApplication.update of the new domain.
            if (stalled < MainThreadStallSeconds)
            {
                error = null;
                return false;
            }

            string[] inFlight = PendingMethods;
            string culprit = inFlight.Length > 0
                ? $" Bridge request(s) dispatched before the freeze and still in flight: {string.Join(", ", inFlight)} — " +
                  "the freeze is most likely theirs (a modal dialog they opened, or a long synchronous operation)."
                : string.Empty;

            if (!_stallLogged)
            {
                _stallLogged = true;
                string stallMessage = $"Main thread has not ticked for {stalled:F1}s — refusing bridge requests " +
                                      $"('{method}' is the first one refused).{culprit}";
                if (IsLongSyncOperationInFlight(inFlight))
                {
                    McpLogger.LogWarning(stallMessage);
                }
                else
                {
                    McpLogger.LogError(stallMessage);
                }
            }

            error = McpUnitySocketHandler.CreateErrorResponse(
                $"Unity main thread has not ticked for {stalled:F1}s (threshold {MainThreadStallSeconds:F0}s): the editor " +
                $"is frozen and '{method}' was NOT dispatched — nothing ran." + culprit +
                " The usual cause in this project is a modal dialog on the main thread; confirm with " +
                "`sample <unity-pid> 1` and look for `NSAlert runModal` (INC-127), the cause itself is fixed by " +
                "CMP-150. Retrying will not help until the editor is unblocked: no tick, no response.",
                "main_thread_blocked");
            error["error"]["stalledSeconds"] = Math.Round(stalled, 1);
            error["error"]["thresholdSeconds"] = MainThreadStallSeconds;
            error["error"]["inFlightRequests"] = new JArray(inFlight);
            error["error"]["retryAfterMs"] = MainThreadStallRetryAfterMs;
            error["error"]["domainGeneration"] = _generation;
            return true;
        }

        /// <summary>
        /// Register an in-flight request so it can be answered explicitly if the domain goes down while it runs.
        /// </summary>
        /// <param name="method">tool/resource name, for logs</param>
        /// <param name="respond">sends the response to the client; called at most once</param>
        public PendingRequest Register(string method, Action<JObject> respond)
        {
            var ticket = new PendingRequest(Interlocked.Increment(ref _nextTicketId), method, respond);
            _pendingRequests[ticket.Id] = ticket;
            return ticket;
        }

        /// <summary>Forget a request that has been answered by its own execution path</summary>
        public void Unregister(PendingRequest ticket)
        {
            if (ticket != null)
            {
                _pendingRequests.TryRemove(ticket.Id, out _);
            }
        }

        /// <summary>
        /// Answer every in-flight request before the managed domain is torn down. Runs on the main thread from
        /// beforeAssemblyReload, before McpUnityServer stops the socket server, so the responses still reach
        /// the client. Requests that manage to answer on their own within the grace period keep their real
        /// result — <see cref="PendingRequest.TryClaim"/> makes the two paths mutually exclusive.
        /// </summary>
        public void AbortPendingForReload()
        {
            if (_pendingRequests.Count == 0)
            {
                return;
            }

            McpLogger.LogWarning($"Domain reload starting with {_pendingRequests.Count} in-flight bridge request(s): " +
                                 $"waiting up to {AbortGraceMs}ms for them to answer");

            var waited = 0;
            while (_pendingRequests.Count > 0 && waited < AbortGraceMs)
            {
                Thread.Sleep(25);
                waited += 25;
            }

            foreach (var pair in _pendingRequests)
            {
                PendingRequest ticket = pair.Value;
                if (!ticket.TryClaim())
                {
                    continue;
                }

                _pendingRequests.TryRemove(ticket.Id, out _);

                JObject error = McpUnitySocketHandler.CreateErrorResponse(
                    $"Request '{ticket.Method}' was aborted by a Unity domain reload before it could run. " +
                    $"Nothing was executed — retry after the reload completes (bridge generation is {_generation} " +
                    "now; it increments once the reload is done).",
                    "domain_reload_aborted");
                error["error"]["retryAfterMs"] = RetryAfterMs;
                error["error"]["domainGeneration"] = _generation;

                try
                {
                    ticket.Respond(error);
                    McpLogger.LogWarning($"Aborted in-flight request '{ticket.Method}' with an explicit error (domain reload)");
                }
                catch (Exception ex)
                {
                    McpLogger.LogError($"Failed to send domain reload abort for '{ticket.Method}': {ex.Message}");
                }
            }
        }

        /// <summary>
        /// Main-thread watchdog. Keeps the busy flag raised while the editor compiles or imports, and lowers it
        /// when a "reload pending" state turns out not to end in a reload (compilation with errors keeps the old
        /// domain, so afterAssemblyReload never fires and the gate would stay closed forever).
        /// </summary>
        public void Tick(bool editorIsCompiling, bool editorIsUpdating, double now)
        {
            // Первым делом и до любых ранних выходов: сам факт вызова Tick — это и есть доказательство, что
            // главный поток жив, и оно не должно зависеть от того, компилирует редактор или простаивает
            // (CMP-151).
            NoteMainThreadAlive();

            if (editorIsCompiling || editorIsUpdating)
            {
                SetBusy(editorIsCompiling ? "compiling" : "importing assets", false);
                return;
            }

            if (_busyReason == null)
            {
                return;
            }

            if (_quietSince < 0)
            {
                _quietSince = now;
                return;
            }

            if (now - _quietSince >= QuietSecondsToClear)
            {
                McpLogger.LogInfo($"Bridge gate reopened without a domain reload (was: {_busyReason})");
                NoteIdle();
            }
        }

        private void SetBusy(string reason, bool reloadInProgress)
        {
            _busyReason = reason;
            if (reloadInProgress)
            {
                _reloadInProgress = true;
            }

            _quietSince = -1;
        }

        private static void OnCompilationStarted(object _) => Default.NoteCompilationStarted();

        private static void OnCompilationFinished(object _) => Default.NoteCompilationFinished();

        private static void OnBeforeAssemblyReload()
        {
            Default.NoteReloadStarted();
            Default.AbortPendingForReload();
        }

        private static void OnAfterAssemblyReload() => Default.NoteReloadFinished();

        private static void OnEditorUpdate()
        {
            Default.Tick(EditorApplication.isCompiling, EditorApplication.isUpdating, EditorApplication.timeSinceStartup);
        }

        /// <summary>
        /// An in-flight bridge request. Exactly one of the two paths — normal completion or domain reload abort —
        /// gets to send the response; the loser silently drops its own.
        /// </summary>
        public class PendingRequest
        {
            private int _claimed;

            public PendingRequest(long id, string method, Action<JObject> respond)
            {
                Id = id;
                Method = method;
                Respond = respond;
            }

            public long Id { get; }

            public string Method { get; }

            public Action<JObject> Respond { get; }

            /// <summary>True for the first caller only</summary>
            public bool TryClaim() => Interlocked.Exchange(ref _claimed, 1) == 0;
        }
    }
}
