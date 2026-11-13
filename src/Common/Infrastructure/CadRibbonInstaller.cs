using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Windows.Input;
#if ZWCAD
using CadApp = ZwSoft.ZwCAD.ApplicationServices.Application;
#elif ACAD_CORE
using CadApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;
#else
using CadApp = Autodesk.AutoCAD.ApplicationServices.Application;
#endif

namespace ZwcadBatchPlot;

/// <summary>
/// 功能区（Ribbon）安装器：在 [有云] 等选项卡右侧追加 [LA打印] 选项卡，
/// 内含「图框管理 / 批量打印 / 设置相关」三个面板，按钮与菜单栏入口一一对应。
///
/// 背景：用户习惯隐藏经典菜单栏（MENUBAR=0），隐藏后菜单栏上的 LA打印 入口不可见，
/// 因此需要一个功能区选项卡作为常驻入口。
///
/// 关键：为什么需要「守护」阶段（勿删）——
/// 自动加载（注册表 DemandLoad）时插件在宿主启动早期就被加载，此时功能区往往还没建好，
/// 或者刚建好但<b>工作空间（CUI）尚未应用</b>。宿主随后应用工作空间时会重建功能区选项卡，
/// 把此前程序化加入的选项卡一并清掉，表现为：<b>下次启动后经典菜单栏有 LA打印，
/// 功能区却没有</b>（日志里还会写着“安装成功”）。
/// 因此本安装器：
/// 1) 安装阶段先等锚点选项卡 [有云] 出现（它是工作空间已加载完成的判据），超时才退回追加；
/// 2) 装好后进入守护阶段，持续校验选项卡是否仍在<b>当前</b>功能区上，被清掉就自动装回；
/// 3) 额外订阅宿主 <c>ComponentManager.ItemInitialized</c>，功能区重建时尽快触发校验。
///
/// 安全约束（重要，勿回退）：
/// 1. 不在 NETLOAD 的初始化线程里操作宿主 UI：所有功能区对象一律延后到宿主主线程
///    空闲（Application.Idle）时创建，避免在 UI 未就绪或跨线程状态下访问 Ribbon。
/// 2. 不做「遍历全部程序集 / 枚举全部类型 / 调用任意静态成员」这类探测：
///    CAD 进程中常驻大量第三方插件程序集（天正、有云等），触发它们的类型初始化会
///    执行不可控代码，造成的宿主崩溃属于非托管致命错误，托管 try/catch 无法拦截。
///    功能区控件一律只通过已知类型的限定名解析。
/// 3. 任何一步失败都放弃本次会话的功能区安装（菜单栏入口始终可用），
///    并把失败原因写入诊断日志文件，便于现场定位。安装结果不向命令行输出，
///    避免与用户正在执行的命令输入串在一起（见 WriteToCommandLine 的说明）。
/// 4. 支持环境变量 ZBP_LA_DISABLE_RIBBON=1 关闭功能区安装，便于排障与应急。
/// 5. 支持手动命令 ZBP_INSTALL_RIBBON（或别名 ZBP_RIBBON）随时重装功能区选项卡。
/// </summary>
public static class CadRibbonInstaller
{
    /// <summary>选项卡标题（显示在 [有云] 右侧）。</summary>
    private const string TabTitle = "LA打印";

    /// <summary>选项卡 Id，用于重复加载时定位并移除旧选项卡。</summary>
    private const string TabId = "ZBP_LA_PRINT_TAB";
    private const string LegacyTabIdLxl = "ZBP_LXL_PRINT_TAB";
    private const string LegacyTabTitleLxl = "Lxl打印";

    /// <summary>锚点选项卡标题关键字：新选项卡插入到该选项卡右侧。</summary>
    private const string AnchorTabKeyword = "有云";

    /// <summary>功能区安装尝试的最大连续时长（秒），期间每个主线程空闲周期都重试一次。</summary>
    private const int MaxRetrySeconds = 20;

    /// <summary>等到宿主空闲若干次后才开始安装：确保功能区与主窗口已就绪。</summary>
    private const int IdleWarmupCount = 3;

    /// <summary>
    /// 安装阶段等待锚点选项卡（[有云]）出现的时长（秒）。
    ///
    /// 关键：锚点选项卡来自<b>已加载的工作空间 / 局部 CUI 文件</b>，只有宿主把工作空间应用完成后
    /// 才会出现在功能区上。因此「锚点是否存在」可以作为「工作空间是否已经加载完」的判据。
    /// 在工作空间加载<b>之前</b>插入的选项卡，会在工作空间应用时被宿主连同内置选项卡一起重建掉——
    /// 这正是「自动加载启动后，经典菜单栏有 LA打印、功能区却没有」的根本原因。
    /// 因此安装阶段先等锚点出现，超时后才退回「追加到末尾」。
    /// </summary>
    private const int AnchorWaitSeconds = 10;

    /// <summary>两次“维护检查”的最小间隔（毫秒），防止宿主事件密集触发时空转。</summary>
    private const int MinPumpIntervalMs = 300;

    /// <summary>首次安装成功后“安定窗口”内的校验间隔（毫秒）：抵抗启动末尾的工作空间重建。</summary>
    private const int GuardVerifyIntervalMs = 1500;

    /// <summary>安定窗口结束后的长期校验间隔（毫秒）。</summary>
    private const int SteadyVerifyIntervalMs = 5000;

    /// <summary>首次安装成功后的“安定窗口”（秒）。</summary>
    private const int SettleSeconds = 60;

    /// <summary>轮询守护的最长时长（秒）；之后只保留事件通道（宿主重建功能区时仍会即可恢复）。</summary>
    private const int GuardMaxSeconds = 900;

    /// <summary>诊断日志文件名（与插件 DLL 同目录，不可写时退回临时目录）。</summary>
    private const string LogFileName = "LAPrint-功能区日志.txt";

    /// <summary>
    /// Ribbon 类型命名空间（各宿主自己的功能区 UI 程序集）。
    /// AutoCAD：`Autodesk.Windows.*`（AdWindows.dll）；
    /// 中望CAD：`ZwSoft.Windows.*`（ZdWindows.dll，注意不是 ZwSoft.ZwCAD.Windows）。
    /// </summary>
    private static readonly string[] RibbonNamespaces =
    {
#if ZWCAD
        "ZwSoft.Windows",
        "Autodesk.Windows"
#else
        "Autodesk.Windows",
        "ZwSoft.Windows"
#endif
    };

