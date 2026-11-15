# ModelX Studio 代码审查与优化报告

作者：zhenhua ｜ 审查日期：2026-11-14（同日做了第二轮：界面配色与标题栏，见第七节；2026-11-15 第三轮：引擎随包分发与开源合规，见第八节；同日第四轮：放置引擎核查与 Vulkan 支持，见第九节）｜ 首轮范围：`src/` 全部 33 个手写源文件（Core 12 个 + App 21 个），约 3400 行

## 结论概览

架构本身是合理的，不需要重构：`Core`（net8.0，无 WPF 依赖）/ `App`（WPF）分层干净，服务职责单一，ViewModel 划分与页面一一对应；「三档生成风格」「按硬件打适配标签」「默认 hf-mirror」这类面向小白的抽象做得对，注释也是中文且解释了为什么。**问题集中在两类：错误处理过于乐观（出错时骗用户而不是告诉用户）、UI 线程效率（每个 token / 每行日志一次同步调用）。**

本轮共修改 14 处：P0 四项（会产出坏模型文件、会让界面显示与实际不符）、P1 八项、P2 若干部性能打磨；`dotnet build ModelXStudio.sln` 改前改后均为 **0 警告 0 错误**，另写了一个假 HTTP 服务 + 控制台探针做行为验证，**24 条断言全部通过**（见第四节）。

| 级别 | 位置 | 问题 | 状态 |
| --- | --- | --- | --- |
| P0 | `Core/Services/DownloadService.cs:90` | 不判 HTTP 状态码，404/403 错误页被存成 `.gguf` 并显示「已下载」 | 已修 |
| P0 | `Core/Services/DownloadService.cs:148` | 连接被掐断、只收到半截数据也算下载成功 | 已修 |
| P0 | `Core/Services/DownloadService.cs:61` | 开始前已取消 → 静默 return，上层误判成功 | 已修 |
| P0 | `Core/Services/EngineManager.cs:256,293,307,138` | 启动不串行、不查端口、残留进程不清、地址栏端口与实际不一致 | 已修 |
| P1 | `Core/Services/SystemInfoService.cs:56` | 逻辑线程数翻倍显示，直接影响「我的机器能跑多大模型」的判断 | 已修 |
| P1 | `Core/Services/SystemInfoService.cs:130-135` | 读了 stdout 不读 stderr，nvidia-smi 有输出时管道缓冲写满 → 死锁到超时，显存信息丢失 | 已修 |
| P1 | `Core/Services/OpenAiClient.cs:89-100` | SSE 只 catch `JsonException`；服务回 `{"error":…}` 时抛 `KeyNotFound`/`IndexOutOfRange` 冒到界面 | 已修 |
| P1 | `Core/Services/OpenAiClient.cs:21` | 每发一条消息 new 一个 `HttpClient` | 已修 |
| P1 | `App/ViewModels/ChatViewModel.cs:226,250-252` | 报错文案「⚠️ 请求失败…」被写进对话并存盘，下一轮当成模型自己说过的话喂回去 | 已修 |
| P1 | `Core/Services/ChatStore.cs:59`、`SettingsService.cs:50` | 写盘非原子，崩溃即把原有 JSON 变成半个文件（对话/设置可能整份丢） | 已修 |
| P1 | `App/App.xaml.cs:42-44,93,141` | 未处理异常无任何提示；`_ = Task` 即发即弃抛异常直接带崩进程 | 已修 |
| P1 | `App/ViewModels/ChatViewModel.cs:64` | 历史不截断，聊到几十轮必然超上下文（服务端 500） | 已修 |
| P2 | `App/Views/ChatView.xaml.cs:36-41`、`App/ViewModels/ServerViewModel.cs:83-104` | 每个 token / 每行日志一次 `Dispatcher.Invoke` + `ScrollIntoView`；日志字符串无限累加 | 已修 |
| P2 | `App/ViewModels/ModelsViewModel.cs:385-393` 等 | 直链导入文件名未清洗会抛；GPU 版升级失败后 `EnginePreference` 停在无效值；`Progress` 里多余的 `Invoke` | 已修 |
| P2 | `App/Helpers/AppInfo.cs`、`MainWindow.xaml:11,33` | 版本号在 csproj / XAML / README 手写三份对不上，界面亦无署名 | 已修 |
| 建议 | 见第五节 | 单元测试、文件日志、DI、空 catch、KV cache 显存估算、滚动跟随、git/CI | 未改，附理由 |

第二轮（同日晚上，编译运行后反馈“标题栏与窗口底色不匹配 / 下拉框文字看不清”）另改 7 处，见第七节。

---

## 一、P0：会产出坏文件或让用户看到假状态（已修）

**1. 下载不判 HTTP 状态码。** 原实现直接把响应流拷进文件，`404 Not Found` 的 HTML 页面会被写成 `Qwen3-4B-Q4_K_M.gguf`，进度条跑到 100%，界面显示「✓ 已下载」——用户得到一个 llama-server 打不开的垃圾文件，而且原始文件已被 `File.Move(overwrite)` 覆盖。**这是本项目最坏的一个 bug**：镜像仓库改名/删库（HF 上很常见）就会触发，普通用户完全无法自救。现在先发请求、先判状态（`DownloadService.cs:90`），404/403/429 抛出「服务器返回 404。」+ 响应正文前 200 字，界面原样显示，且绝不落成品文件。

**2. 下载不完整被当成完成。** 循环读流结束时不校验「实收字节数 == Content-Length」。代理/路由器掐断长连接（几 GB 的模型下载中很常见）后，半截文件照样改名转正。现在 `total` 已知而实收不足 → 抛 `IOException("下载不完整（x/y 字节），正在续传…")`（`:148`），`.part` 保留，下一轮 `Range` 续传；三次仍不完整才如实报错。

**3. 「开始前已取消」静默成功。** 原循环条件把 `!ct.IsCancellationRequested` 写进了 for 里，取消发生在下载开始前时一次都不进循环，然后正常返回 → `ModelsViewModel` 走到「下载完成，可以加载了」。现在取消一律 `throw`（`:61`），调用方的 `catch (OperationCanceledException)` 才真的显示「已取消」。

**4. 引擎启动缺少并发与端口保护。** 原来 `StartModelAsync` 可以被重入：双击「加载」、或在「本地服务」运行中再从聊天页切模型，会出现两个 `llama-server` 抢同一端口（第二个必然启动失败，界面状态被后启动的那个覆盖）；上一次异常退出残留的 `llama-server` 也会一直占着 8108，此时界面上「启动」点了没反应。现在：

