using GHelper.Battery;
using GHelper.Display;
using GHelper.Input;
using GHelper.Mode;
using GHelper.USB;
using System.Collections.Specialized;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Web;

namespace GHelper.Helpers
{
    // 本地自动化 HTTP API，只绑定 127.0.0.1。端口取自 config 的 "api_port"（默认 5110，设为 0 关闭）
    public static class ApiServer
    {
        static TcpListener? listener;
        static volatile bool stopped;
        static long lastSensors;

        static readonly JsonSerializerOptions jsonOptions = new() { NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals };

        // 开放给 /action 的快捷键动作（InputDispatcher.RunAction）；"custom" 会执行用户配置的任意命令，故不开放
        static readonly Dictionary<string, string> actions = new()
        {
            { "micmute", "toggle microphone mute" },
            { "mute", "toggle speaker mute" },
            { "volume_up", "volume up" },
            { "volume_down", "volume down" },
            { "play", "media play/pause" },
            { "brightness_up", "screen brightness up" },
            { "brightness_down", "screen brightness down" },
            { "backlight_up", "keyboard backlight up" },
            { "backlight_down", "keyboard backlight down" },
            { "screenpad_up", "ScreenPad brightness up" },
            { "screenpad_down", "ScreenPad brightness down" },
            { "fnlock", "toggle Fn lock" },
            { "touchpad", "toggle touchpad" },
            { "touchscreen", "toggle touchscreen" },
            { "aura", "cycle keyboard lighting mode" },
            { "visual", "cycle display color mode" },
            { "performance", "cycle performance mode" },
            { "miniled", "toggle MiniLED mode" },
            { "overlay", "toggle hardware overlay" },
            { "ghelper", "show/hide G-Helper window" },
            { "controller", "toggle Ally controller mode" },
            { "screenshot", "press PrintScreen" },
            { "calculator", "open calculator" },
            { "screen", "turn display off (user must wake it)" },
            { "lock", "lock Windows session (user must sign in again)" },
        };

        public static void Start()
        {
            int port = AppConfig.Get("api_port", 5110);
            if (port <= 0 || port > 65535) return;
            new Thread(() => Run(port)) { IsBackground = true, Name = "ApiServer" }.Start();
        }

        public static void Stop()
        {
            stopped = true;
            try { listener?.Stop(); } catch { }
        }

        static void Run(int port)
        {
            // 旧实例被 CheckAlreadyRunning 关掉后可能还占着端口，重试几次
            for (int i = 0; listener is null; i++)
            {
                try
                {
                    var l = new TcpListener(IPAddress.Loopback, port) { ExclusiveAddressUse = true };
                    l.Start();
                    listener = l;
                }
                catch (SocketException ex)
                {
                    if (i >= 4 || stopped)
                    {
                        Logger.WriteLine($"API: can't bind 127.0.0.1:{port}: {ex.Message}");
                        return;
                    }
                    Thread.Sleep(1000);
                }
            }

            if (stopped) { listener.Stop(); return; }
            Logger.WriteLine($"API: listening on 127.0.0.1:{port}");

            // ponytail: 单线程串行处理请求；命令都是亚秒级，需要并发长操作时再改成每连接一个 Task + 锁
            while (!stopped)
            {
                TcpClient client;
                try { client = listener.AcceptTcpClient(); }
                catch { break; }

                using (client)
                {
                    try { Handle(client, port); }
                    catch (Exception ex) { Logger.WriteLine("API: " + ex.Message); }
                }
            }
        }

