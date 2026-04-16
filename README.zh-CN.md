# G2000 温度联控软锁

语言：[English](README.md) | [中文](README.zh-CN.md) | [Deutsch](README.de.md)

这是一个 Windows 桌面原型软件，用于实验室 plasma reactor 的温度监测和 G2000 高压等离子体发生器的外部联锁控制。

软件会读取 HikmicroAnalyzer 窗口中的最高温文字区域，通过 Tesseract OCR 识别温度，并把所有数据记录为 CSV。当有效温度读数达到或超过 `90.0 C` 时，软件进入 `Tripped` 状态，并通过 USB 串口继电器发送停止命令。

这是实验室原型，不是经过认证的工业安全控制器。它的目标是帮助实验人员把温度超限检测接入 G2000 external interlock / Not-Aus 回路。

## 系统组成

整个系统包含四部分：

- HIKMICRO 红外相机和 HikmicroAnalyzer：显示实时热图和准确温度叠加文字。
- 本 WPF 软件：截图温度文字区域，OCR 识别温度，判断是否超温。
- USB 串口继电器：接收 Windows 电脑发出的 HEX 命令。
- G2000 外部 interlock / Not-Aus 回路：继电器触点作为无源干接点接入该回路。

本软件不调用 HIKMICRO 相机 API，因为当前相机工作流中温度信息已经显示在 HikmicroAnalyzer 画面上。

## 软件如何工作

1. 根据 `Window` 字段查找 HikmicroAnalyzer 窗口。
2. 用户用 `Select ROI` 框选最高温文字区域。
3. 软件周期性截取该区域。
4. Tesseract OCR 把截图转换为文字。
5. 解析 `Max 89.9 C`、`90.1°C`、`最高 90.0 C` 这类温度。
6. 每条样本写入 CSV。
7. 任何有效读数 `>= ThresholdC` 时进入 `Tripped`。
8. `Tripped` 时只发送一次继电器停止命令，并保持锁存。
9. 只有读到低于阈值的有效温度后，才允许人工 `Reset`。
10. 如果启用 `Auto reset`，温度连续低于 `Recovery C` 并保持到设定稳定时间后，软件会自动发送继电器复位命令。

如果 OCR 失败，软件记录 `NO READING`。单独的 `NO READING` 不会触发继电器。

## 仓库结构

- `src/ReactorSoftInterlock.sln`：Visual Studio / .NET 解决方案。
- `src/ReactorSoftInterlock.Wpf`：WPF 桌面软件。
- `src/ReactorSoftInterlock.Domain`：阈值、样本、继电器动作、锁存 trip 逻辑。
- `src/ReactorSoftInterlock.Application`：监控循环、OCR 文本解析、接口定义。
- `src/ReactorSoftInterlock.Infrastructure`：窗口截图、Tesseract、串口继电器、CSV、配置。
- `tests/ReactorSoftInterlock.Tests`：核心逻辑测试。
- `docs/g2000-soft-interlock.md`：额外硬件说明。
- `Doc`：G2000 手册和项目 PDF。
- `HikmicroAnalyzer`：HIKMICRO 示例图片。

## 运行环境

开发电脑：

- Windows 10 或更新版本。
- Visual Studio 2022，安装 `.NET desktop development`，或安装 .NET 8 SDK。
- 第一次 restore NuGet 包时需要网络。

实验室电脑：

- Windows 10 或更新版本。
- HikmicroAnalyzer 正常运行，最高温叠加文字可见。
- Windows 版 Tesseract OCR。
- USB 串口继电器及其 Windows 驱动。
- 已确认的 G2000 external interlock / Not-Aus 接线点。
- 万用表，用于在接入 G2000 前检查继电器触点。

## 安装所需组件

实验室电脑优先使用 self-contained Release 包。这个包已经带上本软件需要的 .NET runtime，所以实验室电脑通常不需要单独安装 .NET。实验室电脑仍然需要安装 HikmicroAnalyzer、Tesseract OCR、USB 继电器驱动，并完成正确的继电器/G2000 接线。

实验室电脑安装步骤：

1. 安装 HikmicroAnalyzer，并确认相机画面和最高温叠加文字都能显示。
2. 安装 Windows 版 Tesseract OCR。安装后，可以把 Tesseract 安装目录加入 `PATH`，也可以在软件的 `Tesseract` 字段中填写 `tesseract.exe` 的完整路径。
3. 安装 USB 串口继电器驱动。常见继电器板可能使用 CH340、CH341、CP210x 或 FTDI 驱动，按继电器板型号选择。
4. 打开 Windows 设备管理器，展开 `Ports (COM & LPT)`，插入继电器，记录出现的 `COMx`。
5. 在 OCR 和继电器空载测试完成前，保持 `Dry run relay` 勾选。
6. 根据 G2000 手册和实验室实际接线确认 external interlock / Not-Aus 回路需要“打开”还是“闭合”来禁止高压。

