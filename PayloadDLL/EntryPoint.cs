using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Threading;

namespace Payload
{
    public static class EntryPoint
    {
        private static TcpListener _listener;
        private static volatile bool _running;
        private static PerformanceController _controller;
        private static readonly object _lock = new object();

        // 防止 AssemblyResolve 处理程序重复注册
        private static bool _resolveHandlerAdded;
        private static string _assemblySearchDir;

        /// <summary>
        /// DLL 注入入口 — 由 CLR Hosting 引导程序调用。
        /// 支持重复调用：SHUTDOWN 后将 _running 置为 false，下次注入时通过
        /// BootstrapStart 导出函数再次进入，重新启动 TCP 服务。
        /// </summary>
        /// <param name="args">包含 DLL 搜索目录的路径（与 BootstrapNative.dll 同目录）</param>
        /// <returns>0 = 成功, 非 0 = 失败</returns>
        public static int LinkStart(string args)
        {
            lock (_lock)
            {
                if (_running)
                {
                    // 已在运行 — 可能是 LoadLibraryW 触发的二次调用
                    // (LoadLibraryW 对已加载的 DLL 不会再次触发 DllMain，
                    //  但此路径将由 Injector 通过 BootstrapStart 导出绕过)
                    return 1;
                }
                _running = true;
            }

            try
            {
                // 设置程序集解析 — 从 args 目录加载 HP DLL
                // 仅在首次调用时注册，避免重复累加
                string dllDir = !string.IsNullOrEmpty(args) ? args : AppDomain.CurrentDomain.BaseDirectory;
                if (!_resolveHandlerAdded)
                {
                    _assemblySearchDir = dllDir;
                    AppDomain.CurrentDomain.AssemblyResolve += ResolveAssembly;
                    _resolveHandlerAdded = true;
                }

                // 每次进入都重新创建控制器，确保状态清零
                _controller = new PerformanceController();
                _controller.InitializeAsync().GetAwaiter().GetResult();

                // 后台线程启动（或重启）TCP 服务
                var serverThread = new Thread(RunServer)
                {
                    IsBackground = true,
                    Name = "PayloadTcpServer"
                };
                serverThread.Start();

                return 0;
            }
            catch (Exception)
            {
                lock (_lock) { _running = false; }
                return -1;
            }
        }

        /// <summary>
        /// 命名的 AssemblyResolve 处理程序，用于从 DLL 目录加载 HP 程序集。
        /// 通过 _resolveHandlerAdded 标志确保只注册一次，防止重复注入导致处理程序累积。
        /// </summary>
        private static Assembly ResolveAssembly(object sender, ResolveEventArgs e)
        {
            var name = new AssemblyName(e.Name);
            string path = Path.Combine(_assemblySearchDir, name.Name + ".dll");
            if (File.Exists(path))
                return Assembly.LoadFrom(path);
            return null;
        }

        private static void RunServer()
        {
            try
            {
                // SO_REUSEADDR 允许快速重启：SHUTDOWN 后端口可能处于 TIME_WAIT，
                // 此选项允许立即重新绑定 26745，无需等待 OS 超时。
                _listener = new TcpListener(IPAddress.Loopback, 26745);
                _listener.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                _listener.Start();

                while (_running)
                {
                    try
                    {
                        var client = _listener.AcceptTcpClient();
                        ThreadPool.QueueUserWorkItem(HandleClient, client);
                    }
                    catch (SocketException)
                    {
                        if (!_running) break;
                    }
                    catch (ObjectDisposedException)
                    {
                        break;
                    }
                }
            }
            catch (Exception)
            {
            }
        }

        private static void HandleClient(object state)
        {
            var client = (TcpClient)state;
            using (client)
            {
                try
                {
                    using (var stream = client.GetStream())
                    using (var writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true })
                    using (var reader = new StreamReader(stream, new UTF8Encoding(false)))
                    {
                        string line;
                        while ((line = reader.ReadLine()) != null)
                        {
                            if (string.IsNullOrWhiteSpace(line)) continue;
                            string response = ProcessCommand(line.Trim());
                            writer.WriteLine(response);
                        }
                    }
                }
                catch (Exception)
                {
                }
            }
        }

        private static string ProcessCommand(string cmd)
        {
            try
            {
                if (cmd == "PING")
                    return "PONG";

                if (cmd == "GET_MODE")
                {
                    _controller.RefreshStatus();
                    return "MODE:" + _controller.CurrentMode;
                }

                if (cmd == "GET_MODE_NAME")
                {
                    _controller.RefreshStatus();
                    return "NAME:" + _controller.CurrentModeName;
                }

                if (cmd.StartsWith("SET_MODE:"))
                {
                    string val = cmd.Substring(9);
                    if (int.TryParse(val, out int mode))
                    {
                        bool ok = _controller.SetModeAsync(mode).GetAwaiter().GetResult();
                        return ok ? "OK" : "ERR:SetMode 返回 false";
                    }
                    return "ERR:无效的模式值: " + val;
                }

                if (cmd == "GET_SUPPORTED_MODES")
                {
                    return "MODES:" + string.Join(",", _controller.SupportedModes);
                }

                if (cmd == "GET_STATUS")
                {
                    _controller.RefreshStatus();
                    return string.Format(
                        "MODE:{0}|NAME:{1}|POWER:{2}|VERSION:V{3}|READY:{4}",
                        _controller.CurrentMode,
                        _controller.CurrentModeName,
                        _controller.IsOnBattery ? "BATTERY" : "AC",
                        _controller.SystemVersion,
                        _controller.IsInitialized);
                }

                if (cmd == "REFRESH")
                {
                    _controller.RefreshStatus();
                    return "OK";
                }

                if (cmd == "GET_VERSION")
                    return "VERSION:" + _controller.SystemVersion;

                if (cmd == "GET_IS_SUPPORT_SMARTSENSE")
                {
                    // SmartSense 检测已在初始化时完成
                    // SmartSense = 5，选项中有 5 说明支持
                    return "SMARTSENSE:" + (_controller.SelectedBalancedMode == 5 ? "1" : "0");
                }

                if (cmd == "SHUTDOWN")
                {
                    _running = false;
                    try { _listener?.Stop(); } catch { }
                    return "BYE";
                }

                return "ERR:未知命令: " + cmd;
            }
            catch (Exception ex)
            {
                return "ERR:" + ex.GetType().Name + ": " + ex.Message;
            }
        }
    }
}