        static void Handle(TcpClient client, int port)
        {
            client.ReceiveTimeout = 3000;
            client.SendTimeout = 3000;
            var stream = client.GetStream();

            var buf = new byte[8192];
            int len = 0, end;
            while ((end = buf.AsSpan(0, len).IndexOf("\r\n\r\n"u8)) < 0)
            {
                if (len == buf.Length) { Respond(stream, 431, Error("headers too large")); return; }
                int n = stream.Read(buf, len, buf.Length - len);
                if (n == 0) return;
                len += n;
            }

            var lines = Encoding.ASCII.GetString(buf, 0, end).Split("\r\n");
            var request = lines[0].Split(' ');
            if (request.Length != 3) { Respond(stream, 400, Error("bad request")); return; }

            string? host = null;
            bool origin = false;
            int contentLength = 0;
            foreach (var line in lines.Skip(1))
            {
                int colon = line.IndexOf(':');
                if (colon <= 0) continue;
                var name = line[..colon].Trim();
                var value = line[(colon + 1)..].Trim();
                if (name.Equals("Host", StringComparison.OrdinalIgnoreCase)) host = value;
                else if (name.Equals("Origin", StringComparison.OrdinalIgnoreCase)) origin = true;
                else if (name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase)) int.TryParse(value, out contentLength);
            }

            // 丢弃请求体（参数只走 query string），避免关闭时残留数据触发 RST
            int pending = Math.Min(contentLength, 65536) - (len - end - 4);
            while (pending > 0)
            {
                int n = stream.Read(buf, 0, Math.Min(pending, buf.Length));
                if (n == 0) break;
                pending -= n;
            }

            // 带 Origin 的是浏览器网页发起的请求（CSRF）；Host 不对说明可能是 DNS rebinding
            if (origin || (host != $"127.0.0.1:{port}" && host != $"localhost:{port}"))
            {
                Respond(stream, 403, Error("forbidden"));
                return;
            }

            string method = request[0];
            string target = request[1];
            int q = target.IndexOf('?');
            string path = (q < 0 ? target : target[..q]).TrimEnd('/').ToLowerInvariant();
            var query = HttpUtility.ParseQueryString(q < 0 ? "" : target[(q + 1)..]);

            if (method != "GET") Logger.WriteLine($"API: {method} {target}");

            (int status, object body) result;
            try { result = Route(method, path, query); }
            catch (Exception ex)
            {
                Logger.WriteLine($"API: {target} failed: {ex}");
                result = (500, Error(ex.Message));
            }
            Respond(stream, result.status, result.body);
        }