- `SemaphoreSlim _startLock` 串行化启动（`:256`），真正的流程拆到 `StartModelCoreAsync`；
- 启动前探测端口，被占则先杀掉**只属于本程序引擎目录**的残留 `llama-server`（`:293-297`，按 `MainModule` 路径前缀匹配，不会误杀别人的 llama.cpp）；仍占用就明确报「端口 8108 已被占用，请关闭占用它的程序，或在设置里换一个端口」；
- 显式传 `--host 127.0.0.1`（`:307`），不再依赖 llama-server 的默认值——将来它默认改成 `0.0.0.0` 就等于把 API 暴露到局域网，与「数据全部留在本机」的承诺冲突；
- `BaseUrl` 改为使用**实际启动成功**的端口 `_runningPort`（`:138`），改完设置里的端口不再出现「界面显示 8109、实际服务在 8108」的串台；
- 进程退出/加载超时/异常路径统一 `KillProcessNoLock()` + 清 `RunningVariant`/`_runningPort`；`Stop()` 只在确实有进程时才把状态改成 Ready（原来无条件伪报「服务已停止」）。

## 二、P1：明显缺陷（已修）

**5. 逻辑线程数翻倍。** `Detect()` 里先 `LogicalCores = Environment.ProcessorCount`，`DetectWithWmi` 里再对每颗物理 CPU `LogicalCores += lc` → 单路机器显示成两倍。本机实测修前「4 核 16 线程」，修后「4 核 8 线程」（`:56` 改成 WMI 之后才兜底赋值）。这行代码直接污染「本机硬件体检」结论，对目标用户是实质性误导。

**6. `nvidia-smi` 读取可能死锁。** `RedirectStandardError = true` 但从不读 stderr。驱动异常时 `nvidia-smi` 会往 stderr 打印大段文本，管道缓冲（约 4KB）写满后进程阻塞，`WaitForExit(6000)` 超时被杀 → 显存识别失败，全部模型卡片等级降成「未知」，用户不知道是自己的卡其实能跑。现在双路 `ReadToEndAsync` 一起读空 + `Kill(entireProcessTree: true)`（`:130-135`）。

**7. SSE 解析的异常类型不全。** 原来只 `catch (JsonException)`，但 llama-server 出错时发的是 `{"error":{"message":"..."}}`——`RootElement.GetProperty("choices")` 抛 `KeyNotFoundException`，`choices` 为空数组时 `choices[0]` 抛 `IndexOutOfRangeException`，两者都不在原 catch 范围内，异常直接从 `SendAsync` 冒到 UI。现在逐层 `TryGetProperty` + `ValueKind` 判断（`:89-95`）。

**8. 每条消息 `new HttpClient()`。** `ChatViewModel` 每条消息都 `new OpenAiClient(...)`，客户端内部再 new 一个 `HttpClient`。虽然 .NET 会复用连接池，但这是明确的反模式（`IHttpClientFactory` 出现的原因），且 100 秒 TIME_WAIT 场景下会累积 socket。改为进程内 `static readonly HttpClient`（`:21`），base 地址留在实例字段。

**9. 报错文案污染对话历史。** 失败时把「⚠️ 请求失败：…」赋给 `assistantMsg.Content`，而 `assistantMsg` 属于 `Conversation` 且随后 `ChatStore.Save()` 存盘 —— 下一轮请求会把这句报错当作模型之前的回答喂回去。模型看多了会开始模仿报错语气，且用户删不掉（已存盘）。现在用独立 `StringBuilder generated` 只累积真实增量（`:226`），落盘用 `generated.ToString()`（`:250`），内容为空就把占位消息从会话里移除（`:252`）；「⚠️ 请求失败」「…（已停止）」只存在于界面视图模型。

**10. 写盘非原子。** `File.WriteAllText` 直接覆盖：写到一半崩溃/断电/磁盘满，用户所有对话（或 settings.json）变成半个 JSON，`LoadAll` 的「损坏跳过」逻辑会把它当坏文件忽略，等于记录全丢。改为 `.tmp` + `File.Move(overwrite: true)`（`ChatStore.cs:59`、`SettingsService.cs:50`）。顺带 `SettingsService.Save()` 返回 `bool`，设置页现在能提示「保存失败：请检查磁盘空间与该文件夹的写入权限」，而不是静默失败让用户以为改成功了。

**11. 崩溃没有任何提示。** 面向「不懂技术的普通人」的应用，之前崩溃表现为窗口直接消失，没有对话框、没有日志。现在 `App.xaml.cs` 注册 `DispatcherUnhandledException` / `AppDomain.UnhandledException` / `TaskScheduler.UnobservedTaskException`（`:42-44`），写 `logs/error-yyyyMMdd.log`（`:141`）并弹中文对话框告知日志位置（`:130`）；`_ = SomeAsync()` 全部换成 `FireAndForget(work, "做什么")`（`:93`）——即发即弃的 async 里抛异常就是未处理异常直接带崩进程，硬件检测和「自动加载上次的模型」两处都有这个风险。

**12. 长对话必然超上下文。** 历史消息全量发送，不设窗口。`ContextSize` 默认 4096，几十轮之后 llama-server 必然 500，而用户只看到「请求失败」。现在截断到最近 20 条（`:64`）。注意这是**简单粗暴但正确**的策略；更合理的做法是按 token 数裁剪 + 老对话自动摘要，列在建议里。

## 三、P2：性能与体验打磨（已修）

- **聊天滚动**：原来每个增量都 `Dispatcher.Invoke`（同步阻塞引擎读取线程）+ `ScrollIntoView`，长回复时明显掉帧。改为「已排帧就不再排」的合并模式，`DispatcherPriority.Background`（`ChatView.xaml.cs:36-41`）。
- **引擎日志**：每行一次 `Dispatcher.Invoke` + 一个在 UI 线程内毫无意义的 `lock`，`LogText` 字符串无限累加后 `[^15_000..]` 切片。改为 `Queue` + `Interlocked` 排帧批量刷（`ServerViewModel.cs:83-104`）。
- **`Progress<T>` 里多余的 `Dispatcher.Invoke`**：`Progress<T>` 在 UI 线程构造时已捕获 `SynchronizationContext`，回调本来就在 UI 线程，再 `Invoke` 是白绕一层（`ModelsViewModel` 三处）。
- **直链导入**：`Path.GetFileName(new Uri(url).LocalPath)` 之后不清洗非法字符，粘贴带 `?a=b\c` 的链接会抛异常崩在页面上。现在过滤 `Path.GetInvalidFileNameChars()` + 单独 catch `UriFormatException` 给中文提示（`:385-393`）。
- **「升级到 GPU 版」失败**后 `EnginePreference` 停在中间值不再反映实际，现在回滚成 `"Cpu"` 并保存。
- **版本号**：csproj `<Version>`、`MainWindow.xaml` 的「v0.1.0 · 数据全部留在本机」、README 三处手写。集中到 `Helpers/AppInfo.cs`（读 `AssemblyInformationalVersion` 并切掉 `+commit`），XAML 用 `{x:Static}` 绑定；顺带在左下角把「作者 zhenhua」显示出来（`MainWindow.xaml:33`）。