    /// <summary>宿主功能区 UI 程序集；仅在限定名解析失败时按需载入（每会话最多一次）。</summary>
    private static readonly string[] HostUiAssemblyNames =
    {
#if ZWCAD
        "ZdWindows",
        "AdWindows"
#else
        "AdWindows",
        "ZdWindows"
#endif
    };

    /// <summary>
    /// 功能区布局定义：面板名 → 若干「列」，每列是自上而下的按钮（显示文字, 命令名）。
    ///
    /// 说明：
    /// - 每个面板内的列为一组纵向堆叠的按钮；一个面板可以有多个列（横向并排）。
    /// - 每列最多 3 个按钮，保证三个面板的行数一致、所有按钮左起位置对齐。
    /// - 「设置相关」共 5 个按钮，拆成 3 + 2 两列；列内顺序与经典菜单栏完全一致。
    /// - 按钮显示文字与命令名必须与 <see cref="CadMenuInstaller"/> 中的菜单项一一对应。
    /// </summary>
    private static readonly (string Panel, (string Label, string Command)[][] Columns)[] Panels =
    {
        ("图框管理", new[]
        {
            new[] { ("新增图框", "ZBP_ADD_TITLE_BLOCK"), ("图框库管理", "ZBP_MANAGE_LIBRARY"), ("签章槽位", "ZBP_SIGN_SLOT") },
            new[] { ("签名库管理", "ZBP_SIGNATURE_LIBRARY"), ("印章库管理", "ZBP_STAMP_LIBRARY"), ("导入签名", "ZBP_SIGN_IMPORT") },
        }),
        ("批量打印", new[]
        {
            new[] { ("批量打印(选图框块)", "ZBP_SHOW_PANEL"), ("通用型批量打印", "ZBP_RECTANGLE_BATCH_PLOT"), ("单张打印", "ZBP_SINGLE_PLOT") },
        }),
        ("设置相关", new[]
        {
            new[] { ("设置", "ZBP_SETTINGS"), ("快捷键设置", "ZBP_SHORTCUT_SETTINGS"), ("安装自动加载", "ZBP_INSTALL_AUTOLOAD") },
            new[] { ("卸载自动加载", "ZBP_UNINSTALL_AUTOLOAD"), ("关于", "ZBP_ABOUT") },
        }),
    };

    private static readonly object SyncRoot = new();

    private static int _idleSeen;
    private static bool _started;
    private static bool _finished;
    private static bool _hostUiAssemblyProbed;
    private static bool _logStarted;
    private static DateTime _startedAtUtc;
    private static string? _lastError = "";
    private static Delegate? _idleHandler;
    private static EventInfo? _idleEvent;
    private static System.Windows.Forms.Timer? _timer;

    /// <summary>是否至少成功装过一次（此后进入“守护”语义：发现被清掉就装回）。</summary>
    private static bool _installedOnce;

    /// <summary>宿主功能区项初始化事件（重建功能区时触发）已置位，用于让下一次维护检查立刻执行。</summary>
    private static bool _nudge;

    /// <summary>维护检查正在执行（防重入：宿主事件可能在我们自己构造按钮时被触发）。</summary>
    private static bool _pumping;

    /// <summary>上次维护检查时间（UTC），用于节流。</summary>
    private static DateTime _lastPumpUtc;

    /// <summary>本次安装是否成功定位到锚点选项卡（[有云]），仅用于日志表述。</summary>
    private static bool _anchorLocated;

    /// <summary>是否已做过一次“选项卡已写入但未渲染”的界面刷新（每会话最多一次）。</summary>
    private static bool _renderRepairDone;

    /// <summary>安装阶段超时提示是否已写过（避免日志刷屏）。</summary>
    private static bool _installTimeoutLogged;

    /// <summary>维护检查异常是否已写过（避免日志刷屏）。</summary>
    private static bool _pumpErrorLogged;

    /// <summary>宿主功能区项初始化事件的订阅句柄。</summary>
    private static Delegate? _itemInitializedHandler;
    private static EventInfo? _itemInitializedEvent;

    /// <summary>
    /// 安装或刷新 [LA打印] 选项卡。
    /// 仅安排任务、不在此处触碰 UI：真正的安装延后到宿主主线程空闲时执行。
    /// 采用「Application.Idle 事件 + UI 计时器 + 功能区项初始化事件」三通道，任一通道可用即可完成安装。
    /// </summary>
    public static void Install()
    {
        StartLog();

        lock (SyncRoot)
        {
            if (_started)
            {
                return;
            }
        }

        if (IsDisabledByEnvironment())
        {
            Log("功能区安装已被环境变量 ZBP_LA_DISABLE_RIBBON 关闭。");
            MarkFinished();
            return;
        }

        lock (SyncRoot)
        {
            _started = true;
            _startedAtUtc = DateTime.UtcNow;
            _idleSeen = 0;
        }

        if (!EnsureChannels())
        {
            Log("宿主未提供 Application.Idle 事件，且计时器通道不可用，跳过功能区安装（菜单栏入口仍可用）。");
            MarkFinished();
        }
    }

    /// <summary>
    /// 挂好全部维护通道（空闲事件 / UI 计时器 / 功能区项初始化事件），可重复调用。
    /// </summary>
    /// <returns>是否至少有一个通道可用</returns>
    private static bool EnsureChannels()
    {
        var idleChannel = TrySubscribeIdle();
        var timerChannel = TryStartTimer();
        // 事件通道只用于“提示下一次检查”，拿不到也不影响安装，仅少一层自动恢复。
        TrySubscribeItemInitialized();
        return idleChannel || timerChannel;
    }

