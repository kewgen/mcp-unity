using System.Text.RegularExpressions;
using McpUnity.Unity;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace McpUnity.Tests
{
    /// <summary>
    /// Unity-specific (парк, CMP-151 / INC-127): сторож живости главного потока.
    ///
    /// Регрессия, которую фиксируют тесты: при заморозке главного потока (модалка `NSAlert runModal`,
    /// бесконечный цикл в чьём-то update) приём WebSocket остаётся живым — соединение принимается, запрос
    /// логируется, ответа нет. Клиент ждёт свой таймаут (120 с) и повторяет; в INC-127 записаны 15 попыток
    /// подряд. Проверяется ровно то поведение, которого не хватало: отказ по факту отсутствия тиков,
    /// снятый с потока приёма и потому не требующий главного потока.
    ///
    /// Часы у гейта подменены: ждать <see cref="McpUnityDomainReloadGate.MainThreadStallSeconds"/> секунд
    /// в тесте незачем, а Thread.Sleep сделал бы прогон медленным и плавающим.
    /// </summary>
    public class MainThreadStallGateTests
    {
        private double _now;
        private McpUnityDomainReloadGate _gate;

        [SetUp]
        public void SetUp()
        {
            _now = 1000.0;

            // Свой экземпляр, а не McpUnityDomainReloadGate.Default — по той же причине, что и в
            // DomainReloadGateTests: тест, дёргающий гейт живого моста, обрывает реальные запросы,
            // включая тот run_tests, которым сам и запущен.
            _gate = new McpUnityDomainReloadGate(false, () => _now);
        }

        /// <summary>Тик главного потока — ровно то, что делает EditorApplication.update через гейт</summary>
        private void MainThreadTick() => _gate.Tick(false, false, _now);

        /// <summary>
        /// Заморозка главного потока обязана быть громкой: она пишется в консоль как Error, один раз на
        /// заморозку. NUnit валит тест на незаявленный Error — заявляем и заодно фиксируем формат строки,
        /// по которой этот класс отказов ищут в Editor.log.
        /// </summary>
        private static void ExpectStallLogged() =>
            LogAssert.Expect(LogType.Error, new Regex(@"Main thread has not ticked for .+refusing bridge requests"));

        [Test]
        public void NoTickSeenYet_DoesNotReject()
        {
            // Свежий домен: гейт создан, EditorApplication.update ещё ни разу не вызвался. Отказ «по незнанию»
            // положил бы мост сразу после каждой перезагрузки домена.
            Assert.AreEqual(-1.0, _gate.SecondsSinceMainThreadTick, 1e-9,
                "до первого тика сторож не знает о главном потоке ничего");

            _now += 10 * McpUnityDomainReloadGate.MainThreadStallSeconds;

            Assert.IsFalse(_gate.TryRejectMainThreadBlocked("get_scene_info", out JObject error),
                "без единого тика отказывать нельзя — это не доказательство заморозки");
            Assert.IsNull(error);
        }

        [Test]
        public void RecentTick_DoesNotReject()
        {
            MainThreadTick();
            _now += McpUnityDomainReloadGate.MainThreadStallSeconds - 0.5;

            Assert.AreEqual(McpUnityDomainReloadGate.MainThreadStallSeconds - 0.5,
                _gate.SecondsSinceMainThreadTick, 1e-6);
            Assert.IsFalse(_gate.TryRejectMainThreadBlocked("get_scene_info", out JObject error),
                "живой редактор обязан обслуживать запросы вплоть до самого порога");
            Assert.IsNull(error);
        }

        [Test]
        public void TicksStopped_RequestIsRefusedExplicitly()
        {
            MainThreadTick();
            _now += McpUnityDomainReloadGate.MainThreadStallSeconds + 2.5;

            ExpectStallLogged();
            Assert.IsTrue(_gate.TryRejectMainThreadBlocked("get_console_logs", out JObject error),
                "запрос при замороженном главном потоке обязан получить отказ, а не тишину");

            JToken body = error["error"];
            Assert.AreEqual("main_thread_blocked", body["type"].ToString());
            Assert.AreEqual(McpUnityDomainReloadGate.MainThreadStallSeconds + 2.5,
                body["stalledSeconds"].ToObject<double>(), 0.05,
                "длительность заморозки — главное, чего не хватало агенту в INC-127");
            Assert.AreEqual(McpUnityDomainReloadGate.MainThreadStallSeconds,
                body["thresholdSeconds"].ToObject<double>(), 1e-9);
            Assert.AreEqual(McpUnityDomainReloadGate.MainThreadStallRetryAfterMs,
                body["retryAfterMs"].ToObject<int>());
            Assert.AreEqual(_gate.Generation, body["domainGeneration"].ToObject<int>());

            string message = body["message"].ToString();
            StringAssert.Contains("get_console_logs", message, "в отказе должен быть назван отвергнутый метод");
            StringAssert.Contains("NSAlert runModal", message, "подсказка «как подтвердить» — часть отказа");
            StringAssert.Contains("CMP-150", message, "отказ обязан указывать, чем чинится причина");
        }

        [Test]
        public void InFlightRequest_IsNamedAsTheLikelySuspect()
        {
            // INC-127: заморозку вызвал сам вызов execute_menu_item, открывший модалку. Тот запрос всё ещё
            // «в полёте», и назвать его — самое полезное, что отказ может сообщить следующему запросу.
            _gate.Register("execute_menu_item", _ => { });
            MainThreadTick();
            _now += McpUnityDomainReloadGate.MainThreadStallSeconds + 1;

            ExpectStallLogged();
            Assert.IsTrue(_gate.TryRejectMainThreadBlocked("get_console_logs", out JObject error));

            var inFlight = error["error"]["inFlightRequests"].ToObject<string[]>();
            CollectionAssert.AreEqual(new[] { "execute_menu_item" }, inFlight);
            StringAssert.Contains("execute_menu_item", error["error"]["message"].ToString());
        }

        [Test]
        public void ModalRequestInFlight_StallStaysAnError()
        {
            // INC-127 остаётся громкой: модалку открывает сам запрос моста, поэтому «в полёте что-то есть» —
            // как раз та заморозка, ради которой Error заводился. Понижение до Warning — только для run_tests.
            _gate.Register("execute_menu_item", _ => { });
            MainThreadTick();
            _now += McpUnityDomainReloadGate.MainThreadStallSeconds + 1;

            ExpectStallLogged();
            Assert.IsTrue(_gate.TryRejectMainThreadBlocked("get_console_logs", out _));
            LogAssert.NoUnexpectedReceived();
        }

        [Test]
        public void RunTestsInFlight_StallIsWarningNotError()
        {
            // INC-328: полный EditMode run_tests держит главный поток синхронно — это занятость гейта, а не
            // заморозка. Отказ чужому probe остаётся, но в консоль идёт Warning: незаявленный Error NUnit
            // засчитал бы идущему тесту (02.10: UIINT014 и красный гейт от --check-only соседа).
            _gate.Register("run_tests", _ => { });
            MainThreadTick();
            _now += McpUnityDomainReloadGate.MainThreadStallSeconds + 1;

            LogAssert.Expect(LogType.Warning, new Regex(@"Main thread has not ticked for .+refusing bridge requests"));
            Assert.IsTrue(_gate.TryRejectMainThreadBlocked("get_console_logs", out JObject error),
                "отказ клиенту остаётся: запрос всё равно некуда выполнить");
            Assert.AreEqual("main_thread_blocked", error["error"]["type"].ToString());
            LogAssert.NoUnexpectedReceived();
        }

        [Test]
        public void RunTestsInFlight_RefusalDoesNotFailTheRunningTest()
        {
            // Тот же сценарий глазами идущего теста: он о мосте ничего не знает и LogAssert.Expect не ставит.
            // Отказ соседу не должен сделать его красным — ровно так упал UIINT014.
            _gate.Register("run_tests", _ => { });
            _gate.Register("get_console_logs", _ => { });
            MainThreadTick();
            _now += McpUnityDomainReloadGate.MainThreadStallSeconds + 1;

            Assert.IsTrue(_gate.TryRejectMainThreadBlocked("get_console_logs", out _));
        }

        [Test]
        public void TickResumed_StopsRejecting()
        {
            MainThreadTick();
            _now += McpUnityDomainReloadGate.MainThreadStallSeconds + 1;
            ExpectStallLogged();
            Assert.IsTrue(_gate.TryRejectMainThreadBlocked("get_scene_info", out _));

            // Единственная страховка от ложного срабатывания: состояние самовосстанавливается с первым же
            // тиком. Иначе неверный порог означал бы навсегда мёртвый мост.
            MainThreadTick();

            Assert.IsFalse(_gate.TryRejectMainThreadBlocked("get_scene_info", out JObject error),
                "один тик — и мост снова принимает запросы");
            Assert.IsNull(error);
        }

        [Test]
        public void CompilingEditor_StillCountsAsAlive()
        {
            // Компиляция и импорт ассетов — занятость, а не заморозка: update при них продолжает вызываться.
            // Отметка живости обязана стоять до ранних выходов Tick, иначе долгая компиляция выглядела бы как
            // мёртвый главный поток и получала бы неверный диагноз вместо domain_reloading.
            _gate.Tick(true, false, _now);
            _now += McpUnityDomainReloadGate.MainThreadStallSeconds + 1;
            _gate.Tick(true, false, _now);

            Assert.IsFalse(_gate.TryRejectMainThreadBlocked("get_scene_info", out JObject error),
                "компилирующий редактор жив — диагноз тут даёт гейт domain reload, а не сторож");
            Assert.IsNull(error);
            Assert.IsTrue(_gate.IsBusy, "занятость компиляцией при этом никуда не делась");
        }
    }
}