        static (int, object) Route(string method, string path, NameValueCollection query)
        {
            if (path is "" or "/status")
            {
                if (method != "GET") return (405, Error("use GET"));
                return (200, path == "" ? Help() : Status());
            }

            if (path is not ("/mode" or "/gpu" or "/charge" or "/screen" or "/backlight" or "/aura" or "/action"))
                return (404, Error("not found"));

            if (method != "POST") return (405, Error("use POST"));

            string? value = query["value"]?.ToLowerInvariant();

            switch (path)
            {
                case "/mode":
                    int mode = value switch
                    {
                        "silent" => AsusACPI.PerformanceSilent,
                        "balanced" => AsusACPI.PerformanceBalanced,
                        "turbo" => AsusACPI.PerformanceTurbo,
                        _ => int.TryParse(value, out int m) ? m : -1
                    };
                    if (!Modes.Exists(mode)) return Bad("value: silent | balanced | turbo | <mode id>");
                    Program.modeControl.SetPerformanceMode(mode, true);
                    return Ok();

                case "/gpu":
                    if (AppConfig.NoGpu() || Program.acpi.DeviceGet(AsusACPI.GPUEco) < 0) return Bad("GPU modes not supported");
                    // 从 Ultimate 切出要弹确认框并重启，只允许在 UI 里操作
                    if (AppConfig.Get("gpu_mode") == AsusACPI.GPUModeUltimate) return (409, Error("Ultimate mode is active, change it in G-Helper UI"));
                    // Eco（Optimized 在电池供电时也会切 Eco）会结束所有占用独显的程序，可能包括调用方自己
                    if (value is "eco" or "optimized" && query["force"] != "1")
                        return Bad("eco/optimized closes every app using the dGPU (possibly the caller); repeat with &force=1 to confirm");
                    var gpu = Program.settingsForm.gpuControl;
                    switch (value)
                    {
                        case "eco": gpu.SetGPUMode(AsusACPI.GPUModeEco); break;
                        case "standard": gpu.SetGPUMode(AsusACPI.GPUModeStandard); break;
                        case "optimized":
                            AppConfig.Set("gpu_auto", 1);
                            Program.settingsForm.VisualiseGPUMode();
                            // AutoGPUMode 可能弹确认框，放到后台免得卡住 API 线程
                            Task.Run(() => gpu.AutoGPUMode(true));
                            break;
                        default: return Bad("value: eco | standard | optimized");
                    }
                    return Ok();

                case "/charge":
                    if (!int.TryParse(value, out int limit) || limit < 40 || limit > 100) return Bad("value: 40..100");
                    BatteryControl.SetBatteryChargeLimit(limit);
                    return Ok();

                case "/screen":
                    string? hz = query["hz"]?.ToLowerInvariant();
                    string? od = query["overdrive"];
                    int frequency = hz switch
                    {
                        null => -1,
                        "max" => ScreenControl.MAX_REFRESH,
                        "min" => ScreenControl.MIN_RATE,
                        _ => int.TryParse(hz, out int f) && f > 0 ? f : -2
                    };
                    int overdrive = od switch { null => -1, "0" => 0, "1" => 1, _ => -2 };
                    if (frequency == -2 || overdrive == -2 || (frequency < 0 && overdrive < 0))
                        return Bad("hz: max | min | <Hz>, overdrive: 0 | 1");
                    if (frequency > 0) ScreenControl.SetAutoRefresh(0);
                    ScreenControl.SetScreen(frequency, overdrive);
                    return Ok();

                case "/backlight":
                    int max = AppConfig.Get("max_brightness", 3);
                    if (!int.TryParse(value, out int backlight) || backlight < 0 || backlight > max) return Bad($"value: 0..{max}");
                    InputDispatcher.SetBacklight(backlight - InputDispatcher.GetBacklight());
                    return Ok();

                case "/aura":
                    if (AppConfig.NoAura()) return Bad("Aura not supported");
                    if (!int.TryParse(value, out int aura) || !Aura.GetModes().ContainsKey((AuraMode)aura)) return Bad("value: <aura mode id>, see /status");
                    Program.settingsForm.BeginInvoke(() => Program.settingsForm.SetAuraMode(aura));
                    return Ok();

                case "/action":
                    if (value is null || !actions.ContainsKey(value)) return Bad("value: one of the actions listed in GET /");
                    InputDispatcher.RunAction(value);
                    return Ok();
            }

            return (404, Error("not found"));
        }

        // 给 agent 的自描述：调一次 GET / 就知道全部接口、参数和副作用
        static object Help() => new
        {
            name = "G-Helper local API",
            usage = "All parameters go in the query string. Reads are GET, changes are POST. Responses are JSON; errors are {\"error\": \"...\"} with HTTP 4xx/5xx. Changes apply asynchronously, re-check GET /status after ~1-2 s.",
            endpoints = new object[]
            {
                new { method = "GET", path = "/status", effect = "performance mode, GPU mode, battery and charge limit, temperatures, fans, screen, keyboard backlight and lighting; also lists valid mode ids and aura mode ids" },
                new { method = "POST", path = "/mode", @params = "value=silent|balanced|turbo|<mode id from /status modes>", effect = "set performance mode" },
                new { method = "POST", path = "/gpu", @params = "value=standard | value=eco&force=1 | value=optimized&force=1", effect = "set GPU mode. eco/optimized close every app using the dGPU, which may include the caller, so force=1 is required. Ultimate is not available via API (needs reboot); while Ultimate is active this returns 409" },
                new { method = "POST", path = "/charge", @params = "value=40..100", effect = "set battery charge limit (some models round to 60/80/100)" },
                new { method = "POST", path = "/screen", @params = "hz=max|min|<Hz> and/or overdrive=0|1", effect = "set laptop screen refresh rate / overdrive, disables auto refresh" },
                new { method = "POST", path = "/backlight", @params = "value=0..maxBacklight", effect = "set keyboard backlight level" },
                new { method = "POST", path = "/aura", @params = "value=<aura mode id from /status keyboard.auraModes>", effect = "set keyboard lighting mode" },
                new { method = "POST", path = "/action", @params = "value=<action>", effect = "run a G-Helper hotkey action, see actions" },
            },
            actions
        };