## 四、验证方式与结果

**静态**：`dotnet build ModelXStudio.sln -v m` → 已成功生成，**0 个警告 0 个错误**（基线也是绿的，改动没有引入新警告）。

**动态**：在仓库外（`D:/tools/verify/mxstudio/`）写了一个假下载服务 + 控制台探针，覆盖改动涉及的全部行为，**结果 24 PASS / 0 FAIL**。关键几条：

| 场景 | 结果 |
| --- | --- |
| `/missing`（404 + HTML 正文） | 抛「服务器返回 404。&lt;html&gt;…」，目标文件不存在 ✅（修前：静默「成功」+ 坏文件） |
| `/trunc`（声明 100000 字节，每次只发 30000 就断） | 三次重试均不如数 → 最终抛「response ended prematurely」，**无成品文件**，`.part` 累进到 **90000 字节**可供续传，重试退避耗时 4532ms ✅ |
| `/norange`（忽略 `Range` 直接 200 全量）+ 预置 100 字节 `.part` | 丢弃错误偏移重下，结果 **1048576 字节完整** ✅ |
| 调用前已 `Cancel()` | 抛 `OperationCanceledException`，不生成文件 ✅ |
| 全量下载 | 1 MiB 首尾字节正确、无 `.part` 残留、进度含百分比与速度 ✅ |
| `catalog.json` 新增顶层 `_说明` | 仍解析出 9 条模型 ✅（`System.Text.Json` 忽略未知成员） |
| 下载链接拼接 | `https://hf-mirror.com/Qwen/Qwen3-4B-GGUF/resolve/main/Qwen3-4B-Q4_K_M.gguf`；modelscope 分支文件名空格转义 `%20` ✅ |
| 对话存取 | 原子保存可读回、无 `.tmp` 残留、删除生效 ✅ |
| 适配文案 | 8GB 显存 + 16GB 内存：`8 核 16 线程`、阈值 `≤7.2 GB`、20GB 模型判 `TooBig`、6.5GB 判 `GpuFast` ✅ |
| 真实硬件检测 | `i7-11390H` → **4 核 / 8 线程**（修前显示 16）、15.7 GB 内存、Iris Xe 2 GB ✅ |

**未做的验证**：真机 GUI 冒烟需要下载约 20MB 引擎 + GB 级模型并人工看界面，本机未执行。请你手工跑一遍：安装引擎 → 下载 Qwen3-4B（可中途点取消，验证续传）→ 加载 → 聊天（长回复看流畅度）→ 本地服务页复制 curl 调一次。另外值得故意试两次异常路径：把端口改成被占用的端口看新提示是否清晰；断网点下载看是否不再出现「已下载」。

## 五、未修改但建议（按性价比排序）

1. **补单元测试。** 本轮验证是仓库外的一次性脚本，用完即弃。`Core` 已无 WPF 依赖，`DownloadService`（配 `HttpMessageHandler` 桩）、`ModelCatalogService`、`ModelRecommender`、`ChatStore` 都很好测，建议建 `tests/ModelXStudio.Core.Tests`（xunit）。这是防止上面这些 P0 回归的唯一有效手段。
2. **建一个极简文件 logger。** 现在只有崩溃日志和 `Debug.WriteLine`（Release 下看不到）。用户反馈「下载失败」「加载不出来」时，能让他打包一个 logs 文件夹是最省事的排障方式。
3. **约 20 处 `catch {}` 空吞异常**（目录扫描、文件删除、硬件检测）。硬件检测那几处空 catch 是有意的（单项失败不影响整体），但 `ChatStore`、`EngineManager` 里几处应该至少留一行日志。
4. **`ModelRecommender` 只按权重文件体积估显存**（`Models/SystemHardware.cs:73` 用 `sizeGb <= VramGb * 0.9`），没算 KV cache 与计算图开销。8GB 卡按 7.2GB 判「可全部装入显存」偏乐观，用户把上下文开到 8K 就会爆显存或掉回内存。建议阈值降到 0.7 左右，或在文案里写明「开大上下文可能装不下」。
5. **聊天页无条件跟随到底部**：用户往上翻历史时会被新生成的内容拽下来，应改为「仅当已经在底部附近时才跟随」。
6. **DI**：`App.xaml.cs` 的静态服务定位器在当前规模（7 个服务 + 4 个 VM）够用，不必现在改；再涨就用 `Microsoft.Extensions.DependencyInjection`，否则 VM 单测会很难写（VM 直接 `App.Xxx` 取依赖）。
7. **工程管理**：`D:/projects/2017/modelx-studio` **目前不是 git 仓库**（有 `.gitignore` 但没 `git init`），`bin/`、`obj/`、`.vs/` 全在工作区里，任何误操作都无法回滚 —— 建议先 `git init` 再提交，`.gitignore` 已能覆盖生成物。另外缺 `.editorconfig`、没有 CI（`dotnet build` + 测试）、没开 `TreatWarningsAsErrors`。
8. **README 路线图里的「CUDA 启动失败自动回退 CPU」仍未实现。** 这是小白最容易卡死的一环：驱动偏旧 → CUDA 版引擎启动失败 → 界面只说「启动失败」，用户不知道要去设置里手动改成「仅 CPU」。实现上 `EngineManager` 已有 `RunningVariant`，缺的是「CUDA 失败 → 记一次数 → 自动降级启动 CPU 版并在聊天页提示这次是 CPU 推理」。建议作为下一个功能优先级最高的做。
9. **`ServerView` 的日志用 `TextBox` 绑一个字符串**，文本大时每次更新是 O(n)。真要长期挂机跑服务，建议换 `ItemsControl` + 固定容量环形缓冲。
10. **生成参数没有 `max_tokens`**：`OpenAiClient` 只发 `temperature/top_p/top_k`，长文档可能被引擎默认的 `n_ctx` 截断。加一个「最长回复」设置项更可控。

