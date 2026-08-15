using System;
using System.Collections.Concurrent;
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
        /// Tools that stay callable while the editor is merely compiling: recompile_scripts is exactly the tool
        /// a caller uses to wait for a compilation it has just triggered, so refusing it would break the only
        /// synchronization point the bridge has. It is still refused once the reload itself has started.
        /// </summary>
        private static readonly string[] AllowedWhileCompiling = { "recompile_scripts" };

        private static bool _initialized;

        private readonly ConcurrentDictionary<long, PendingRequest> _pendingRequests =
            new ConcurrentDictionary<long, PendingRequest>();

        private readonly bool _persistGeneration;

        private long _nextTicketId;
        private volatile string _busyReason;
        private volatile bool _reloadInProgress;
        private volatile int _generation;
        private double _quietSince = -1;

        static McpUnityDomainReloadGate()
        {
            Initialize();
        }

        public McpUnityDomainReloadGate(bool persistGeneration)
        {
            _persistGeneration = persistGeneration;
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
