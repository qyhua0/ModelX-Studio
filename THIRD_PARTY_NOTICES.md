# 第三方组件与许可说明

ModelX Studio 本身按 [`LICENSE`](LICENSE) 分发（开源、**禁止商用**）。
但它不是从零造的轮子：运行时要依赖下面这些开源组件。这些组件**不受**本项目许可限制，
各自按原文本授权（多数是 MIT，允许商用），本文件把它们完整列出来，避免任何越权或漏署名。

> 一句话理解：本项目的**代码**不能商用；llama.cpp 等**上游组件**该怎样还怎样，
> 我们没资格、也没有改变它们的许可。


## 1. llama.cpp（核心推理引擎，随包分发在 `runtime/llama`）

- 项目：https://github.com/ggml-org/llama.cpp （上游现为 ggml-org/llama.cpp）
- 作者：Georgi Gerganov 及 contributors（完整名单见上游 `AUTHORS` 文件）
- 许可：**MIT License**
- 我们用了什么：官方为 Windows 预编译的 `llama-server.exe` 及其依赖 DLL
  （CPU 版 `llama-b<构建号>-bin-win-cpu-x64.zip`、GPU 版 `llama-b<构建号>-bin-win-cuda-12.x-x64.zip`）。
  本程序只是以子进程方式启动它并通过它的 OpenAI 兼容接口对话，**没有把它的源码编进我们的代码**。
- 二进制随附的许可文本：`runtime/llama/LICENSE-MIT-llama-cpp.txt`

```
MIT License

Copyright (c) 2023 Georgi Gerganov

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```

补充：llama.cpp 内部还包含少量第三方代码（例如 Intel 的 SYCL 相关代码为 MIT、
少量来自 LLVM 项目的片段为 Apache-2.0 WITH LLVM-exception）。这些细节由上游仓库的
`LICENSE` / `AUTHORS` 负责说明，我们原样分发其官方构建产物，不做剥离。

### 1.1 随包分了哪几个后端，各自还牵扯谁的许可

本目录当前放了三个官方构建（同一构建号 `b12387`）：

| 目录 | 资产 | 依赖的额外组件 | 许可提醒 |
| --- | --- | --- | --- |
| `cpu\` | `llama-b…-bin-win-cpu-x64.zip` | `libomp.dll`（LLVM OpenMP 运行时） | Apache-2.0 WITH LLVM-exception；zip 自带的 `LICENSE-LLVM-OpenMP` **必须跟着放**（已放） |
| `vulkan\` | `llama-b…-bin-win-vulkan-x64.zip` | 同上 + 系统里的 Vulkan ICD（显卡驱动提供） | 驱动本身不由我们分发，无额外义务 |
| `cuda\cuda-12.4-x64\`、`cuda\cuda-13.5-x64\` | `llama-b…-bin-win-cuda-12.x-x64.zip` | NVIDIA CUDA 运行时与 cuBLAS（**静态链入 `ggml-cuda.dll`**，所以该 dll 单独就有 651 MB，目录里没有单独的 `cudart64_*.dll` / `cublas64_*.dll`） | **这一部分受 NVIDIA 的 CUDA 工具包许可（CUDA Toolkit EULA）约束**，它对可再分发组件有额外条件。官方 zip 本身不带 NVIDIA 的许可文本，所以我们也不自行编逐。真要签安装包、批量发行时，请自己读一遍 NVIDIA 的再分发条款 |

补充两条可操作的事实：

- 每个变体目录里都放了一份 `LICENSE-llama-cpp.txt`（MIT 全文）+ zip 自带的 `LICENSE-LLVM-OpenMP`。
  MIT 的硬性条件就是“所有副本必须包含版权声明与许可文本”，**拷目录时不要把这两个文件删了**。
- 官方 zip 里除了 `llama-server` 还有 `llama-cli` / `llama-bench` / `llama-quantize` / `llama-mtmd-cli` 等工具。
  本程序只用 `llama-server.exe`，删其余工具每份能省约 **19 MB**（收益小，风险是删错 dll，
  因此默认全部保留）。


## 2. CommunityToolkit.Mvvm（界面 MVVM 工具库）

- 项目：https://github.com/CommunityToolkit/dotnet （NuGet 包 `CommunityToolkit.Mvvm` 8.2.2）
- 版权：.NET Foundation
- 许可：**MIT License**（标准 MIT 文本，版权行换成 `.NET Foundation`）

```
MIT License