开发电脑安装步骤：

1. 安装 Visual Studio 2022。
2. 在 Visual Studio Installer 中选择 `.NET desktop development` 工作负载。
3. 确认单个组件中安装了 `.NET 8.0 SDK`，或单独安装 .NET 8 SDK。
4. 如果 Visual Studio 提示解决方案不受支持，请安装 `.NET desktop development`，关闭 Visual Studio 后重新打开 `src/ReactorSoftInterlock.sln`。
5. 如果 Visual Studio 提示进行非功能性 solution 更改，可以接受；但 solution 中必须保留 Debug 和 Release 配置。

## 从源代码构建

在仓库根目录打开 PowerShell：

```powershell
cd "L:\Documents\files\Yu Zhang TU Clausthal\ProjectShukang\raspberry-pi-based-infrared-temperature-monitoring-and-safety-interlock-for-a-laboratory-plasma-reactor"
```

还原依赖：

```powershell
dotnet restore .\src\ReactorSoftInterlock.sln
```

运行测试：

```powershell
dotnet test .\src\ReactorSoftInterlock.sln
```

构建 Release：

```powershell
dotnet build .\src\ReactorSoftInterlock.sln -c Release
```

生成文件位于：

```text
src\ReactorSoftInterlock.Wpf\bin\Release\net8.0-windows\
```

生成实验室部署用的 self-contained 包：

```powershell
dotnet publish .\src\ReactorSoftInterlock.Wpf\ReactorSoftInterlock.Wpf.csproj -c Release -r win-x64 --self-contained true -o .\artifacts\ReactorSoftInterlock-win-x64
```

生成的文件夹可以压缩后复制到实验室电脑。里面包含 `ReactorSoftInterlock.Wpf.exe` 和软件需要的 .NET runtime 文件。

## 启动软件

从源码运行：

```powershell
dotnet run --project .\src\ReactorSoftInterlock.Wpf\ReactorSoftInterlock.Wpf.csproj
```

Release 模式运行：

```powershell
dotnet run -c Release --project .\src\ReactorSoftInterlock.Wpf\ReactorSoftInterlock.Wpf.csproj
```

也可以用 Visual Studio 2022 打开 `src/ReactorSoftInterlock.sln`，把 `ReactorSoftInterlock.Wpf` 设为启动项目，然后按 `F5`。

运行实验室 Release 包：

1. 解压 `ReactorSoftInterlock-win-x64-latest.zip`。
2. 双击运行 `ReactorSoftInterlock.Wpf.exe`。
3. 如果 Windows SmartScreen 弹出提示，先确认文件来自本项目，再继续运行。
4. 保持 `appsettings.json` 和 exe 在同一目录；点击保存设置时软件会更新这个文件。
5. 如果软件提示缺少 Tesseract、ROI、HikmicroAnalyzer、COM 口或 HEX 命令，先按提示修好，再开始监控。

## 第一次软件配置

1. 打开 HikmicroAnalyzer。
2. 确认最高温文字显示在画面上。
3. 保持 HikmicroAnalyzer 可见，建议最大化。
4. 启动本软件。
5. `Window` 填 HikmicroAnalyzer 窗口标题的一部分，例如 `Hikmicro`。
6. `Tesseract` 填 `tesseract.exe` 或完整路径。
7. 保持 `Dry run relay` 勾选。
8. 点击 `Save Settings`。
9. 点击 `Select ROI`。
10. 只框选最高温文字，不要框太大。
11. 点击 `Start`。
12. 观察 `Temperature`、`Raw OCR text` 和历史表格。

ROI 和窗口位置、大小、显示缩放有关。如果移动、缩放、最小化、遮挡 HikmicroAnalyzer，或 Windows 显示缩放改变，需要重新 `Select ROI`。

## 连接 Plasma / G2000

继电器必须作为无源触点使用。不要从电脑 USB、串口模块、本软件或继电器控制侧向 G2000 interlock 端子主动输出电压。

连接原则：

- USB 继电器的 USB/串口侧连接 Windows 电脑。
- 继电器触点侧只作为干接点。
- 触点必须接入实验室确认的 G2000 external interlock / Not-Aus 回路。
- 正常运行时，触点状态必须允许 G2000 高压路径。
- 超温 trip 时，触点状态必须禁止或断开 G2000 高压路径。

本 README 不指定 G2000 端子号。端子必须按 G2000 手册和实验室实际接线确认。

推荐调试顺序：