## 六、署名与功能注释覆盖情况

| 文件类型 | 数量 | 做法 |
| --- | --- | --- |
| `.cs` | 24 | 文件最顶部统一注释块：`// ====` / `// 作者：zhenhua` / `// 功能：…` / `// ====`（放在 `using` 之前，编译器与 IDE 导航都不受影响） |
| `.xaml` | 7 | 根元素之前 `<!-- 作者：zhenhua / 功能：… -->` |
| `.csproj` | 2 | `<Project>` 之前 XML 注释 |
| `Resources/catalog.json` | 1 | JSON 不支持注释，改为顶层 `"_说明": "作者：zhenhua —— …"`；`System.Text.Json` 会忽略未知成员，实测解析正常 |
| `README.md` | 1 | 正文署名并指向本报告 |
| `ModelXStudio.sln` | — | 有意不加：由 VS / `dotnet sln` 维护，手写注释可能被工具重写掉 |
| `bin/`、`obj/` 下 57 个生成文件 | — | 生成物，不加 |

界面署名：主窗口左下角「v0.1.0 · 数据全部留在本机 · 作者 zhenhua」（`MainWindow.xaml:33` ← `AppInfo.CreditLine`，版本号自动跟随 csproj）。

插入脚本是一次性的、幂等的（已含「作者：zhenhua」的文件自动跳过，保留原 BOM），放在 `D:/tools/verify/mxstudio/add_headers.py`，将来新增文件可复用同一格式。

---

## 七、第二轮：配色与标题栏（2026-11-14 晚）

用户反馈：编译运行后标题栏与窗口底色不匹配、各页下拉框里的字看不清、整体需要换一套护眼简洁的配色。

### 问题定位

| 级别 | 位置 | 问题 |
| --- | --- | --- |
| P1 | `Themes/Dark.xaml`（原版） | 主题里**根本没有** `ComboBox` / `ComboBoxItem` / `ToolTip` 样式，8 个下拉框全走 WPF 默认浅色模板：深色界面里一块白底，文字几乎不可读 |
| P1 | 系统标题栏 | 标题栏由系统画，跟 `BrushBg #14161A` 自然对不上；就算去染 DWM 也不行——实测本机 **Win10 19045** 上 `DWMWA_CAPTION_COLOR(35)` / `BORDER_COLOR(34)` / `TEXT_COLOR(36)` 全部返回 `E_INVALIDARG`（这些是 Win11 22000+ 才有的属性），Win10 只认「深色模式」开关 |
| P2 | `Themes/Dark.xaml` ScrollBar 模板 | 横向滚动条也写了 `IsDirectionReversed="True"`，滚动方向反了 |
| P2 | 10 处系统 `MessageBox` | 浅色弹窗夹在深色界面里非常跳 |

### 怎么改的

- **配色体系**：5 级表面 + 单一强调色 + 4 级文字，全部集中在 `Themes/Dark.xaml`（779 行 / 65 个资源，视图里没有一处硬编码颜色，换肤只改这一个文件）。
  - 表面由深到浅：`BrushBg #14161A` → `BrushPanel #191C22`（导航 / 标题栏）→ `BrushPanelAlt #232830`（卡片、输入框）→ `BrushPopup #262C36`（下拉面板），再加 `BrushHover #2C333D` / `BrushPressed #1B1F26` / `BrushSelected #2A3550`。不用纯黑纯白，降低长时间看着的刺目感。
  - 文字四级：`BrushText #E2E5EA`、`BrushTextDim #A0A8B4`、`BrushTextFaint #6E7784`、`BrushCaptionText #B6BDC8`。
  - **强调色拆成两套**：暗底上的文字 / 图标 / 描边用 `BrushAccent #6C8EEF`（5.82:1）；**实心按钮与勾选底**用 `BrushAccentFill #4366D0`（白字 5.19:1）。之前实心按钮直接用 #6C8EEF，白字只有 3.11:1，就是「看不清」的其中一处。
- **补齐控件样式**（全部是隐式样式，视图一行不用改）：`ComboBox`（自定义模板：圆角 8、可旋转箭头、`PART_Popup` 深色浮层 + 阴影 + Fade，可编辑态走 `PART_EditableTextBox`）、`ComboBoxItem`（深底亮字，选中项左侧 3px 竖标放在文字左边的空隙里、不压字）、`ToolTip`、`CheckBox`、`ProgressBar`、`ListBox` / `ListBoxItem`、`TextBox`、`Label`、`Window`；滚动条按 `Orientation` 分竖 / 横两套模板，修掉方向反了的问题。
- **标题栏改为自绘**：新增 `Controls/TitleBar.xaml` + `Controls/TitleBar.xaml.cs`，配 `WindowStyle="None"` + `WindowChrome(CaptionHeight=38, ResizeBorderThickness=6, UseAeroCaptionButtons=False)`。标题栏底色与左侧导航同色（VS Code / Figma 的做法），顶部到侧栏是一整块面；右上角三枚按钮走 `CaptionButton` / `CaptionCloseButton`（关闭悬停变红，符合 Windows 习惯），最大化 / 还原图标跟 `Window.StateChanged` 切换，固定尺寸的弹窗只留关闭按钮。`MainWindow` 与 `ThemedDialog` 都用它，弹窗标题绑 `Window.Title`。
- `Helpers/WindowTheme.cs` 保留但**降级为兜底**：只负责系统仍然会画的那部分（Win10 的深色模式开关、Win11 的边框 / 标题栏染色），系统不支持时静默忽略。
- **新增 `Helpers/WindowChromeFix.cs`**：无边框窗口最大化默认会把「看不见的调整边框」也算进去，实测最大化后是 `1934x1094 @ (-7,-7)`（屏幕 1920x1080、工作区 1920x1050）→ 既盖住任务栏，四边又各被裁掉一条。现在在 `WM_GETMINMAXINFO` 里按所在显示器的工作区重算，实测变成 `1920x1050 @ (0,0)`。
- `ThemedDialog`（应用内消息框，替代 10 处系统 `MessageBox`）也换成自绘标题栏，深色底 + 深色标题栏一致。
- **导航图标从 emoji 换成矢量 `Path`**（`MainWindow.xaml` 四个 `RadioButton`），并去掉 `ModelsView.xaml:60` 「🚀 …」、`:76` 「🖥️ 本机硬件体检」两个标题里的 emoji（其它页的分区标题都是纯文字，留着它们风格不统一）。WPF 不会把 emoji 渲成彩色，只会拿到一团灰色字形；导航现在是 15x15、1.3 粗细的线性图标，`Stroke` 绑 RadioButton 的 `Foreground`，悬停 / 选中时跟着文字一起变亮。正文里的「✓ 已下载」「已保存 ✓」是普通 U+2713 字符，不是 emoji，保留。
- 侧栏左下角的「版本 · 隐私 · 作者」原本挤在一行，200 宽的侧栏会把“作者”断词换行 → 拆成 `AppInfo.CreditLine` + `AppInfo.AuthorLine` 两行显示。