Copyright (c) .NET Foundation and Contributors

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```


## 3. Markdig（Markdown 解析，聊天消息渲染）

- 项目：https://github.com/xoofx/markdig （NuGet 包 `Markdig` 0.22.0）
- 版权：Alexandre Mutel
- 许可：**MIT License**（标准 MIT 文本，版权行换成 `Alexandre Mutel`）
- 用在哪：只用来把模型输出的 Markdown 解析成语法树，再由 `src/ModelXStudio.App/Markdown/MarkdownX.cs` 转成 WPF 的
  FlowDocument 显示（表格/列表/代码块）。**不联网**，不拉字体，不动态加载插件；代码高亮是自己写的
  （`Markdown/CodeHighlighter.cs`），不依赖 Highlight.js / pygments 之类。
- 义务：MIT 只需在分发物里保留下面的版权与许可文本（本文件就是）。

```
MIT License

Copyright (c) 2018 Alexandre Mutel

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```


## 4. .NET 运行时与 WPF

本项目基于 Microsoft .NET 8 / WPF 构建（`net8.0`、`net8.0-windows`）。
.NET 运行时与 WPF 同为开源项目（MIT 许可），由 `dotnet` SDK 与 Windows 提供，
我们不再额外分发。


## 5. 模型权重（不属于本项目）

「模型库」页面里的模型是各家机构发布的权重文件，从 Hugging Face / hf-mirror /
ModelScope 下载，**不属于本软件，也不受本项目许可授权**。常见例子：

| 模型 | 发布方 | 许可（按各仓库原文） | 商用 |
| --- | --- | --- | --- |
| Qwen3 系列 | 阿里巴巴通义 | Apache-2.0（官方 README：All our open-weight models are licensed under Apache 2.0） | 允许，需遵守上游条款 |
| Phi-4-mini | Microsoft | MIT（模型卡 front matter `license: mit`） | 允许 |
| Gemma 3 | Google | Gemma Terms of Use（**自定义条款**，含使用限制与再分发要求） | 需自己读条款 |
| Llama 3.2 | Meta | Llama Community License（商用允许，但月活超 7 亿需另行授权、有命名要求，多模态款另有地区限制） | 有门槛 |
| DeepSeek-R1-Distill-Qwen | DeepSeek | 仓库标 MIT；但蒸馏基座 Qwen2.5 是 Apache-2.0，上游有「应改成 Apache-2.0」的讨论 | 拿不准就问发布方 |

两点务必注意：

1. 上表是我们整理时看到的许可标注，**上游随时会改**；真正要商用某款模型，请以该模型仓库当前的
   `LICENSE` 原文为准，别拿这张表当依据。
2. **本项目的代码禁止商用，和模型能不能商用是两件独立的事**：拿到一个 Apache-2.0 的模型，
   也不等于可以把 ModelX Studio 打包成商业产品卖。

参考来源（查证于 2026-11-15）：
- https://github.com/qwenLM/qwen3/ （License Agreement 一节）
- https://huggingface.co/microsoft/Phi-4-mini-instruct/blob/main/README.md （`license: mit`）
- https://ai.google.dev/gemma/terms （Gemma Terms of Use）
- https://github.com/meta-llama/llama-models/blob/main/models/llama3_2/LICENSE
- https://huggingface.co/deepseek-ai/DeepSeek-R1-Distill-Qwen-32B/discussions/1

## 6. 字体与界面图标

界面使用系统字体（Microsoft YaHei UI / Segoe UI），不随安装包分发字体文件；
导航图标是本项目自绘的矢量 `Path` 几何，不是第三方图标库。


## 7. 分发时的义务清单（给别人 fork / 打包时照抄）

1. 保留 `LICENSE`（本项目，禁止商用）与本文件。
2. 保留源代码文件头里的「作者：zhenhua」与功能描述注释。
3. 随包分发 llama.cpp 二进制时，一起带上 `runtime/llama/LICENSE-MIT-llama-cpp.txt`，
   并在界面或文档里保留对 llama.cpp / GGML 的致谢。
4. 用到 Markdig（聊天渲染）时，保留上面 §3 的 MIT 文本；升级 Markdig 大版本时顺手把本节的版本号与版权行对一下。
5. 说明你的版本是否改过、改了什么。
6. 别把模型权重打包进你的分发物（体积、许可都有问题），让用户自己下载。
7. 想商用？请先联系本项目许可人取得书面授权，并逐一确认上游组件与模型的许可。
