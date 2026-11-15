# ModelX Studio

**ModelX Studio** 是一款参考 [LM Studio](https://lmstudio.ai) 设计的 Windows 桌面本地大模型工具，目标用户是**不懂技术的普通人**：下载模型 → 点击加载 → 直接聊天，全程中文、无需命令行。

> 作者：**zhenhua** ｜ 许可：**开源但禁止商用**（完整条款见 [LICENSE](LICENSE)）｜ 推理引擎来自 [llama.cpp](https://github.com/ggml-org/llama.cpp)（MIT，见 [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md)）
>
> 每个源文件开头都写有作者与本文件功能说明；代码审查与优化记录见 [CODE_REVIEW.md](CODE_REVIEW.md)。

## 与 LM Studio 的差异（更简单）

| 功能 | LM Studio | ModelX Studio |
| --- | --- | --- |
| 模型下载 | 需搜索 HuggingFace 仓库、挑量化后缀 | 内置精选模型卡片，一键下载推荐量化，国内默认走 hf-mirror 加速 |
| 引擎管理 | 手动选 runtime | llama.cpp 随程序分发，不需要在线下载，也不会“第一次用先等半小时” |
| 参数 | 大量高级参数 | 只保留「快速 / 均衡 / 高质量」三档风格 |
| 硬件匹配 | 无提示 | 自动检测 CPU/内存/显卡显存，逐模型标注适配等级 |
| 本地 API 服务 | 需配置 | 一键启停，OpenAI 兼容地址一键复制 |

## 核心功能

- 💬 **聊天**：流式回复、多会话管理、模型一键切换、Enter 发送
- 🚀 **GPU 加速**：有 NVIDIA 显卡优先走 CUDA，没 N 卡但有其它显卡（**包括 Intel / AMD 核显**）走 Vulkan，都没有才用 CPU（`--n-gpu-layers` 全量卸载）；只有 CPU 版时，模型库页会直接告诉你把加速包放到哪个目录；设置中可强制「自动 / CUDA / Vulkan / 仅 CPU」
- 🖥️ **硬件体检**：自动检测 CPU / 内存 / 显卡显存（NVIDIA 用 nvidia-smi 精确读取，其余走 WMI），生成「你的机器适合多大模型」建议；每个模型卡片按显存/内存实时标注 🟢 GPU 全载 / 🔵 CPU 可跑 / 🟡 偏紧 / ⚠️ 带不动
- 📚 **模型库**：精选 9 款主流 GGUF 模型（Qwen3 / DeepSeek-R1 / Gemma 3 / Llama 3.2 / Phi-4），断点续传下载；支持粘贴任意 `.gguf` 直链导入；删除 / 定位文件
- 🔌 **本地服务**：一键启动 OpenAI 兼容 API（仅监听 127.0.0.1，端口默认 8108），复制 curl 示例
- ⚙️ **设置**：模型目录、端口、上下文长度、下载镜像（默认 hf-mirror.com）

所有数据（模型、对话、设置）都保存在本机 `%LOCALAPPDATA%\ModelXStudio\`，不上传任何内容。

## 技术架构

```
src/
├── ModelXStudio.Core/          # 纯 .NET 8 类库（无界面依赖，可在任何平台编译/测试）
│   ├── Models/                 # AppSettings / Catalog / Conversation
│   ├── Services/
│   │   ├── EngineManager.cs    # 在随包目录里找 llama-server.exe 并管理子进程（不联网）
│   │   ├── OpenAiClient.cs     # /v1/chat/completions SSE 流式客户端 + 内存检测
│   │   ├── DownloadService.cs  # 断点续传下载
│   │   ├── ModelCatalogService.cs
│   │   └── ChatStore.cs / LocalModelScanner.cs / SettingsService.cs
│   └── Resources/catalog.json  # 精选模型目录
└── ModelXStudio.App/           # WPF 界面（.NET 8，深色主题，CommunityToolkit.Mvvm）
    ├── Views/                  # 聊天 / 模型库 / 本地服务 / 设置
    ├── ViewModels/
    └── Themes/Dark.xaml
```

推理引擎采用 [ggml-org/llama.cpp](https://github.com/ggml-org/llama.cpp) 的 `llama-server`（Windows x64），应用通过它的 OpenAI 兼容 HTTP 接口对话。这样聊天和「本地服务」共用同一个引擎实例，架构简单可靠。

## 推理引擎：随程序分发，不再在线下载

早期版本会在首次使用时去 GitHub 拉 llama.cpp，实际体验很坑：公司网 / 代理下不通报错、GitHub API 限流、
下到一半断掉、用户根本不知道在下什么东西。现在改成**引擎跟程序一起分发**：

```
runtime/llama/
├── cpu/                    # CPU 版：人人能跑，速度最慢（约 62 MB）
├── vulkan/                 # Vulkan 版：N / A 卡与 Intel 核显都能用（约 102 MB）
├── cuda/cuda-12.4-x64/     # CUDA 12.4 版：N 卡，驱动要求宽（约 713 MB）
├── cuda/cuda-13.5-x64/     # CUDA 13.5 版：需要较新驱动（约 245 MB）
├── VERSION.txt                        # 构建号 / SHA256（界面显示引擎版本就读它）
├── LICENSE-MIT-llama-cpp.txt          # 上游 MIT 许可文本，随包必须带着
└── README.md                          # 该放哪些文件、怎么选后端、去哪下载
```

每个变体目录里都各带一份 MIT 许可文本（MIT 的硬性条件就是副本必须带许可）。

查找顺序：设置里手动指定的目录 → `runtime\llama` → 旧版 `%LOCALAPPDATA%\ModelXStudio\engine`（老用户不用搬文件）。
每个目录里递归找 `llama-server.exe`，**路径里带 `cuda` 归为 CUDA 版、带 `vulkan` 归为 Vulkan 版**，
所以官方 zip 解压自带的那层目录（`llama-b12387-bin-win-cuda-12.4-x64\`）也能直接认出来。

「自动」下的选择顺序：有 N 卡且放了 CUDA 版 → CUDA（同时放了 12.4 与 13.5 时**固定选主版本号小的 12.4**，
对驱动版本要求更宽）→ 没 N 卡但本机有显卡且放了 Vulkan 版 → Vulkan → 其余用 CPU。
强制指定的那份没放时，程序退回能用的那份，不会报错。

做安装包时不必三份都带：只带 **CPU + Vulkan**（约 164 MB）就能覆盖 N / A / Intel 三种显卡；
只有明确面向 N 卡用户才加 CUDA（CUDA 二进制还额外受 NVIDIA 工具包再分发条款约束，见 `THIRD_PARTY_NOTICES.md`）。

取引擎只需一条命令（在能上网的机器上跑一次）：

```powershell
powershell -ExecutionPolicy Bypass -File scripts\get-llama.ps1                # 只要 CPU 版
powershell -ExecutionPolicy Bypass -File scripts\get-llama.ps1 -Vulkan       # CPU + Vulkan（覆盖 N / A / Intel 显卡，约 164 MB）
powershell -ExecutionPolicy Bypass -File scripts\get-llama.ps1 -Cuda -Vulkan  # 三个后端全带上
```

缺引擎时不会默默重试或抛「网络错误」，而是把目标目录、需要哪个包、官方下载地址一次列清楚，
并给「打开引擎文件夹 / 复制下载地址 / 我已放好，重新检查」三个按钮。

## 在 Windows 上构建

1. 安装 [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)（或 Visual Studio 2022 17.8+，勾选「桌面开发」）
2. 取推理引擎（clone 里没有二进制，见上一节）：`powershell -ExecutionPolicy Bypass -File scripts\get-llama.ps1`
3. 编译运行：

```powershell
git clone <本仓库>
cd modelx-studio
powershell -ExecutionPolicy Bypass -File scripts\get-llama.ps1
dotnet run --project src/ModelXStudio.App
```

发布单文件版本：

```powershell
dotnet publish src/ModelXStudio.App -c Release -r win-x64 --self-contained -p:PublishSingleFile=true
```

> 本仓库在非 Windows 机器上可用 `dotnet build src/ModelXStudio.Core` 验证核心类库；
> WPF 项目需在 Windows 上编译运行。

## 使用流程（给普通用户）

1. 确认顶部没有「缺少推理引擎（llama.cpp）」提示（自己编译的话先跑一次 `scripts\get-llama.ps1`）
2. 在精选模型里点「下载」（推荐 Qwen3-4B，约 2.5GB，国内镜像加速）
3. 下载完点「加载」→ 去「聊天」页开聊
4. 想让其它软件调用：「本地服务」→ 启动 → 复制地址

## 代码审查与优化（2026-11-14）

对 `src/` 全部 40 个源文件做过一轮审查并修掉 14 处问题，详见 **[CODE_REVIEW.md](CODE_REVIEW.md)**。其中影响最大的四处（都会让用户拿到「看起来成功、其实不能用」的结果）：

1. 下载不校验 HTTP 状态码 → 镜像仓库 404 时，错误页 HTML 会被存成 `.gguf` 并显示「已下载」（现已抛「服务器返回 404」并保留中文原因）
2. 连接中断、只收到半截数据也算下载完成 → 半截模型文件（现校验总字节数，保留 `.part` 续传）
3. 逻辑线程数显示翻倍 → 「本机硬件体检」结论误导用户选大模型（现修正，如 4 核 8 线程不再显示成 16）
4. 推理引擎启动不串行、不查端口、残留进程不清 → 双击加载/改端口后界面状态与实际不符（现已加启动锁、端口探测与残留清理，并显式 `--host 127.0.0.1`）

另外：请求失败的文字不再被写进对话历史回喂给模型；对话与设置改为原子写入；未处理异常有中文提示和 `logs/error-*.log`；长对话按最近 20 条截断；聊天滚动与引擎日志改为按帧合并刷新。验证方式与 24 条行为断言结果见报告第四节。

## 界面配色（2026-11-14 第二轮）

全部颜色集中在 `src/ModelXStudio.App/Themes/Dark.xaml` 一个文件里（界面代码无一处硬编码颜色），换配色只改这一个文件：

- **深色护眼基调**：不纯黑、不纯白，靠 5 级表面（窗口底 `#14161A` → 导航/标题栏 `#191C22` → 卡片 `#232830` → 下拉面板 `#262C36`）拉开层次，只用一个靛蓝强调色；正文 `#E2E5EA` 与窗口底对比度 14.3:1（WCAG AAA）。
- **实心按钮用单独一套色**（`#4366D0`）：直接用强调色 `#6C8EEF` 做底时白字只有 3.1:1，看不清；换成深一档后 5.2:1。
- **补齐了下拉框 / 提示气泡 / 复选框 / 进度条 / 滚动条样式**（之前主题里没有 ComboBox 样式，用的是 WPF 默认白底模板，在深色界面里文字几乎不可读）。
- **标题栏自绘**（`Controls/TitleBar.xaml`）：Win10 系统不允许给标题栏配色（`DWMWA_CAPTION_COLOR` 是 Win11 22000+ 才有的属性），所以改成无边框 + 自绘标题栏，颜色与左侧导航完全一致；配套 `Helpers/WindowChromeFix.cs` 修好了最大化时盖住任务栏、四边被裁的问题。`Helpers/WindowTheme.cs` 保留作为 Win11 上的额外润色。
- **弹窗也不再跳白**：10 处系统 `MessageBox` 换成应用内的 `ThemedDialog`（深色、同样自绘标题栏、支持 Esc）。

验证：离屏渲染样张 + 资源键扫描 + 17 项 WCAG 对比度全部通过（脚本 `D:/tools/verify/mxstudio/shot/`）；真机启动、DWM 属性回读、最大化边界均 PASS。详见报告第七节。

## 界面细节修复（2026-11-15 第三轮）

这一轮只处理反馈的界面问题，不改功能：

- **窗口最大化后不再「左边一堆内容、右边一条空白」**：模型库 / 本地服务 / 设置三个页面原本是「限宽 + 靠左」，
  WPF 会把剩下的宽度全留在右边；改成「限宽 + 居中」，并把模型库的宽度上限从 980 提到 1440（大屏上卡片能多排一列）。
  聊天页是相反的毛病——气泡会分别顶住屏幕左右两条边，现在消息和输入框共用一条 1000 px 的居中列。
- **模型适配等级改成真正的「指示灯」**：原先文案里的 🟢🔵🟡 在 WPF 里画不出彩色，只会成一个灰白空心圈，
  看着就是「灯没亮」。现在用矢量小灯表示：绿＝能整份装进显存（速度最快）、蓝＝内存跑得动、黄＝内存偏紧张、
  红＝超出本机能力，还没读到硬件信息时整颗灯不显示。灯芯带高光和柔光晕，在深色底上是「亮着」的样子。
- **下载中的百分比与速度不再左右跳**：两个数字各占固定宽度并右对齐（等宽数字），从 `9%` 到 `100%`、
  从 `1.2 MB/s` 到 `123.4 MB/s` 都不会推着整行移动；「下载 / 取消」按钮也改成同宽，切换时不闪位。
- **下拉框没有数据时不再是一条又窄又空的黑色框**：下拉面板至少和下拉框一样宽，并在中间写一句下一步该干什么
  （例如「还没有已下载的模型，先到「模型库」下载」），没写提示的下拉框用默认的「暂无可选项」。

验证方式与样张见报告第十节（harness 在 `D:/tools/verify/mxstudio/led/`）。

## 界面宽度与聊天输入框（2026-11-15 第四轮）

- 各页面最大宽度集中在 `src/ModelXStudio.App/Themes/Dark.xaml` 一处：`WidthPageWide 2200`（模型库）、`WidthPageMedium 1600`（本地服务）、`WidthPageForm 1520`（设置）、`WidthChat 1400`（聊天列与输入行同一个宽度，保证左边缘对齐）、`WidthChatBubble 1000`（单条气泡的行长）。想更宽就改这几个数，想彻底铺满改成 `10000`。
- 限宽容器一律 `MaxWidth` + `Stretch`（默认就是 Stretch，不用特意写），**不要写 `HorizontalAlignment="Center"`**：Center 是「按内容期望宽度收缩再居中」，容器里有 `*` 列时会缩成中间一条，两边反而更空。
- 聊天输入框默认 100 px 高（约 5 行），内容多了最长涨到 320 px，再长内部滚动。

## 后续路线图

- [ ] 模型首次加载进度百分比（解析 llama-server 日志）
- [ ] Vulkan 引擎变体（AMD/Intel 显卡加速）+ AMD 显存精确识别
- [ ] 引擎启动失败时自动回退：CUDA 版跑不起来（驱动过旧等）自动切回 CPU 版
- [ ] 回复 Markdown 渲染与代码高亮
- [ ] 系统托盘常驻 + 全局快捷键唤起聊天窗
- [ ] 安装包（MSIX / Inno Setup）与代码签名（打包时一并携带 llama.cpp 与它的 MIT 许可文本）
- [ ] 随包引擎自检：启动时校对 `VERSION.txt` 里的 SHA256，DLL 缺失 / 版本不匹配时给明确提示

## 致谢

- **[llama.cpp](https://github.com/ggml-org/llama.cpp) / [GGML](https://github.com/ggml-org/ggml)**（Georgi Gerganov 及所有 contributors）——本项目的推理能力全部来自它。
  没有 llama.cpp 就没有 ModelX Studio：它把“在普通 Windows 电脑上跑大模型”从一件要编译、配环境、查报错的事，
  变成了一个 exe 就能跑的事。本项目使用它的官方 Windows 预编译构建（`llama-server.exe`）并通过它自带的
  OpenAI 兼容接口对话，**没有修改它的源码**，并随包分发它的 MIT 许可文本（`runtime/llama/LICENSE-MIT-llama-cpp.txt`）。
- **[CommunityToolkit.Mvvm](https://github.com/CommunityToolkit/dotnet)**（.NET Foundation，MIT）——省掉 MVVM 样板代码。
- **.NET 8 / WPF**（Microsoft，MIT），以及 Hugging Face / hf-mirror / ModelScope 上开源模型的各家发布方。

完整第三方清单、许可原文与再分发注意事项：[THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md)。

## License

**ModelX Studio 开源，但只能非商业使用。** 完整条款就在本仓库根目录的 [`LICENSE`](LICENSE) 文件里（仓库页面上直接可见），要点：

- 你可以自由使用、阅读、修改、学习，也可以**非商业**地再分发（含修改版）。
- **禁止商用**：出售或出租本软件、把它做成付费服务 / SaaS / 企业内商用系统、集成进对外销售或运营的产品、
  以它为主要卖点开展收费服务，都需要先取得作者的书面授权。
- 分发时必须保留 `LICENSE`、源代码里的作者署名、以及对 llama.cpp 的致谢，并说明你改了什么。
- **上游组件不受本协议限制**：llama.cpp（MIT）、CommunityToolkit.Mvvm（MIT）、.NET / WPF（MIT）仍按各自的
  MIT 授权（允许商用），本文件不改变也不限制它们——但我们没资格替它们放宽，也没资格替自己收紧它们。
- **模型权重不属于本项目**：Qwen / Gemma / Llama / DeepSeek / Phi 各有自己的许可，其中 Gemma Terms of Use、
  Llama Community License 等自带商用限制，能否商用请以各模型仓库里的 LICENSE 原文为准。

需要商业授权，或你的场景（企业内网统一部署、教学机构批量分发等）确实满足不了上面条件，请开 Issue 或通过仓库上登记的联系方式联系作者。

---

**致谢与许可**：本工具基于 [llama.cpp](https://github.com/ggml-org/llama.cpp)（GGML 项目，MIT 许可）构建，感谢作者
Georgi Gerganov 与全体 contributors。ModelX Studio 由 **zhenhua** 开发并开源，**仅限非商业使用**，完整条款在本仓库根目录
[`LICENSE`](LICENSE) 内声明；上游第三方组件仍按各自 MIT 许可授权，模型权重遵守各模型自己的许可协议，
详见 [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md)。