### 验证

- **离屏渲染 + 资源键扫描 + 对比度**：`D:/tools/verify/mxstudio/shot/`（`dotnet run --project shot.csproj`）→ **ALL PASS**：视图里引用的 20 个资源键全部存在；17 项 WCAG 对比度全过（正文 / 窗口底 14.34:1、正文 / 下拉面板 11.12:1、提示气泡 11.88:1、选中项 9.65:1、主按钮白字 5.19:1 / 悬停 4.58:1；描边类最低 3.49:1，门槛 1.5）。样张 `theme.png`（1830x1350）已目视确认下拉展开态、勾选框、滚动条、气泡与标题栏配色。
- **真机冒烟** `D:/tools/verify/mxstudio/shot/smoke.ps1` → 主窗口创建 OK、`ImmersiveDarkMode=1`、最大化正好等于工作区、无错误日志；`dotnet build ModelXStudio.sln` 仍 **0 警告 0 错误**。
- **没能做到的**：这台机器是 Win10 19045，且当前会话屏幕被锁，`CopyFromScreen` 与 `PrintWindow` 都只能拿到纯色，**真实窗口的像素级截图无法验证**，需要自己跑一次看；另外目前只有这一套深色主题，浅色主题 / 跟随系统切换仍在路线图上。

## 八、第三轮：推理引擎改为随包分发 + 开源合规（2026-11-15）

需求（用户原话摘要）：「不用下载推理引擎，把 llama.cpp 集成过来」+「README 感谢 llama.cpp」+
「注意合规，本项目也开源，但选择不能拿来商用那种协议」。

### 为什么去掉「运行时自动下载引擎」

原来 `EngineManager` 在首次加载模型时调 GitHub API 找 release、下 zip、解压 `llama-server.exe`。
问题不在代码写错，而在设计：

1. 普通用户第一次点「加载」要多等一次几百 MB 下载，中间还夹着进度条，跟「装好就能用」的定位相反；
2. 依赖 `api.github.com`（未登录每小时 60 次限流、公司代理/网络受限就直接失败），失败信息对用户不可理解；
3. 下载链路长（API → 资产列表 → zip → 解压），任何一环坏了用户都只能看到「引擎安装失败」；
4. 版本不可复现：不同用户拿到的引擎构建号不同，出问题难对齐。

改成**引擎随程序分发**：程序只在自己的安装目录里找 `llama-server.exe`，不再联网。

### 改动清单

