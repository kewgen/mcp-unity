using McpUnity.Unity;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace McpUnity.Tests
{
    /// <summary>
    /// Unity-specific (парк, CMP-135 / INC-120): гейт domain reload.
    ///
    /// Регрессия, которую фиксируют тесты: запрос, отправленный в окно перезагрузки домена, раньше не получал
    /// ни ответа, ни ошибки — главный поток заморожен, домен вместе с продолжением сносится, клиент висит до
    /// собственного таймаута. Проверяется ровно то поведение, которого не хватало: явный отказ на входе и
    /// явный ответ уже принятым запросам перед сносом домена.
    /// </summary>
    public class DomainReloadGateTests
    {
        private McpUnityDomainReloadGate _gate;

        [SetUp]
        public void SetUp()
        {
            // Свой экземпляр, а не McpUnityDomainReloadGate.Default: тест, дёргающий гейт живого моста,
            // обрывает реальные запросы — в том числе тот run_tests, которым сам и запущен (проверено на
            // первой редакции этих тестов: собственный прогон получил domain_reload_aborted).
            _gate = new McpUnityDomainReloadGate(false);
        }

        [Test]
        public void IdleBridge_AcceptsRequests()
        {
            Assert.IsFalse(_gate.IsBusy, "гейт должен быть открыт на спокойном редакторе");
            Assert.IsFalse(_gate.TryRejectDuringReload("run_tests", out JObject error));
            Assert.IsNull(error);
        }

        [Test]
        public void AfterCompilation_RequestIsRefusedExplicitly()
        {
            // compilationFinished — это конец компиляции, дальше идёт domain reload: запрос выполнить нельзя.
            _gate.NoteCompilationFinished();

            Assert.IsTrue(_gate.TryRejectDuringReload("run_tests", out JObject error),
                "run_tests в окне domain reload обязан получить отказ, а не тишину");
            Assert.AreEqual("domain_reloading", error["error"]["type"].ToString());
            Assert.AreEqual(McpUnityDomainReloadGate.RetryAfterMs, error["error"]["retryAfterMs"].ToObject<int>());
            Assert.AreEqual(_gate.Generation, error["error"]["domainGeneration"].ToObject<int>());
        }

        [Test]
        public void WhileCompiling_RecompileScriptsStaysCallable()
        {
            // recompile_scripts — единственная точка синхронизации вызывающего с компиляцией: отказывать в ней,
            // пока идёт та самая компиляция, значит сломать штатный сценарий «поправил файлы → дождался сборки».
            _gate.NoteCompilationStarted();
            Assert.IsFalse(_gate.TryRejectDuringReload("recompile_scripts", out JObject allowed));
            Assert.IsNull(allowed);

            // А когда домен уже перезагружается — отказ получают все, включая recompile_scripts.
            _gate.NoteReloadStarted();
            Assert.IsTrue(_gate.TryRejectDuringReload("recompile_scripts", out JObject refused));
            Assert.AreEqual("domain_reloading", refused["error"]["type"].ToString());
        }

        [Test]
        public void InFlightRequest_IsAnsweredBeforeDomainGoesDown()
        {
            JObject sent = null;
            McpUnityDomainReloadGate.PendingRequest ticket =
                _gate.Register("run_tests", response => sent = response);

            Assert.AreEqual(1, _gate.PendingCount);

            _gate.NoteReloadStarted();
            _gate.AbortPendingForReload();

            Assert.IsNotNull(sent, "принятый запрос обязан получить ответ до сноса домена");
            Assert.AreEqual("domain_reload_aborted", sent["error"]["type"].ToString());
            Assert.AreEqual(McpUnityDomainReloadGate.RetryAfterMs, sent["error"]["retryAfterMs"].ToObject<int>());
            Assert.AreEqual(0, _gate.PendingCount);
            Assert.IsFalse(ticket.TryClaim(), "ответ уже отправлен гейтом — штатный путь не должен слать второй");
        }

        [Test]
        public void RequestAnsweredNormally_IsNotAnsweredTwiceByTheGate()
        {
            JObject sent = null;
            McpUnityDomainReloadGate.PendingRequest ticket =
                _gate.Register("get_scene_info", response => sent = response);

            // Штатный путь успел ответить первым.
            Assert.IsTrue(ticket.TryClaim());
            _gate.Unregister(ticket);

            _gate.NoteReloadStarted();
            _gate.AbortPendingForReload();

            Assert.IsNull(sent, "гейт не должен слать второй ответ на уже отвеченный запрос");
            Assert.AreEqual(0, _gate.PendingCount);
        }

        [Test]
        public void Generation_IncrementsAfterReload()
        {
            int before = _gate.Generation;

            _gate.NoteReloadStarted();
            Assert.IsTrue(_gate.IsBusy);

            _gate.NoteReloadFinished();

            Assert.AreEqual(before + 1, _gate.Generation,
                "по поколению вызывающий отличает «мост вернулся после reload» от «мост ещё не уходил»");
            Assert.IsFalse(_gate.IsBusy);
        }
    }
}
