# llama.cpp 引擎目录（随程序分发）

本程序**不再联网下载**推理引擎。推理由 [llama.cpp](https://github.com/ggml-org/llama.cpp)
提供的 `llama-server.exe` 完成，按下面的摆法放进来即可（llama.cpp 是 MIT 许可，
可以随包分发，许可文本见同目录 `LICENSE-MIT-llama-cpp.txt`，每个变体目录里也各放了一份）。

## 当前目录里有什么

```
runtime\llama\
├── VERSION.txt                        # 构建号 / 来源 / 许可，界面显示的引擎版本从这里读
├── LICENSE-MIT-llama-cpp.txt          # llama.cpp 的 MIT 全文（分发时必须带上）
├── cpu\                               # CPU 版：人人能跑，速度最慢
│   ├── llama-server.exe  + *.dll
│   └── LICENSE-llama-cpp.txt
├── cuda\
│   ├── cuda-12.4-x64\                 # CUDA 12.4 版（N 卡，需要支持 CUDA 12.4 的驱动）
│   └── cuda-13.5-x64\                 # CUDA 13.5 版（需要更新的驱动）
└── vulkan\                            # Vulkan 版：N / A 卡与 Intel 核显都能用
```

新版官方包的结构是「小启动器 + `llama-server-impl.dll` + 后端 dll」：四个目录里的
`llama-server.exe`（约 9 KB）与 `llama-server-impl.dll` **完全相同**，差别只在后端
（CPU 只有 `ggml-cpu-*.dll`，CUDA 多一个 `ggml-cuda.dll`，Vulkan 多一个 `ggml-vulkan.dll`）。
所以三个变体的体积差主要来自后端 dll，不是重复存了多份主程序。

## 程序怎么选引擎

查找目录（每次启动/重新检查时重算，补完文件不用重启）：
**设置里指定的目录 → 本目录（`runtime\llama`） → 旧版 `%LOCALAPPDATA%\ModelXStudio\engine`**。
每个目录里递归找 `llama-server.exe`，**路径含 `cuda` 判为 CUDA 版、含 `vulkan` 判为 Vulkan 版**，
其余算 CPU 版；因此把官方 zip 解压自带的 `llama-b12387-bin-win-cuda-12.4-x64\` 那一层直接放进来也认。

「自动」偏好下的选择顺序：

1. 有 NVIDIA 显卡且放了 CUDA 版 → 用 CUDA（同变体放了多份时，**固定选主版本号小的那份**，
   即 `cuda-12.4-x64` 优先于 `cuda-13.5-x64`，因为 12.4 对驱动版本要求更宽、能跑的人群更多）；
2. 没有 N 卡但本机有显卡（**包括 Intel 核显**）且放了 Vulkan 版 → 用 Vulkan；
3. 其余情况用 CPU 版。

设置里也可以强制指定 CUDA / Vulkan / 仅 CPU；指定的那份没放时程序会退回能用的那份，不会报错。

## 需要下载哪个 zip

到 llama.cpp 的 [Releases 页面](https://github.com/ggml-org/llama.cpp/releases)：

| 用途 | 资产文件名 | 放到哪 |
| --- | --- | --- |
| CPU（保底） | `llama-b<构建号>-bin-win-cpu-x64.zip` | `runtime\llama\cpu` |
| NVIDIA GPU | `llama-b<构建号>-bin-win-cuda-12.x-x64.zip`（一般选 CUDA 12） | `runtime\llama\cuda\cuda-12.4-x64` |
| A 卡 / Intel 核显 | `llama-b<构建号>-bin-win-vulkan-x64.zip` | `runtime\llama\vulkan` |

几个变体建议用**同一个构建号**（当前是 `b12387`），切换后端时行为一致。
只需要 `llama-server.exe` 与同批 `*.dll`，zip 里的其他工具（`llama-cli`、`llama-bench` 等）可以不复制。

## 体积提醒（做安装包时注意）

当前三份全放是 **1.1 GB** 左右：CPU 约 62 MB、Vulkan 约 102 MB、CUDA 12.4 约 713 MB
（`ggml-cuda.dll` 一个就 651 MB，它静态链接了 cuBLAS）、CUDA 13.5 约 245 MB。
如果只想发一个通用包，带 **CPU + Vulkan**（约 164 MB）就能覆盖 N / A / Intel 三种显卡；
只有明确面向 N 卡用户时再加 CUDA。CUDA 二进制同时要遵守 NVIDIA 的 CUDA 工具包再分发条款，
见仓库根 `THIRD_PARTY_NOTICES.md`。

## 一条命令搞定（在能上网的机器上）

```powershell
powershell -ExecutionPolicy Bypass -File scripts\get-llama.ps1                 # 只取 CPU 版
powershell -ExecutionPolicy Bypass -File scripts\get-llama.ps1 -Vulkan        # CPU + Vulkan（推荐搭配）
powershell -ExecutionPolicy Bypass -File scripts\get-llama.ps1 -Cuda -Vulkan   # 三个后端全取
powershell -ExecutionPolicy Bypass -File scripts\get-llama.ps1 -Build 12387 -Proxy http://127.0.0.1:7890
```

脚本只做四件事：只取 `llama-server.exe` + `*.dll` + 上游 `LICENSE*`（展平到 `cpu\` /
`vulkan\` / `cuda\cuda-12.4-x64\`）、每个目录**确保带许可文本**（万一 zip 里没有就用
`LICENSE-MIT-llama-cpp.txt` 补）、写 `VERSION.txt`（构建号 / 资产名 / SHA256 / 时间）、
多个 CUDA 资产时固定优先 12.x（跟程序的选法一致）。`-Build` 走直链不依赖 GitHub API（限流时用），
`-CudaAssetName` 可以指定完整资产名以取另一个 CUDA 版本。

本目录当前的内容是一次性手工放置的，`VERSION.txt` 已按同一格式写好 `b12387`；
重跑脚本会**覆写** `VERSION.txt`（不会删已有目录），所以手工改过的记录先备份一下。

## 不想用随包的那份？

「设置 → 引擎目录」可以指到你机器上任何已有 `llama-server.exe` 的目录（例如你自己编译的），
指定后它的优先级最高。

## 本目录里的 exe / dll 为什么不进 git

`llama.cpp` 是几百 MB 的二进制，不入库（见根目录 `.gitignore`：忽略本目录下 `**/*.exe`、
`**/*.dll`、`**/*.zip`），仓库里只保留本说明、许可文本、`VERSION.txt` 与取包脚本。
所以在别的机器上 clone 之后，本目录会是空的 —— 请先跑上面的脚本或手工放置，
否则程序会在「模型库」页提示「缺少推理引擎（llama.cpp）」，其他功能仍可正常浏览。