1. 勾选 `Dry run relay`，只验证 OCR。
2. 把 USB 继电器接到电脑，但先不要接 G2000。
3. 在 Windows Device Manager 中找到 COM 口。
4. 填写 `COM Port`、`Baud`、`Stop HEX`、`Reset HEX`。
5. 继电器触点仍保持不接 G2000。
6. 取消勾选 `Dry run relay`。
7. 点击 `Test Relay Stop`。
8. 用万用表测 `COM-NO` 和 `COM-NC` 的开闭变化。
9. 判断本实验需要“报警时开路”还是“报警时闭合”。
10. 将继电器触点接入确认过的 G2000 interlock / Not-Aus 回路。
11. 在不启用 plasma 或低风险状态下测试 G2000 对继电器动作的响应。
12. 确认无误后，才用于真实超温停机。

触点选择：

- 如果 G2000 interlock 需要“闭合才允许运行”，通常使用 `COM` + `NC`，报警时继电器动作打开回路。
- 如果实验室接线或继电器逻辑相反，改用 `COM` + `NO`，或交换 `Stop HEX` 与 `Reset HEX` 对应的继电器状态。
- 必须用万用表确认，不要只看继电器 LED。

## COM 口设置

查找 COM 口：

1. 打开 Windows Device Manager。
2. 展开 `Ports (COM & LPT)`。
3. 拔掉 USB 继电器。
4. 再插入 USB 继电器。
5. 观察新增的 `COMx`，例如 `COM3` 或 `COM4`。
6. 在软件 `COM Port` 中填写同样的值。

波特率：

- 先看继电器板说明书。
- 常见值是 `9600` 或 `115200`。
- 软件中的 `Baud` 必须和继电器板一致。
- 如果 `Test Relay Stop` 没反应，先检查波特率，再改 HEX。

HEX 命令：

- `Stop HEX` 是超温 trip 时发送的命令。
- `Reset HEX` 是人工 reset 或自动复位成功时发送的命令。
- 这些命令属于外部 USB 继电器板，不属于 G2000 本体。
- 软件默认保持两条命令为空，因为正确字节取决于实际外部继电器型号。
- 仅作为例子：常见 LCUS-1 / LCUS-2 / LC Technology / CH340 USB 串口继电器板通常使用 9600 波特率，`A0 01 01 A2` 表示通道 1 ON / 吸合，`A0 01 00 A1` 表示通道 1 OFF / 释放。
- 支持以下格式：

```text
A0 01 01 A2
A0-01-01-A2
A0:01:01:A2
```

如果还没有确定外部继电器板型号，保持 `Dry run relay` 勾选，不要把软件输出接到 G2000。

可选 LCUS/CH340 通道 1 接法，仅当实际使用这种外部继电器板时适用：

- 如果 G2000 interlock 需要“开路来停止高压”，并且使用 `COM` + `NC`，`Stop HEX = A0 01 01 A2` 会让继电器吸合，从而打开 NC 触点。
- `Reset HEX = A0 01 00 A1` 会释放继电器，让 `COM` + `NC` 重新闭合。
- 如果实验室决定使用 `COM` + `NO`，必须用万用表确认；stop/reset 逻辑可能需要互换。

G2000 电脑控制：

- G2000 手册提到 CAN、RS485 等工业控制接口。这些是 G2000 原生接口，和外部 USB 继电器方案是两条不同路线。
- 当前原型还没有实现 G2000 CAN/RS485 报文。要让电脑直接控制 G2000，必须先解析手册里的协议页：接口针脚、总线类型、波特率、节点地址、消息/寄存器表、enable 命令、stop 命令、状态字，以及是否需要 watchdog 或周期报文。
- 在原生协议实现并测试前，推荐停机路径仍然是 external interlock / Not-Aus 回路，因为这个路径可以用万用表验证，不依赖 G2000 软件控制模式。

Dry Run 含义：

- 勾选：软件只模拟继电器动作，不打开 COM 口。
- 取消勾选：软件会真实打开 COM 口并发送 HEX 字节。

## 实验前检查清单

- HikmicroAnalyzer 已打开。
- 最高温文字可见。
- HikmicroAnalyzer 没有最小化。
- 没有其他窗口遮挡温度文字。
- 已在当前窗口位置下重新框选 ROI。
- `Temperature` 显示合理温度。
- CSV 记录正常。
- `Dry run relay` 状态符合当前测试阶段。
- `Test Relay Stop` 已用万用表验证。
- G2000 interlock 响应已在低风险状态验证。

## 实验中注意事项

- 不要移动或缩放 HikmicroAnalyzer。
- 不要最小化 HikmicroAnalyzer。
- 不要遮挡温度文字。
- 如果温度看起来不合理，查看 `Raw OCR text`。
- 如果连续出现 `NO READING`，先暂停依赖该软件，检查 ROI/OCR。

## Trip 后流程