        static object Status()
        {
            if (Environment.TickCount64 - lastSensors > 2000)
            {
                lastSensors = Environment.TickCount64;
                HardwareControl.ReadSensors();
            }

            var power = SystemInformation.PowerStatus;

            return new
            {
                mode = new { id = Modes.GetCurrent(), name = Modes.GetCurrentName() },
                modes = Modes.GetDictonary(),
                gpu = new
                {
                    mode = AppConfig.Get("gpu_mode") switch
                    {
                        AsusACPI.GPUModeEco => "eco",
                        AsusACPI.GPUModeStandard => "standard",
                        AsusACPI.GPUModeUltimate => "ultimate",
                        _ => "unknown"
                    },
                    optimized = AppConfig.Is("gpu_auto")
                },
                battery = new
                {
                    percent = (int)Math.Round(power.BatteryLifePercent * 100),
                    plugged = power.PowerLineStatus == PowerLineStatus.Online,
                    chargeLimit = AppConfig.Get("charge_limit"),
                    chargeFull = BatteryControl.chargeFull,
                    rateW = HardwareControl.batteryRate,
                    health = HardwareControl.batteryHealth
                },
                sensors = new
                {
                    cpuTemp = HardwareControl.cpuTemp,
                    gpuTemp = HardwareControl.gpuTemp,
                    cpuFan = HardwareControl.cpuFan,
                    gpuFan = HardwareControl.gpuFan,
                    midFan = HardwareControl.midFan
                },
                screen = new
                {
                    hz = AppConfig.Get("frequency"),
                    overdrive = AppConfig.Get("overdrive"),
                    auto = AppConfig.Is("screen_auto")
                },
                keyboard = new
                {
                    backlight = InputDispatcher.GetBacklight(),
                    maxBacklight = AppConfig.Get("max_brightness", 3),
                    aura = (int)Aura.Mode,
                    auraModes = Aura.GetModes().ToDictionary(m => (int)m.Key, m => m.Value)
                }
            };
        }

        static (int, object) Ok() => (200, new { ok = true });
        static (int, object) Bad(string message) => (400, Error(message));
        static object Error(string message) => new { error = message };

        static void Respond(NetworkStream stream, int status, object body)
        {
            string reason = status switch
            {
                200 => "OK",
                400 => "Bad Request",
                403 => "Forbidden",
                404 => "Not Found",
                405 => "Method Not Allowed",
                409 => "Conflict",
                431 => "Request Header Fields Too Large",
                _ => "Internal Server Error"
            };

            byte[] json;
            try { json = JsonSerializer.SerializeToUtf8Bytes(body, jsonOptions); }
            catch (Exception ex)
            {
                Logger.WriteLine("API: " + ex.Message);
                status = 500; reason = "Internal Server Error";
                json = JsonSerializer.SerializeToUtf8Bytes(Error(ex.Message));
            }

            var head = Encoding.ASCII.GetBytes($"HTTP/1.1 {status} {reason}\r\nContent-Type: application/json; charset=utf-8\r\nContent-Length: {json.Length}\r\nConnection: close\r\n\r\n");
            stream.Write(head);
            stream.Write(json);
        }
    }
}