| 位置 | 改动 |
| --- | --- |
| `Core/Models/AppSettings.cs` | 删掉「下载目录」语义；新增 `EngineRoot`（用户可自选引擎目录，优先级最高）、`EngineRootCandidates()`（自定义目录 → `runtime\llama` → 旧版 `%LOCALAPPDATA%\ModelXStudio\engine`，每次调用重算，补完文件不用重启）、`BundledEngineRoot()`（从 `AppContext.BaseDirectory` 逐级向上找 `runtime\llama`，开发时在仓库根命中，发布时命中安装目录）、`LegacyEngineDirectory`；`EnsureDirectories()` 不再创建引擎目录 |
| `Core/Services/EngineManager.cs` | 删除 `ReleasesApi`、两个资产正则、`CreateGitHubClient()`、`EnsureEngineAsync()`、`ResolveAssetUrlAsync()`、`_downloads`、`EngineStatus.DownloadingEngine`（**引擎路径上不再有任何网络调用**）；新增 `ScanExecutables()`（在候选根目录 `SearchOption.AllDirectories` 找 `llama-server.exe`，相对路径含 `cuda` 判为 GPU 版，因此官方 zip 解压自带的 `llama-b5102-bin-win-cuda-12.4-x64\` 那层也能直接认）、`FindExecutable()`（路径最短优先=规范位置优先）、`AnyInstalled`、`FolderFor()`（未装时返回应当放入的目录）、`ActiveEngineFolder`、`AssetHint()`、`InstalledEngineBuild()`（读 `VERSION.txt` 里 `b\d{3,6}`）、`EngineSummary`、`MissingEngineMessage`、`OpenEngineFolder()`；构造函数签名改为 `EngineManager(SettingsService, SystemInfoService?)`（去掉 `DownloadService`）；`KillOrphanInstances()` 改为匹配**所有**候选根目录前缀（原来只匹配旧目录，换目录后残留进程杀不掉）；`StartModelCoreAsync` 引擎缺失时直接 `SetState(Failed, null, MissingEngineMessage)`，不再触发下载；新增模型文件不存在的显式提示 |
| `App.xaml.cs:55` | `new EngineManager(SettingsService, SystemInfo)` |
| `ViewModels/ModelsViewModel.cs` | 删 `InstallEngineAsync` / `UpgradeToGpuAsync` 与 `EnginePercent`、`ShowEngineInstall`；新增 `ShowEngineMissing`、`OpenEngineFolder` / `CopyEngineLink`（复制 releases 页地址）/ `RescanEngine` 三个命令 |
| `Views/ModelsView.xaml` | 「第一步：安装引擎 + 进度条」横幅 → 橙色警示卡「缺少推理引擎（llama.cpp）」+ 三个可操作按钮；GPU 横幅由「下载 300MB 升级」改为「把 CUDA 包解压到 `runtime\llama\cuda`」 |
| `ViewModels/ChatViewModel.cs:121`、`ServerViewModel.cs:69` | 去掉 `DownloadingEngine` 分支；未安装时聊天页显示「⚠ 推理引擎文件缺失，去「模型库」页看怎么补上」 |
| `ViewModels/SettingsViewModel.cs`、`Views/SettingsView.xaml` | 新增「当前引擎」（变体 + 构建号）、「引擎目录」可浏览/可清除（`Microsoft.Win32.OpenFolderDialog`）；保存时写 `EngineRoot` 并重新扫描；底部新增「致谢与许可」卡片 |
| `Helpers/AppInfo.cs`、`MainWindow.xaml` | 侧栏底部新增 `LicenseLine`（`基于 llama.cpp（MIT）· 仅限非商业使用`）；`CreditNotice` 供设置页展示 |
| 新增文件 | `LICENSE`（本项目许可，禁止商用）、`THIRD_PARTY_NOTICES.md`、`runtime/llama/README.md`、`runtime/llama/LICENSE-MIT-llama-cpp.txt`、`scripts/get-llama.ps1` |
| `.gitignore` | **原来有一条 `*.md`**，导致 README / CODE_REVIEW 根本没被 git 跟踪（`git check-ignore -v README.md` 证实）。改为按类型忽略，并只忽略 `runtime/llama` 下的 exe/dll/zip |

模型下载（Hugging Face / hf-mirror / ModelScope）仍然联网，这是功能本身；`DownloadService` 与
`OpenAiClient` 保持不动，只是引擎不再走这条路。

### 合规是怎么考虑的

1. **能随包分发 llama.cpp 吗？** 能。llama.cpp 是 MIT（`Copyright (c) 2023 Georgi Gerganov`，已核对上游
   `LICENSE`），MIT 允许再分发与闭源/商用集成，唯一实质条件是**保留版权声明与许可文本**——所以
   `runtime/llama/LICENSE-MIT-llama-cpp.txt` 与 `THIRD_PARTY_NOTICES.md` 是「必须存在」而不是「锦上添花」，
   打包脚本/安装包必须带上它们。我们只分发官方预编译产物、不改它源码，也避免了 GPL 类传染担忧（它本来也不是 copyleft）。
2. **本项目自己的代码选什么许可？** 用户要求「开源但不能商用」。没有直接抄 PolyForm Noncommercial 英文原文
   （本机取不到官方文本，抄半截反而危险），而是自撰中文《ModelX Studio 非商业使用许可 1.0》：
   先定义什么算「商业使用」（出售/出租/付费服务/SaaS/集成进商业产品/以它为主要卖点的收费服务），
   再授予非商业范围的著作权，附分发条件（保留 LICENSE、保留作者署名、保留上游致谢、说明改动），
   保留双重许可权利、写明自动终止与无担保。歧义条款明确「按允许非商业、禁止商用的目的解释」。
3. **边界要说清楚，否则容易误导下游**：许可声明了三件事——上游组件仍按各自 MIT（我们没有资格替它们放宽或收紧）；
   模型权重不属于本项目、各自遵守原始许可；本项目的代码禁商用 ≠ 手里的模型能商用。
   `THIRD_PARTY_NOTICES.md` 第 4 节的模型许可表逐条查了来源（Qwen3 = Apache-2.0 官方 README 原话；
   Phi-4-mini = 模型卡 `license: mit`；Gemma 3 = Gemma Terms of Use；Llama 3.2 = Community License，
   月活 7 亿门槛 + 命名要求 + 多模态款地区限制；DeepSeek-R1-Distill-Qwen 仓库标 MIT 但蒸馏基座是
   Apache-2.0、上游有争议帖），并注明「上游会改，商用前以原文为准」。
4. **署名与致谢落到位**：README 顶部、独立的「致谢」章节、License 章节、页面底部一句话声明；
   应用内设置页底部「致谢与许可」卡片 + 侧栏 `基于 llama.cpp（MIT）· 仅限非商业使用`；
   40 个手写源文件头统一加「许可：…（上游第三方组件按各自 MIT 许可授权）」，
   `catalog.json` 加 `_许可` 键（`System.Text.Json` 忽略未知成员，解析不受影响，已由探针验证）。

### 验证

- `dotnet build ModelXStudio.sln` → **0 警告 0 错误**。
- 新增引擎行为探针 `D:/tools/verify/mxstudio/engine_probe/`（假 exe + 假 cuda 嵌套目录，不启真进程）
  → **57 项断言全 PASS**：缺失时状态/多行指引内容（含目标目录、releases 页、包名、要重启）、
  `cpu\` 与 `llama-b5102-bin-win-cuda-12.4-x64\` 两种摆法识别、`EngineRoot` 优先级、清空后回落、
  旧版目录里的老引擎仍被找到（免重下）、`VERSION.txt` 构建号、`EngineSummary` 文案、
  GPU 提示的四种条件组合、`EngineManager` 源码里已无 `api.github.com` / `DownloadService` / 解压调用、
  LICENSE 与第三方清单/随包 MIT 文本/README 致谢/`.gitignore` 不再屏蔽 `*.md` 等合规文件检查、
  `catalog.json` 加键后 9 条卡片与下载地址仍正常。
- 主题/视图侧回归：离屏渲染 harness **ALL PASS**（主题 65 资源；视图引用的 22 个资源键全部存在；
  17 项 WCAG 对比度全过，最低 4.58:1）；`smoke.ps1` **ALL PASS**（真 App 起得来、最大化正好铺满工作区、
  无错误日志）。
- **没做到的**：这台机器完全无外网（`curl` 到 github.com / api.github.com / hf-mirror 全部
  `http_code=000`），所以 ① `runtime/llama` 里**目前还没有真的 llama.cpp 二进制**，需要你在有网的机器上跑
  `scripts/get-llama.ps1`；② 该脚本只做了语法解析检查（`PSParser` 0 错误）与逻辑走查，没有实跑下载；
  ③ `OpenFolderDialog` 与「打开引擎文件夹」需要真机点一下；④ 屏幕被锁导致真实窗口像素截图仍不可行。

## 九、第四轮：用户放置引擎后的核查与 Vulkan 支持（2026-11-15）

用户手工把 llama.cpp 放进了 `runtime/llama`，要求「检查一下是否正确」。结论：**能跑，但原实现会用不上他放的那份加速引擎**，改了 3 处。

### 实测到的事实

| 目录 | 体积 | `--list-devices` 实测 |
| --- | --- | --- |
| `cpu\` | 64,674,077 B（52 个文件） | `(none)` |
| `cuda\cuda-12.4-x64\` | 747,478,301 B（53） | `(none)` |
| `cuda\cuda-13.5-x64\` | 257,267,485 B（53） | `(none)` |
| `vulkan\` | 106,838,301 B（53） | **`Vulkan0: Intel(R) Iris(R) Xe Graphics (8061 MiB, 7402 MiB free)`** |

四个 `llama-server.exe` 都是 9,728 B 且 SHA256 相同（`1C7872A8…`），`llama-server-impl.dll` 也相同
（`47F6CB76…`）——新版官方包是「小启动器 + impl dll + 后端 dll」，差别只在 `ggml-cuda.dll` /
`ggml-vulkan.dll`。CUDA 12.4 的 `ggml-cuda.dll` 单文件 682,804,224 B，因为 cuBLAS 是**静态链进去**的
（目录里没有独立 `cudart64_*.dll` / `cublas64_*.dll`）。本机 `nvidia-smi` 与 `cudart*.dll` 都不存在 ⇒
**这台机器没有 N 卡，两份 CUDA 共 959 MB 在本地永远用不到**（对别的用户仍有意义）。
参数兼容性另用 `--help`（778 行）核对过：`--host/--port/-c/--temp/--top-p/--top-k/--n-gpu-layers/--jinja/--no-webui`
在 `b12387` 全部存在，EngineManager 拼的命令行没问题。构建号 `version: 1.0.0-dev (build 12387, commit a956a852c)`，
`built with Clang 20.1.8 for Windows x86_64`。

### 三个真问题与改法

| 问题 | 后果 | 改法 |
| --- | --- | --- |
| `EngineManager` 只认路径里的 `cuda`，`vulkan\` 被判成 **CPU 版** | 用户特意放的核显加速版永远不会被选中，白占 102 MB | `EngineVariant` 加 `Vulkan`；`Classify()` 先判 `vulkan` 再判 `cuda`；`DirName`、`VariantName`、`AssetHint`、`EngineSummary`、加载文案全部补齐；`--n-gpu-layers` 条件由 `== Cuda` 改成 `== Cpu ? 0 : 999`（Vulkan 也要全量卸载） |
| 同变体放了两份 CUDA，`OrderBy(e => e.Exe.Length)` 长度一样 ⇒ **选哪份不确定** | 换机器/重排目录后可能突然改用 13.5，驱动旧就直接起不来 | `FindExecutable` 排序改 `OrderBy(FlavorRank)`（正则 `(?:cuda\|cublas)[-_ ]?(\d+)` 取主版本号，取不到=0 表示规范目录优先）`.ThenBy(长度).ThenBy(路径)` → 固定选 12.4，且只有 13.5 时仍能找到 |
| `PickStartVariant` 只会二选一，Intel 核显机器拿不到加速 | 这台机器（Iris Xe 有 8 GB 共享显存）会走纯 CPU | Auto 顺序改为：`HasNvidia() && 装了 CUDA` → CUDA；否则 `HasAnyGpu() && 装了 Vulkan` → **Vulkan**；否则 `FirstInstalledVariant()`。显式指定 CUDA/Vulkan/Cpu 时优先用它，**没放则退回能用的那份而不是报错** |

顺带：`GpuUpgradeAvailable` 增加 `&& !IsInstalled(Vulkan)`（两种加速都缺才提示）；
`MissingEngineMessage` 在推荐 CPU 时多两行，告诉非 N 卡用户改用 Vulkan 包与其目标目录；
设置页「引擎偏好」下拉加 `Vulkan 加速（A 卡 / 核显）`（`SettingsViewModel` 三处映射 + ToolTip 同步，
标签文案改动要同时改 `EnginePrefLabels`、读取映射、保存映射）；GPU 提示条文案补 Vulkan 退路。

### 补齐的随包文件

- `runtime/llama/VERSION.txt`：用户是手工放的、当时没有这个文件 ⇒ 界面显示不出引擎版本。现按取包脚本的
  格式写好（`version=b12387`、`commit`、`built_with`、四个变体的 `dir/files/bytes/ggml_*_dll_bytes/sha256 前 16 位/
  requires/note`）。注意 `InstalledEngineBuild()` 用正则 `b\d{3,6}` 取**第一个**匹配，所以文件首行注释里不能出现 `b` + 数字。
- 每个变体目录各放一份 `LICENSE-llama-cpp.txt`（用户放的 zip 里只有 `LICENSE-LLVM-OpenMP`，**缺 llama.cpp 的 MIT 文本**；
  MIT 的硬性条件就是副本必须带许可文本）。zip 自带的 `LICENSE-LLVM-OpenMP` 保留（`libomp.dll` 是 Apache-2.0 WITH LLVM-exception）。
- `THIRD_PARTY_NOTICES.md` 新增 §1.1：三个后端各自牵扯的许可 —— `libomp.dll`（LLVM）、Vulkan 靠系统驱动 ICD（无额外义务）、
  **CUDA 版静态链入 cuBLAS ⇒ 受 NVIDIA CUDA 工具包 EULA 的再分发条款约束**（官方 zip 不带 NVIDIA 许可文本，我们不自行编造，批量发行前需自己读一遍）；
  并量化了「删掉 zip 里 `llama-cli`/`llama-bench`/`llama-quantize` 等工具每份只省约 19 MB」⇒ 默认全保留。
- `runtime/llama/README.md`、根 `README.md`：目录树改成实际四份摆法、写明「含 cuda 归 CUDA、含 vulkan 归 Vulkan」、
  Auto 选择顺序与两份 CUDA 取 12.4 的规则、体积与「只带 CPU + Vulkan（约 164 MB）就能覆盖三家显卡」的建议。

### 取包脚本 `scripts/get-llama.ps1` 本轮修的问题

| 严重度 | 问题 | 改法 |
| --- | --- | --- |
| **P0（脚本本来跑不起来）** | `$cpuUrl` / `$cuUrl` 在两个分支里算好之后，又被无条件 `$cpuUrl = ''` / `$cuUrl = ''` 复位，紧接着就传给 `Install-Engine` 去 `Invoke-WebRequest` ⇒ **首次执行必败**，而且报错是空的 Uri | 删掉复位；`Install-Engine` 开头加 `[string]::IsNullOrWhiteSpace($downloadUrl)` 前置报错 |
| 合规缺口 | 只拷 `llama-server.exe` + `*.dll`，**不拷许可文本** ⇒ 脚本装出来的目录缺 MIT 文本（MIT 的硬性条件就是副本必须带许可） | `LICENSE*` 一并保留；zip 里没有就用仓库内 `runtime\llama\LICENSE-MIT-llama-cpp.txt` 补为 `LICENSE-llama-cpp.txt`；两者都没才 `Write-Warning` |
| 文档与实现不一致 | README 推荐「只带 CPU + Vulkan」，但脚本没有 `-Vulkan` | 新增 `-Vulkan`（资产正则 `^llama-b\d+-bin-win-vulkan-x64\.zip$`），可与 `-Cuda` 同时用 |
| 目录结构与程序选版不对应 | CUDA 一律展平到 `cuda\`，与手工放置的 `cuda\cuda-12.4-x64\` 不一致；两份 CUDA 并存时程序无法判断主次 | 按资产名生成 `cuda\cuda-12.4-x64\`（正则 `cuda[-_]?(\d+(?:\.\d+)?)`）；多 CUDA 资产时固定优先 12.x（跟 `FlavorRank` 一致）；新增 `-CudaAssetName` 取其它 CUDA 版本 |
| 可用性 | `Invoke-WebRequest` 失败时只报原始异常；zip 里没 `llama-server.exe` 时文案不够用；`-Build` 示例还是旧号 5102 | `Get-RemoteFile` 包一层，提示含资产名 / 异常 / 地址 / 常见原因（无外网、代理、该构建没这个资产）；补「资产结构可能变了，请手动解压」；示例改 `-Build 12387` |
| 可追溯 | `VERSION.txt` 无表头、键名与手工放置那份不一致（`version:` vs `version=`） | 统一成 `version=` + 每个变体一个 `[目录]` 分节（`asset/sha256/zip_bytes`），并写明「第一处 `b`+数字 必须就是构建号」（程序用 `b\d{3,6}` 取首个匹配） |

验证用 `D:/tmp/feng/ps_test.ps1`：用 `[System.Management.Automation.Language.Parser]::ParseFile` 从 AST 里把
`Install-Engine` / `Get-DownloadUrl` 两个 `FunctionDefinitionAst` 单独 `Invoke-Expression` 载入（避开脚本主体的联网部分），
再拿一仿官方结构的假 zip（`llama-b9999-bin-win-cpu-x64/` 内含 `llama-server.exe`、`ggml.dll`、`ggml-cpu.dll`、
`llama-cli.exe`、`README.md`、`LICENSE`、`LICENSE-LLVM-OpenMP`）跑 **18 PASS / 0 FAIL**：直链拼接、真实不可达地址时报错文案
含「下载 … 失败」与「常见原因」、展平、只保留 exe/dll/LICENSE（`llama-cli.exe` 与 `README.md` 不落地）、SHA256 长度 64、
返回记录字段、缺 LICENSE 时补到 `cuda\cuda-12.4-x64\`、缺 exe 报错、空地址报错、CUDA 目录名解析出 `cuda-12.4-x64`。

#lesson 语法检查（`PSParser::Tokenize` 回 0 错误）**不等于能跑**：上面的 `$cpuUrl` 复位是纯逻辑 bug，tokenize 完全看不出。
PowerShell 脚本要用 AST 扣出函数 + 假数据实跑。

### 验证

- `dotnet build ModelXStudio.sln` → **0 警告 0 错误**（中途 2 个 `CS0019: 运算符"??"无法应用于"string"和"EngineVariant"` ——
  在 `PickStartVariant` 里误用了返回路径的 `FirstInstalled()`，改为新增的 `FirstInstalledVariant()`）。
- `engine_probe` 扩到 **88 PASS / 0 FAIL**（新增 C6–C15：Vulkan 识别/路径/`EngineSummary` 三变体/资产名/中文名/
  两份 CUDA 固定选主版本号小的/只剩 13.5 仍可用/删除后不再误判；E0 前置断言；H3/H4 中文名；G17–G21 源码级回归护栏：
  枚举含 Vulkan、分类时识别 vulkan 目录、`--n-gpu-layers` 条件是 `variant == EngineVariant.Cpu ? 0 : 999`、
  引擎偏好四个标签齐全且每个标签在「下拉列表 + 双向映射」里至少出现两次（漏一个就会显示为空）；
  **注意旧断言 `EngineSummary == "CPU 版 + GPU 版"` 与 `VariantName(Cuda) == "GPU 加速版"` 已随文案改为「CUDA 版 / CUDA 加速版」**；
  G17 首次就抓到了 `{ Cpu, Cuda, Vulkan}` 少个空格，顺手格式化）。
- 新增 **`D:/tools/verify/mxstudio/discovery_check/`**：把输出目录建到
  `src/ModelXStudio.App/bin/Debug/net8.0-windows\_probe` 再运行，用 `AppContext.BaseDirectory` 复现真实程序的查找路径 ⇒
  **10 PASS / 0 FAIL**：`BundledEngineRoot = D:\projects\2017\modelx-studio\runtime\llama`、
  零配置 `AnyInstalled=true`、cpu/vulkan 路径都在随包目录内、`cuda` 解析到 `cuda\cuda-12.4-x64\llama-server.exe`、
  构建号 `b12387`、`EngineSummary = CPU 版 + CUDA 版 + Vulkan 版 · b12387`（查完删掉 `_probe` 目录）。
- 四个变体 `llama-server.exe --list-devices` 都能正常跑完（无缺 dll 报错）。
- 取包脚本：`PSParser` 语法 **0 错误**，AST 抽函数 + 假 zip 实跑 **18 PASS / 0 FAIL**（见上一小节）。
- 配色 harness `shot` 与真机冒烟 `shot/smoke.ps1` 均 **ALL PASS**（主题 65 资源 / 视图 22 个资源键全在 /
  17 项 WCAG 最低 4.58:1；App 能起、最大化 1920x1050 @ (0,0)、无错误日志），
  说明 `SettingsView.xaml` / `ModelsView.xaml` 本轮改动没有引入运行时错误。

### 仍未验证 / 需要你做

1. **端到端加载没做**：`%LOCALAPPDATA%\ModelXStudio\models` 是空的且本机无外网 ⇒ 没有任何 `.gguf` 可加载，
   「Vulkan 真的把层卸进 Iris Xe、速度提升多少」需要你自己下一个 1–2 B 的 Q4 模型点「加载」看日志。
2. 建议实测一次 CUDA 缺失路径：把 `cuda\` 临时改名，确认界面显示「CPU 版 + Vulkan 版」且仍能加载（不改设置也不报错）。
3. 若准备发安装包：考虑只带 `cpu + vulkan`（约 164 MB）而不是现在的 1.1 GB；带 CUDA 时把 NVIDIA 再分发条款过一遍。
4. 本轮改动仍未 `git commit`。