1. 确认 plasma 已停止或进入预期安全状态。
2. 保留 CSV 记录。
3. 等待软件读到低于阈值的有效温度。
4. 如果关闭了 `Auto reset`，点击 `Reset`。
5. 如果启用了 `Auto reset`，确认温度连续低于 `Recovery C` 并达到稳定时间后继电器才复位。
6. 重新确认 OCR 和继电器状态，再继续实验。

## 状态含义

- `Monitoring`：有效温度低于阈值。
- `NoReading`：OCR 没有有效温度。样本会记录，但不会因此触发继电器。
- `Tripped`：达到或超过阈值。停止命令已发送一次，并锁存。
- `RelayTestFailed`：继电器测试失败或发生异常。

## CSV 记录

默认位置：

```text
src\ReactorSoftInterlock.Wpf\bin\<Configuration>\net8.0-windows\data\temperature-history.csv
```

字段：

```text
timestamp,temperature_c,raw_ocr_text,status,alarm_reason,relay_action,screenshot_roi
```

点击 `Export CSV` 可以导出当前 CSV。

## 故障排查

### 找不到 HikmicroAnalyzer 窗口

- 确认 HikmicroAnalyzer 已打开。
- 确认 `Window` 字段包含窗口标题的一部分。
- 不要最小化 HikmicroAnalyzer。
- 可以尝试较短的过滤词，例如 `Hikmicro`。

### OCR 显示 `NO READING`

- 重新点击 `Select ROI`。
- 只框最高温文字，不要框整张图。
- 最大化 HikmicroAnalyzer。
- 移除遮挡温度文字的窗口。
- 检查 `Tesseract` 路径。
- 改变 Windows 显示缩放后重新框选 ROI。

### 温度识别错误

- 缩小 ROI。
- 确保温度叠加文字清晰、对比度高。
- 不要把标签、时间、坐标、其他数字框进去。
- 记录 `Raw OCR text`，后续可用于改进解析规则。

### 点击 `Test Relay Stop` 没反应

- 确认 `Dry run relay` 是否仍然勾选。
- 在 Device Manager 中确认 COM 口。
- 确认 USB 继电器驱动已安装。
- 关闭占用串口的软件。
- 确认 `Baud` 与继电器板一致。
- 确认 `Stop HEX` 非空且符合继电器协议。

### COM port access denied

- 关闭 Arduino Serial Monitor、PuTTY、串口调试助手、继电器厂家工具。
- 拔插 USB 继电器。
- 检查 COM 编号是否变化。
- 必要时重启本软件。

### COM port not found

- 检查 USB 线和 USB 口。
- 在 Device Manager 查看是否有未知设备。
- 安装或更新继电器驱动。
- 重新填写 `COM Port`。

### 继电器动作了，但 G2000 没停止

- 用万用表确认触点真的开闭变化。
- 检查接的是 `COM-NO` 还是 `COM-NC`。
- 确认触点确实在 G2000 external interlock / Not-Aus 回路中。
- 确认 stop 命令让继电器进入正确状态。
- 不要继续带 plasma 测试，先回到空载或低风险验证。

### G2000 一直不允许启动

- 触点可能接反。
- 继电器初始状态可能不对。
- 在手册确认后尝试换用 `COM-NO` 或 `COM-NC`。
- 检查该继电器板是否需要交换 `Stop HEX` 和 `Reset HEX` 的状态定义。
- 用万用表确认 reset 后 interlock 回路处于允许状态。

### 超温后无法 Reset

- 如果当前温度仍高于阈值，这是正常锁存行为。
- Reset 需要一个低于阈值的有效温度。
- 如果一直 `NO READING`，先修复 OCR/ROI，让软件能确认温度已低于阈值。

## 配置文件

软件会在可执行文件旁创建和更新 `appsettings.json`。

重要字段：

- `ThresholdC`：默认 `90.0`。
- `PollIntervalMs`：默认 `1000`。
- `WindowTitleContains`：用于查找 HikmicroAnalyzer。
- `Roi`：`Select ROI` 后保存。
- `Ocr.TesseractExePath`：`tesseract.exe` 或完整路径。
- `Ocr.Language`：默认 `eng`。
- `Relay.DryRun`：默认 `true`。
- `Relay.PortName`：默认 `COM3`。
- `Relay.BaudRate`：默认 `9600`。
- `Relay.StopCommandHex`：默认空；按外部继电器说明书填写。
- `Relay.ResetCommandHex`：默认空；按外部继电器说明书填写。
- `DataDirectory`：默认 `data`。
- `Language`：`en`、`zh-CN` 或 `de`。
- `AutoResetEnabled`：默认 `true`。
- `RecoveryThresholdC`：默认 `85.0`。
- `RecoveryStableSeconds`：默认 `30`。

## 开发者测试命令

```powershell
dotnet test .\src\ReactorSoftInterlock.sln
```

当前预期：

```text
Passed: 20, Failed: 0, Skipped: 0
```