    /// <summary>
    /// 立即安装（供命令 ZBP_INSTALL_RIBBON 手动触发，可在 CAD 完全启动后随时重装）。
    /// 这是用户显式输入的命令，因此在命令行给出简短回执（自动安装过程不输出任何命令行提示）。
    /// </summary>
    public static void InstallNow()
    {
        StartLog();

        lock (SyncRoot)
        {
            _finished = false;
            _started = true;
            _startedAtUtc = DateTime.UtcNow;
            _lastError = "";
            _nudge = true;
            _lastPumpUtc = DateTime.MinValue;
            _installTimeoutLogged = false;
            _pumpErrorLogged = false;
        }

        // 重挂通道：手动重装往往是用户发现界面异常后的补救动作，必须把守护一并恢复。
        EnsureChannels();
        Pump(force: true);

        // 手动命令：给一行明确回执，避免用户不知道是否执行过。
        WriteManualCommandFeedback(_installedOnce
            ? "\nLA打印：功能区选项卡已安装（[有云] 右侧）。"
            : "\nLA打印：功能区选项卡安装失败，详见同目录 " + LogFileName + "。");
    }

    /// <summary>UI 计时器通道：即使 Idle 事件不触发也能完成安装与守护。</summary>
    private static bool TryStartTimer()
    {
        if (_timer != null)
        {
            return true;
        }

        try
        {
            var timer = new System.Windows.Forms.Timer { Interval = 400 };
            timer.Tick += (_, _) =>
            {
                try
                {
                    Pump(force: false);
                }
                catch
                {
                    // 计时器回调不得向外抛异常。
                }

                if (_finished)
                {
                    StopTimer(timer);
                }
            };
            timer.Start();
            _timer = timer;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void StopTimer(System.Windows.Forms.Timer? timer)
    {
        try
        {
            timer?.Stop();
            timer?.Dispose();
        }
        catch
        {
            // 释放失败不影响宿主运行。
        }
    }

    private static void StopAllChannels()
    {
        var timer = _timer;
        _timer = null;
        StopTimer(timer);
        UnsubscribeIdle();
        UnsubscribeItemInitialized();
    }

    /// <summary>
    /// 功能区维护主循环（统一收口）：
    ///
    /// 1. <b>安装阶段</b>（尚未装过）：把 [LA打印] 选项卡装上；先高频重试，超过
    ///    <see cref="MaxRetrySeconds"/> 后转为低频继续等待（不永久放弃），并在日志里写明当前原因。
    /// 2. <b>守护阶段</b>（已装过）：校验选项卡是否仍在<b>当前</b>功能区上。
    ///    宿主在启动末尾应用工作空间、或用户切换工作空间时会重建功能区，
    ///    期间被清掉的选项卡在这里被自动装回——这是「自动加载启动后功能区没有 LA打印」的根治手段。
    /// </summary>
    /// <param name="force">true 表示忽略节流立即执行（手动命令使用）</param>
    private static void Pump(bool force)
    {
        lock (SyncRoot)
        {
            if (_finished || _pumping)
            {
                return;
            }

            var now = DateTime.UtcNow;
            var interval = _nudge ? MinPumpIntervalMs : ComputePumpIntervalMs();
            if (!force && (now - _lastPumpUtc).TotalMilliseconds < interval)
            {
                return;
            }

            _lastPumpUtc = now;
            _nudge = false;
            _pumping = true;
        }

        try
        {
            var ribbon = FindRibbon();
            if (ribbon == null)
            {
                if (!_installedOnce)
                {
                    _lastError = "未找到宿主功能区控件（ComponentManager.Ribbon 为空，或功能区未启用）";
                    if ((DateTime.UtcNow - _startedAtUtc).TotalSeconds >= MaxRetrySeconds)
                    {
                        // 超时不再永久放弃：降到低频继续等（宿主可能启动很慢），同时把原因写进日志。
                        LogInstallTimeoutOnce();
                    }
                }

                // 已装过则只是功能区暂时不可用（隐藏/未创建）：不做处理，等它回来。
                return;
            }

            if (TabPresent(ribbon, out var existingTab))
            {
                if (!_installedOnce)
                {
                    // 其它副本/上一会话已经装好：直接进入守护语义。
                    Log("功能区选项卡 [LA打印] 已存在，无需重复安装。");
                    _installedOnce = true;
                }

                EnsureTabRendered(ribbon, existingTab);
                return;
            }

            // 选项卡不在当前功能区上：首次安装，或装好后被宿主重建掉 → 装回。
            var wasInstalled = _installedOnce;
            if (!TryInstallInto(ribbon, out var error))
            {
                _lastError = error;
                if (!wasInstalled && (DateTime.UtcNow - _startedAtUtc).TotalSeconds >= MaxRetrySeconds)
                {
                    LogInstallTimeoutOnce();
                }

                return;
            }

            _installedOnce = true;
            if (wasInstalled)
            {
                Log("检测到功能区选项卡 [LA打印] 被宿主清掉（功能区重建），已自动重新装回。");
            }
            else if (_anchorLocated)
            {
                Log("功能区选项卡 [LA打印] 安装成功（已置于「" + AnchorTabKeyword + "」选项卡右侧）。");
            }
            else
            {
                Log("功能区选项卡 [LA打印] 安装成功（未出现「" + AnchorTabKeyword + "」选项卡，已追加到末尾）。");
            }
        }
        catch (Exception ex)
        {
            _lastError = ex.GetType().Name + ": " + ex.Message;
            if (!_pumpErrorLogged)
            {
                _pumpErrorLogged = true;
                Log("功能区维护检查异常（将继续重试）：" + _lastError);
            }
        }
        finally
        {
            lock (SyncRoot)
            {
                _pumping = false;
            }
        }
    }

    /// <summary>安装阶段超时提示（每个会话只写一次，之后转为低频重试）。</summary>
    private static void LogInstallTimeoutOnce()
    {
        if (_installTimeoutLogged)
        {
            return;
        }

        _installTimeoutLogged = true;
        Log("功能区选项卡尚未安装成功（已尝试 " + MaxRetrySeconds + " 秒）：" + (_lastError ?? "未知原因")
            + "。已转为低频继续重试；也可在命令行执行 ZBP_INSTALL_RIBBON 手动重试。");
    }

    /// <summary>计算本次维护检查与上次之间需要间隔多久（毫秒）。</summary>
    private static double ComputePumpIntervalMs()
    {
        var elapsedSeconds = (DateTime.UtcNow - _startedAtUtc).TotalSeconds;

        if (!_installedOnce)
        {
            // 安装阶段：先快速重试；超过 MaxRetrySeconds 后降频继续等
            //（宿主可能启动很慢，过早放弃会导致功能区永远装不上）。
            return elapsedSeconds <= MaxRetrySeconds ? MinPumpIntervalMs : SteadyVerifyIntervalMs;
        }

        // 守护阶段：启动安定窗口内高频校验（此阶段宿主常重建功能区），之后降频，
        // 窗口结束后不再轮询，只靠宿主事件与手动命令唤醒。
        if (elapsedSeconds <= SettleSeconds)
        {
            return GuardVerifyIntervalMs;
        }

        return elapsedSeconds <= GuardMaxSeconds ? SteadyVerifyIntervalMs : double.MaxValue;
    }

    /// <summary>环境变量开关：ZBP_LA_DISABLE_RIBBON=1 时完全跳过功能区安装。</summary>
    private static bool IsDisabledByEnvironment()
    {
        try
        {
            var value = Environment.GetEnvironmentVariable("ZBP_LA_DISABLE_RIBBON");
            return string.Equals(value, "1", StringComparison.Ordinal)
                || string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>订阅宿主主线程空闲事件（AutoCAD / 中望CAD 均提供 Application.Idle）。</summary>
    private static bool TrySubscribeIdle()
    {
        if (_idleHandler != null)
        {
            return true;
        }

        try
        {
            var idleEvent = typeof(CadApp).GetEvent("Idle", BindingFlags.Public | BindingFlags.Static);
            if (idleEvent == null)
            {
                return false;
            }

            var handlerMethod = typeof(CadRibbonInstaller)
                .GetMethod(nameof(OnHostIdle), BindingFlags.NonPublic | BindingFlags.Static);
            var handlerType = idleEvent.EventHandlerType;
            var addMethod = idleEvent.GetAddMethod();
            if (handlerType == null || handlerMethod == null || addMethod == null)
            {
                return false;
            }

            var handler = Delegate.CreateDelegate(handlerType, handlerMethod);
            addMethod.Invoke(null, new object[] { handler });

            _idleEvent = idleEvent;
            _idleHandler = handler;
            return true;
        }
        catch (Exception ex)
        {
            Log("订阅 Application.Idle 失败：" + ex.GetType().Name + ": " + ex.Message);
            return false;
        }
    }

    /// <summary>主线程空闲回调：预热若干周期后交给维护循环。失败由计时器通道继续重试。</summary>
    private static void OnHostIdle(object sender, EventArgs e)
    {
        try
        {
            lock (SyncRoot)
            {
                if (_finished)
                {
                    return;
                }

                _idleSeen++;
                if (_idleSeen < IdleWarmupCount)
                {
                    return;
                }
            }

            Pump(force: false);
        }
        catch
        {
            // 兜底：空闲回调本身绝不允许向外抛异常。
            MarkFinished();
            StopAllChannels();
        }
    }

    /// <summary>
    /// 订阅宿主 <c>ComponentManager.ItemInitialized</c>：宿主在创建/重建功能区项
    /// （启动末尾应用工作空间、用户切换工作空间、加载局部 CUI 等）时会触发该事件。
    /// 回调里只置一个「尽快检查」标记，真正的安装/校验仍由空闲与计时器通道完成，
    /// 避免在宿主事件回调里做重活或造成重入。
    /// </summary>
    private static bool TrySubscribeItemInitialized()
    {
        if (_itemInitializedHandler != null)
        {
            return true;
        }

        try
        {
            foreach (var ns in RibbonNamespaces)
            {
                var componentManager = FindType(ns + ".ComponentManager");
                var eventInfo = componentManager?.GetEvent("ItemInitialized", BindingFlags.Public | BindingFlags.Static);
                if (eventInfo == null)
                {
                    continue;
                }

                var handlerMethod = typeof(CadRibbonInstaller)
                    .GetMethod(nameof(OnHostRibbonItemInitialized), BindingFlags.NonPublic | BindingFlags.Static);
                var handlerType = eventInfo.EventHandlerType;
                var addMethod = eventInfo.GetAddMethod();
                if (handlerMethod == null || handlerType == null || addMethod == null)
                {
                    continue;
                }

                // 处理函数签名是 (object, EventArgs)；宿主事件是 EventHandler<RibbonItemEventArgs>。
                // 委托绑定支持引用类型的逆变（net48 / .NET 8 均已实测可用）。
                var handler = Delegate.CreateDelegate(handlerType, handlerMethod);
                addMethod.Invoke(null, new object[] { handler });
                _itemInitializedEvent = eventInfo;
                _itemInitializedHandler = handler;
                return true;
            }
        }
        catch (Exception ex)
        {
            Log("订阅宿主功能区项初始化事件失败（不影响安装，仅少一层自动恢复）："
                + ex.GetType().Name + ": " + ex.Message);
        }

        return false;
    }

    /// <summary>宿主功能区项初始化回调：仅置位标记，实际检查交给空闲/计时器通道。</summary>
    private static void OnHostRibbonItemInitialized(object sender, EventArgs e)
    {
        _nudge = true;
    }

    private static void UnsubscribeItemInitialized()
    {
        try
        {
            if (_itemInitializedEvent != null && _itemInitializedHandler != null)
            {
                _itemInitializedEvent.GetRemoveMethod()?.Invoke(null, new object[] { _itemInitializedHandler });
            }
        }
        catch
        {
            // 取消订阅失败不影响宿主运行。
        }

        _itemInitializedEvent = null;
        _itemInitializedHandler = null;
    }

    private static void MarkFinished()
    {
        lock (SyncRoot)
        {
            _finished = true;
        }
    }

    private static void UnsubscribeIdle()
    {
        try
        {
            if (_idleEvent != null && _idleHandler != null)
            {
                _idleEvent.GetRemoveMethod()?.Invoke(null, new object[] { _idleHandler });
            }
        }
        catch
        {
            // 取消订阅失败不影响宿主运行。
        }

        _idleEvent = null;
        _idleHandler = null;
    }

    /// <summary>
    /// 在<b>指定的</b>功能区控件上装出 [LA打印] 选项卡（不负责查找功能区，便于守护阶段复用）。
    /// </summary>
    private static bool TryInstallInto(object ribbon, out string error)
    {
        error = "";
        _anchorLocated = false;

        var tabs = GetMember(ribbon, "Tabs");
        if (tabs == null)
        {
            error = "宿主功能区未暴露 Tabs 集合";
            return false;
        }

        // 安装阶段先等锚点选项卡（[有云]）出现：它来自已加载的工作空间 / 局部 CUI，
        // 早于它插入的选项卡会在工作空间应用时被宿主重建掉。超时后退回“追加到末尾”。
        if (FindInsertIndexAfterAnchor(tabs) < 0
            && !_installedOnce
            && (DateTime.UtcNow - _startedAtUtc).TotalSeconds < AnchorWaitSeconds)
        {
            error = "等待工作空间加载完成（尚未出现「" + AnchorTabKeyword + "」选项卡）";
            return false;
        }

        var tab = CreateTab();
        if (tab == null)
        {
            error = "无法创建 RibbonTab（宿主未提供已知命名空间的 RibbonTab 类型）";
            return false;
        }

        var panelCount = 0;
        var tabPanels = GetMember(tab, "Panels");
        foreach (var definition in Panels)
        {
            var panel = CreatePanel(definition.Panel, definition.Columns);
            if (panel != null && AddToCollection(tabPanels, panel))
            {
                panelCount++;
            }
        }

        if (panelCount == 0)
        {
            error = "无法创建功能区面板（RibbonPanel / RibbonPanelSource 不可用）";
            return false;
        }

        // 重复加载：先移除同名（同 Id / 同标题）旧选项卡，避免出现两个 LA打印。
        RemoveExistingTab(tabs);

        var insertIndex = FindInsertIndexAfterAnchor(tabs);
        _anchorLocated = insertIndex >= 0;

        if (!InsertTab(tabs, tab, insertIndex))
        {
            error = "向功能区 Tabs 集合写入选项卡失败（宿主集合拒绝添加）";
            return false;
        }

        ForceRibbonRefresh(ribbon, tab);
        error = "";
        return true;
    }

    /// <summary>当前功能区上是否已有 [LA打印] 选项卡（按 Id / 标题匹配）。</summary>
    private static bool TabPresent(object ribbon, out object? tab)
    {
        tab = null;

        var tabs = GetMember(ribbon, "Tabs");
        if (tabs is not IEnumerable enumerable)
        {
            return false;
        }

        try
        {
            foreach (var item in enumerable)
            {
                if (item != null && IsOurTab(item))
                {
                    tab = item;
                    return true;
                }
            }
        }
        catch
        {
            // 集合枚举失败：按不存在处理（下一轮会重试）。
        }

        return false;
    }

    /// <summary>
    /// 选项卡已经在 Tabs 集合里、但没有进入功能区真正渲染的视图（MainTabs）时，
    /// 说明宿主尚未把它纳入界面（多见于启动早期）：请求一次刷新让它显示出来。
    /// 每个会话最多做一次，避免反复刷新造成界面抖动。
    /// </summary>
    private static void EnsureTabRendered(object ribbon, object? tab)
    {
        if (_renderRepairDone || tab == null)
        {
            return;
        }

        try
        {
            var mainTabs = GetMember(ribbon, "MainTabs");
            var view = GetMember(mainTabs, "View");
            if (view is not IEnumerable items)
            {
                return;
            }

            var visibleCount = 0;
            var found = false;
            foreach (var item in items)
            {
                if (item == null)
                {
                    continue;
                }

                visibleCount++;
                if (ReferenceEquals(item, tab) || IsOurTab(item))
                {
                    found = true;
                    break;
                }
            }

            if (found || visibleCount == 0)
            {
                return;
            }

            _renderRepairDone = true;
            Log("选项卡仍在功能区集合中但未进入渲染视图，已请求宿主刷新界面。");
            ForceRibbonRefresh(ribbon, tab);
            TryRefreshRibbon(ribbon);
        }
        catch
        {
            // 渲染视图不可用（宿主版本差异）：忽略。
        }
    }

    /// <summary>判断某个选项卡是否就是本插件的 [LA打印] 选项卡。</summary>
    private static bool IsOurTab(object item)
    {
        var id = GetMember(item, "Id") as string;
        if (string.Equals(id, TabId, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var title = GetMember(item, "Title") as string;
        return string.Equals(title, TabTitle, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 强制宿主构建并刷新新选项卡的 UI：临时切换到新选项卡再切回原选项卡。
    /// 部分宿主对直接加入集合的选项卡不会立即生成界面，需要一次激活动作触发。
    /// 原选项卡取不到时保持停在新选项卡（也是安装成功的直观体现）。
    /// </summary>
    private static void ForceRibbonRefresh(object ribbon, object tab)
    {
        try
        {
            var previous = GetMember(ribbon, "ActiveTab");
            SetMember(ribbon, "ActiveTab", tab);
            if (previous != null && !ReferenceEquals(previous, tab))
            {
                SetMember(ribbon, "ActiveTab", previous);
            }
        }
        catch
        {
            // 切换失败不影响选项卡已写入的事实。
        }

        TryRefreshRibbon(ribbon);
    }

    // ---- Ribbon 对象构造 ----

    /// <summary>创建 [LA打印] 选项卡。</summary>
    private static object? CreateTab()
    {
        var tabType = FindRibbonType("RibbonTab");
        var tab = tabType == null ? null : CreateInstance(tabType);
        if (tab == null)
        {
            return null;
        }

        SetMember(tab, "Id", TabId);
        SetMember(tab, "Title", TabTitle);
        SetMember(tab, "Name", TabTitle);
        SetMember(tab, "IsVisible", true);
        return tab;
    }

    /// <summary>
    /// 创建面板：标题显示在面板下方；列横向并排，列内按钮自上而下排列。
    /// </summary>
    private static object? CreatePanel(string title, (string Label, string Command)[][] columns)
    {
        var panelType = FindRibbonType("RibbonPanel");
        var sourceType = FindRibbonType("RibbonPanelSource");
        if (panelType == null || sourceType == null)
        {
            return null;
        }

        var panel = CreateInstance(panelType);
        var source = CreateInstance(sourceType);
        if (panel == null || source == null)
        {
            return null;
        }

        SetMember(source, "Id", "ZBP_PANEL_" + title);
        SetMember(source, "Title", title);
        SetMember(source, "Name", title);
        SetMember(panel, "Source", source);

        var itemList = GetMember(source, "Items");
        var added = false;
        foreach (var column in columns)
        {
            var columnPanel = CreateColumnPanel(column);
            if (columnPanel != null && AddToCollection(itemList, columnPanel))
            {
                added = true;
                continue;
            }

            // 宿主没有 Row 面板类型：退化为按列顺序直接逐个加入，由宿主自行排布。
            foreach (var item in column)
            {
                var button = CreateButton(item.Label, item.Command);
                if (button != null && AddToCollection(itemList, button))
                {
                    added = true;
                }
            }
        }

        return added ? panel : null;
    }

    /// <summary>
    /// 创建一个纵向单列（一列按钮）；宿主缺少 Row 面板类型时返回 null。
    /// 列内所有按钮顶部对齐（IsTopJustified），使各面板首行按钮处在同一水平线上。
    /// </summary>
    private static object? CreateColumnPanel((string Label, string Command)[] items)
    {
        var rowPanelType = FindRibbonType("RibbonRowPanel");
        if (rowPanelType == null)
        {
            return null;
        }

        var rowBreakType = FindRibbonType("RibbonRowBreak");
        var rowPanel = CreateInstance(rowPanelType);
        if (rowPanel == null)
        {
            return null;
        }

        // 默认列内容在面板内垂直居中，会让按钮少的列整体下移（与其它列参差不齐）；
        // 置为顶部对齐后，三个面板的按钮都从面板顶部开始排列，左起位置一致。
        SetMember(rowPanel, "IsTopJustified", true);

        var rowItems = GetMember(rowPanel, "Items");
        var added = false;
        for (var i = 0; i < items.Length; i++)
        {
            var button = CreateButton(items[i].Label, items[i].Command);
            if (button == null || !AddToCollection(rowItems, button))
            {
                continue;
            }

            added = true;

            // 除最后一个按钮外，每个按钮后换行 → 列内呈纵向单列，与菜单观感一致。
            if (i < items.Length - 1 && rowBreakType != null)
            {
                AddToCollection(rowItems, CreateInstance(rowBreakType));
            }
        }

        return added ? rowPanel : null;
    }

    /// <summary>创建单个按钮：只显示文字（不显示图标），点击后执行对应命令。</summary>
    private static object? CreateButton(string label, string command)
    {
        var buttonType = FindRibbonType("RibbonButton");
        var button = buttonType == null ? null : CreateInstance(buttonType);
        if (button == null)
        {
            return null;
        }

        SetMember(button, "Text", label);
        SetMember(button, "Description", label);
        SetMember(button, "Name", "ZBP_RIBBON_" + command);
        SetMember(button, "Id", "ZBP_BTN_" + command);
        SetMember(button, "ShowText", true);
        SetMember(button, "ShowImage", false);
        SetEnumMember(button, "Size", "Standard");

        // 命令既通过按钮实例携带（AdWindows 回调参数是被点击的按钮本体，
        // 不是 CommandParameter），也写入 CommandParameter 便于宿主/调试查看。
        SetMember(button, "CommandParameter", command);
        SetMember(button, "CommandHandler", new CadRibbonCommand(command));
        return button;
    }

    /// <summary>
    /// 获取宿主的 RibbonControl 实例；不可用时返回 null。
    /// 只通过已知类型（Autodesk.Windows.ComponentManager / ZwSoft.ZwCAD.Windows.ComponentManager）
    /// 读取静态 Ribbon 属性，绝不枚举未知程序集或未知类型的静态成员。
    /// 属性取不到时，退回到 WPF 可视树中查找 RibbonControl 实例（同样只涉及已知类型与 WPF 自身 API）。
    /// </summary>
    private static object? FindRibbon()
    {
        foreach (var ns in RibbonNamespaces)
        {
            var componentManager = FindType(ns + ".ComponentManager");
            if (componentManager == null)
            {
                continue;
            }

            try
            {
                var ribbon = componentManager
                    .GetProperty("Ribbon", BindingFlags.Public | BindingFlags.Static)
                    ?.GetValue(null, null);
                if (ribbon != null)
                {
                    return ribbon;
                }
            }
            catch
            {
                // 该宿主未提供 ComponentManager.Ribbon：继续尝试下一命名空间。
            }
        }

        return FindRibbonInVisualTree();
    }

    /// <summary>在已创建的 WPF 可视树（各 HwndSource 根视觉）中查找功能区控件实例。</summary>
    private static object? FindRibbonInVisualTree()
    {
        Type? ribbonType = null;
        foreach (var ns in RibbonNamespaces)
        {
            ribbonType = FindType(ns + ".RibbonControl");
            if (ribbonType != null)
            {
                break;
            }
        }

        if (ribbonType == null)
        {
            return null;
        }

        try
        {
            var sources = System.Windows.PresentationSource.CurrentSources;
            if (sources == null)
            {
                return null;
            }

            foreach (var source in sources)
            {
                if (source is not System.Windows.Interop.HwndSource hwndSource)
                {
                    continue;
                }

                var found = SearchVisual(hwndSource.RootVisual, ribbonType, 0);
                if (found != null)
                {
                    return found;
                }
            }
        }
        catch
        {
            // 可视树不可访问（UI 未就绪等）：按未找到处理。
        }

        return null;
    }

    private static object? SearchVisual(System.Windows.DependencyObject? node, Type targetType, int depth)
    {
        if (node == null || depth > 40)
        {
            return null;
        }

        if (targetType.IsInstanceOfType(node))
        {
            return node;
        }

        int count;
        try
        {
            count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(node);
        }
        catch
        {
            return null;
        }

        for (var i = 0; i < count; i++)
        {
            System.Windows.DependencyObject child;
            try
            {
                child = System.Windows.Media.VisualTreeHelper.GetChild(node, i);
            }
            catch
            {
                continue;
            }

            var found = SearchVisual(child, targetType, depth + 1);
            if (found != null)
            {
                return found;
            }
        }

        return null;
    }

    /// <summary>移除已存在的同名选项卡（按 Id / 标题匹配），保证重复加载不叠加。</summary>
    private static void RemoveExistingTab(object tabs)
    {
        if (tabs is not IEnumerable enumerable)
        {
            return;
        }

        var stale = new List<object>();
        foreach (var item in enumerable)
        {
            if (item != null && IsOurTab(item))
            {
                stale.Add(item);
            }
        }

        foreach (var item in stale)
        {
            TryRemove(tabs, item);
        }
    }

    /// <summary>
    /// 把选项卡插入功能区：<paramref name="index"/> 为锚点（[有云]）右侧的下标，
    /// 小于 0 时追加到末尾。
    /// </summary>
    private static bool InsertTab(object tabs, object tab, int index)
    {
        if (index >= 0)
        {
            try
            {
                if (tabs is IList list)
                {
                    list.Insert(index, tab);
                    return true;
                }
            }
            catch
            {
                // 集合不支持按索引插入：退回追加。
            }
        }

        return AddToCollection(tabs, tab);
    }

    /// <summary>返回锚点选项卡（标题含"有云"）右侧的插入下标；未找到返回 -1。</summary>
    private static int FindInsertIndexAfterAnchor(object tabs)
    {
        try
        {
            if (tabs is not IList list)
            {
                return -1;
            }

            for (var i = 0; i < list.Count; i++)
            {
                var title = GetMember(list[i], "Title") as string;
                if (title != null && title.Length > 0
                    && title.IndexOf(AnchorTabKeyword, StringComparison.Ordinal) >= 0)
                {
                    return i + 1;
                }
            }
        }
        catch
        {
            // 集合读取失败：按未找到锚点处理。
        }

        return -1;
    }

    /// <summary>请求宿主刷新功能区布局（属性不存在时忽略）。</summary>
    private static void TryRefreshRibbon(object ribbon)
    {
        try
        {
            var method = ribbon.GetType().GetMethod("UpdateLayout", Type.EmptyTypes);
            method?.Invoke(ribbon, null);
        }
        catch
        {
            // 刷新失败不影响选项卡已写入的事实。
        }
    }

    /// <summary>按命名空间前缀解析宿主功能区类型（如 RibbonTab）。</summary>
    private static Type? FindRibbonType(string typeName)
    {
        foreach (var ns in RibbonNamespaces)
        {
            var type = FindType(ns + "." + typeName);
            if (type != null)
            {
                return type;
            }
        }

        return null;
    }

    /// <summary>查找类型：限定名 → 已加载程序集 → 按需载入宿主 UI 程序集后再查一次。</summary>
    private static Type? FindType(string fullName)
    {
        try
        {
            var direct = Type.GetType(fullName, false);
            if (direct != null)
            {
                return direct;
            }
        }
        catch
        {
        }

        var loaded = FindTypeInLoadedAssemblies(fullName);
        if (loaded != null)
        {
            return loaded;
        }

        if (_hostUiAssemblyProbed)
        {
            return null;
        }

        _hostUiAssemblyProbed = true;

        // 功能区 UI 程序集在部分宿主中未必已加载：显式载入后再找一次（每会话一次）。
        // 仅按精确程序集名载入，不枚举、不反射任何未知类型。
        var hasNewAssembly = false;
        foreach (var assemblyName in HostUiAssemblyNames)
        {
            try
            {
                Assembly.Load(assemblyName);
                hasNewAssembly = true;
            }
            catch
            {
                // 该程序集不存在（另一平台）：继续尝试下一个。
            }
        }

        return hasNewAssembly ? FindTypeInLoadedAssemblies(fullName) : null;
    }

    /// <summary>在已加载程序集中按全名查找类型（GetType(string) 不会触发类型初始化）。</summary>
    private static Type? FindTypeInLoadedAssemblies(string fullName)
    {
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            try
            {
                var type = assembly.GetType(fullName, false);
                if (type != null)
                {
                    return type;
                }
            }
            catch
            {
                // 反射加载失败的动态程序集：跳过。
            }
        }

        return null;
    }

    // ---- 诊断输出 ----

    private static void StartLog()
    {
        lock (SyncRoot)
        {
            if (_logStarted)
            {
                return;
            }

            _logStarted = true;
        }

        try
        {
            var path = ResolveLogPath();
            File.WriteAllText(
                path,
                "LA打印 功能区安装日志 —— " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + Environment.NewLine,
                Encoding.UTF8);
        }
        catch
        {
            // 日志不可写时忽略。
        }
    }

    /// <summary>写诊断日志文件。刻意不向命令行输出：自动安装过程中的命令行提示会与用户
    /// 正在执行的命令输入串在一起（例如与选择提示混排），属于纯噪声。</summary>
    private static void Log(string message)
    {
        try
        {
            var line = "[" + DateTime.Now.ToString("HH:mm:ss") + "] " + message + Environment.NewLine;
            File.AppendAllText(ResolveLogPath(), line, Encoding.UTF8);
        }
        catch
        {
            // 日志不可写时忽略。
        }
    }

    /// <summary>仅用于用户手动输入的命令（ZBP_INSTALL_RIBBON）所在场景的简短回执。</summary>
    private static void WriteManualCommandFeedback(string message)
    {
        try
        {
            CadApp.DocumentManager.MdiActiveDocument?.Editor.WriteMessage(message);
        }
        catch
        {
            // 无活动文档（启动画面等）：忽略。
        }
    }

    private static string ResolveLogPath()
    {
        try
        {
            var directory = Path.GetDirectoryName(typeof(CadRibbonInstaller).Assembly.Location);
            if (!string.IsNullOrEmpty(directory))
            {
                return Path.Combine(directory, LogFileName);
            }
        }
        catch
        {
            // 取不到程序集目录：退回临时目录。
        }

        return Path.Combine(Path.GetTempPath(), LogFileName);
    }

    // ---- 反射工具 ----

    /// <summary>创建实例；失败返回 null。</summary>
    private static object? CreateInstance(Type type)
    {
        try
        {
            return Activator.CreateInstance(type);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>读取实例属性/字段值，失败返回 null。</summary>
    private static object? GetMember(object? target, string name)
    {
        if (target == null)
        {
            return null;
        }

        try
        {
            var type = target.GetType();
            var property = type.GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
            if (property != null && property.CanRead && property.GetIndexParameters().Length == 0)
            {
                return property.GetValue(target, null);
            }

            var field = type.GetField(name, BindingFlags.Public | BindingFlags.Instance);
            return field?.GetValue(target);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>设置实例属性值；宿主缺少该属性或类型不匹配时静默忽略。</summary>
    private static void SetMember(object? target, string name, object? value)
    {
        if (target == null)
        {
            return;
        }

        try
        {
            var property = target.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
            if (property == null || !property.CanWrite || property.GetIndexParameters().Length != 0)
            {
                return;
            }

            if (value != null && !property.PropertyType.IsInstanceOfType(value))
            {
                if (property.PropertyType == typeof(string))
                {
                    value = value.ToString();
                }
                else
                {
                    return;
                }
            }

            property.SetValue(target, value, null);
        }
        catch
        {
            // 属性在不同版本中类型不一致：忽略。
        }
    }

    /// <summary>设置枚举属性（如按钮尺寸 RibbonItemSize.Standard）。</summary>
    private static void SetEnumMember(object? target, string propertyName, string enumValueName)
    {
        if (target == null)
        {
            return;
        }

        try
        {
            var property = target.GetType().GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance);
            if (property == null || !property.CanWrite || property.GetIndexParameters().Length != 0)
            {
                return;
            }

            if (!property.PropertyType.IsEnum)
            {
                return;
            }

            var value = Enum.Parse(property.PropertyType, enumValueName, ignoreCase: true);
            property.SetValue(target, value, null);
        }
        catch
        {
            // 属性不存在或枚举值名称不同（宿主版本差异）：保持宿主默认尺寸。
        }
    }

    /// <summary>向集合追加元素；成功返回 true。</summary>
    private static bool AddToCollection(object? collection, object? item)
    {
        if (collection == null || item == null)
        {
            return false;
        }

        try
        {
            var type = collection.GetType();
            var add = type.GetMethod("Add", BindingFlags.Public | BindingFlags.Instance, null, new[] { item.GetType() }, null)
                ?? type.GetMethod("Add", BindingFlags.Public | BindingFlags.Instance);
            if (add != null)
            {
                add.Invoke(collection, new[] { item });
                return true;
            }
        }
        catch
        {
            // 参数类型不匹配或宿主拒绝：尝试 IList 路径。
        }

        try
        {
            if (collection is IList list)
            {
                list.Add(item);
                return true;
            }
        }
        catch
        {
            // 只读集合：忽略。
        }

        return false;
    }

    /// <summary>从集合移除元素，成功返回 true。</summary>
    private static bool TryRemove(object collection, object item)
    {
        try
        {
            var remove = collection.GetType().GetMethod("Remove", BindingFlags.Public | BindingFlags.Instance);
            if (remove != null)
            {
                remove.Invoke(collection, new[] { item });
                return true;
            }
        }
        catch
        {
            // 宿主集合不支持 Remove。
        }

        try
        {
            if (collection is IList list)
            {
                list.Remove(item);
                return true;
            }
        }
        catch
        {
            // 只读集合：忽略。
        }

        return false;
    }
}

/// <summary>
/// 功能区按钮命令：把点击转换为 CAD 命令行调用，
/// 与菜单栏入口使用同一套命令名，行为完全一致（含「先取消正在执行的命令」）。
///
/// 关键点：宿主 AdWindows 在按钮被点击时，调用
/// <c>ICommand.Execute(被点击的 RibbonItem 对象)</c>，
/// 传入的参数是<b>按钮本体</b>而不是按钮的 CommandParameter（这一点与 WPF 的常规约定不同）。
/// 因此命令名必须由本对象自己持有（构造时注入），不能依赖 Execute 的入参类型。
/// </summary>
internal sealed class CadRibbonCommand : ICommand
{
    /// <summary>两次 ESC：与菜单宏前缀 ^C^C 等价，用于结束正在执行的命令。</summary>
    private const string EscapePrefix = "\u0003\u0003";

    private readonly string _commandName;

    public CadRibbonCommand(string commandName)
    {
        _commandName = commandName ?? string.Empty;
    }

    public event EventHandler? CanExecuteChanged
    {
        add { }
        remove { }
    }

    public bool CanExecute(object? parameter) => true;

    public void Execute(object? parameter)
    {
        var commandName = ResolveCommandName(parameter);
        if (string.IsNullOrWhiteSpace(commandName))
        {
            return;
        }

        try
        {
            var document = CadApp.DocumentManager.MdiActiveDocument;
            if (document == null)
            {
                return;
            }

            // 仅在确有命令正在执行时先发出 ESC（等价菜单宏的 ^C^C）：
            // 无活动命令时不发 ESC，以免无故清掉用户已经选择的对象（pickfirst）。
            var prefix = string.Empty;
            try
            {
                if (Convert.ToInt32(CadApp.GetSystemVariable("CMDACTIVE")) > 0)
                {
                    prefix = EscapePrefix;
                }
            }
            catch
            {
                // 取不到 CMDACTIVE（宿主差异）：按没有活动命令处理。
            }

            // 命令尾部必须留一个空格，相当于回车确认，否则只会把命令名送进命令行而不执行。
            document.SendStringToExecute(prefix + "_" + commandName + " ", true, false, true);
        }
        catch
        {
            // 无活动文档或命令投递失败：忽略。
        }
    }

    /// <summary>
    /// 取本次点击要执行的命令名：优先使用构造时注入的命令名；
    /// 兼容宿主把字符串参数直接传入的情况，以及把 RibbonItem 传入时从其 CommandParameter 读取。
    /// </summary>
    private string ResolveCommandName(object? parameter)
    {
        if (!string.IsNullOrWhiteSpace(_commandName))
        {
            return _commandName;
        }

        if (parameter is string text)
        {
            return text;
        }

        try
        {
            var property = parameter?.GetType()
                .GetProperty("CommandParameter", BindingFlags.Public | BindingFlags.Instance);
            return property?.GetValue(parameter, null) as string ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }
}
