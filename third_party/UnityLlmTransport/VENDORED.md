# UnityLlmTransport（vendored）

- 来源：https://github.com/SuperAlways/UnityLlmTransport.git（消费方私有）
- 固定提交：`4c0cc4b`（Phase 0–6，`VERSION 0.6.0`，2026-11-08）
- 拷贝范围：`src/UnityLlmTransport.{Abstractions,Sse,HttpClient,Auth,WebSocket,UnityWebRequest.Core}` + `unity/UnityLlmTransport.{UnityWebRequest,Hosts.Unity}/Runtime` + LICENSE/NOTICE/VERSION
- 排除：tests、spikes、Phase*Harness、bin、obj、scripts
- 同步方式：`git -C D:/shuoshu/taiwuask/UnityLlmTransport archive <new-commit> ... | tar -x -C third_party/UnityLlmTransport`；换版必须更新本文件并重新构建。
- 依赖边界：只有 `PiUnityAgent.Hosts.*` 可引用本项目；`PiUnityAgent.Ai` 禁止引用。

## 落地说明

- 六个 `src/` 包以 `netstandard2.1` / `LangVersion 9.0` 自带设置编译，已作为 solution 项目挂入 `PiUnityAgent.slnx`（`/third_party/UnityLlmTransport/` 文件夹）。
- `unity/*/Runtime` 为 Unity 运行时源码（无 csproj），供后续 Unity 侧装配参考，不参与 dotnet 构建。
- 仓库根 `Directory.Build.props` 只额外注入 `GenerateDocumentationFile=false` / `TreatWarningsAsErrors=false`，不改变 vendored 包自身的 TFM/语言版本。
