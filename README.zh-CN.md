# G2000 温度联控软锁

语言：[English](README.md) | [中文](README.zh-CN.md) | [Deutsch](README.de.md)

这是一个 Windows 桌面原型软件，用于实验室 plasma reactor 的温度监测和 G2000 高压等离子体发生器的外部联锁控制。

软件会读取 HikmicroAnalyzer 窗口中的最高温文字区域，通过 Tesseract OCR 识别温度，并把所有数据记录为 CSV。当有效温度读数达到或超过 `90.0 C` 时，软件进入 `Tripped` 状态，并通过 USB 串口继电器发送停止命令。

这是实验室原型，不是经过认证的工业安全控制器。它的目标是帮助实验人员把温度超限检测接入 G2000 external interlock / Not-Aus 回路。

## 系统组成

整个系统包含四部分：

- HIKMICRO 红外相机和 HikmicroAnalyzer：显示实时热图和准确温度叠加文字。
- 本 WPF 软件：截图温度文字区域，OCR 识别温度，判断是否超温。
- USB 串口继电器：接收 Windows 电脑发出的串口继电器命令。在当前 `relay-interlock-test` 分支里，已经实测的继电器是 `DSD TECH SH-UR04A`，协议是 ASCII AT 命令。
- G2000 外部 interlock / Not-Aus 回路：继电器触点作为无源干接点接入该回路。

本软件不调用 HIKMICRO 相机 API，因为当前相机工作流中温度信息已经显示在 HikmicroAnalyzer 画面上。

## 当前已验证分支

当前 `relay-interlock-test` 分支已经对齐到实验室里实际测过的继电器：

- 继电器板：`DSD TECH SH-UR04A 4CH`
- USB 芯片 / 驱动：`Silicon Labs CP210x`
- 实测串口：`COM3`
- 协议：`9600 8N1` 上的 ASCII AT 命令
- 已验证的通道 1 行为：
  - `AT+CH1=1` -> CH1 打开 -> `COM1-NO1` 闭合
  - `AT+CH1=0` -> CH1 关闭 -> `COM1-NC1` 闭合
- 已验证的 G2000 interlock 接线：
  - `G2000 I1 -> COM1`
  - `G2000 I2 -> NO1`

因此本分支现在默认就是四路工程继电器组：

- CH1 -> Interlock A 负支路 -> `I1-I2`
- CH2 -> Interlock A 正支路 -> `I5-I6`
- CH3 -> Interlock B 负支路 -> `I3-I4`
- CH4 -> Interlock B 正支路 -> `I7-I8`
- Trip 动作：断开所有启用通道
- Restore 动作：闭合所有启用通道

本分支现在也开始集成 `AMC2100` 气体质量流量控制器：

- 接口：`RS485 Modbus RTU`
- DB9 关键引脚：
  - Pin 3 = `RS485B`
  - Pin 4 = `RS485A`
  - Pin 5 = `+24V`
  - Pin 6 = `GND`
- 默认从站地址：`1`
- 默认波特率：`19200`
- 关键寄存器：
  - `0-1` = 瞬时流量，`32-bit float`
  - `2-3` = 设置流量，`32-bit float`
  - `11` = 控制模式，`1` 为数字控制，`2` 为模拟电压控制

当前软件里的 AMC2100 控制逻辑是：

- AMC2100 气体流量控制与温度联锁相互独立。
- 发送硬件流量命令前必须启用 AMC2100；禁用状态会明确阻止命令，不再静默显示成功。
- 启用 AMC2100 后，`保存设置`会把界面上的目标流量写入设备。
- 监控运行时，`保存设置`会在采样边界切换温度、恢复、OCR/ROI、轮询间隔以及已保存的 G2000/AMC2100 参数，随后立即唤醒一次安全采样，并单独写入已启用的 AMC2100 目标；不会取消监控循环、重建输出控制器、重新连接 G2000 或中断等离子体控制。继电器/PCAN 连接参数和高级继电器设置会被阻止，必须结束等离子体实验并停止监控后再修改。
- 只有收到近期 G2000 遥测帧才会确认 G2000/PCAN 通信，单纯打开 PCAN 接口不算成功。保存后若没有遥测，请先检查 G2000 是否开机；未开机就打开后重试，已经开机再检查 PCAN 适配器、CAN 线、通道和驱动。AMC2100 的成功或失败会独立报告。
- 目标流量的 `-` / `+` 按钮每次调整 10 mL/min，并立即写入，无需另点保存。
- `停止气流`写入 `0`；`应用气体设定值`写入当前目标。
- 周期实际流量读取与手动写入共用串行命令通道，不会争抢同一个 COM 口。
- 主界面顶部通过 RS485 显示 AMC2100 实际流量寄存器的实时 `气体流量`。

这不是单独的硬复位寄存器。只有 Modbus 写响应确认寄存器 `2-3`，并且立即读回的设定值与请求一致时，界面才报告成功。这证明设备设定值寄存器已接受目标，不等同于物理气流已建立；物理气流仍需结合实时 `气体流量`和现场观察确认。

台架测试时，AMC2100 本地面板可能持续显示 `0`，或不会立即刷新设定值。请同时检查软件读回结果和实时 `气体流量`，不要仅凭命令成功推断已经通气。

如果你要按照软件控制继电器的方式做现场验证，请直接使用这份 Engineering 实操 checklist：

- [docs/Engineering-Checklist.md](/mnt/l/Documents/files/Yu%20Zhang%20TU%20Clausthal/ProjectShukang/raspberry-pi-based-infrared-temperature-monitoring-and-safety-interlock-for-a-laboratory-plasma-reactor/docs/Engineering-Checklist.md)
- [docs/Engineering-Checklist.zh-CN.md](/mnt/l/Documents/files/Yu%20Zhang%20TU%20Clausthal/ProjectShukang/raspberry-pi-based-infrared-temperature-monitoring-and-safety-interlock-for-a-laboratory-plasma-reactor/docs/Engineering-Checklist.zh-CN.md)
- [docs/Engineering-Checklist.de.md](/mnt/l/Documents/files/Yu%20Zhang%20TU%20Clausthal/ProjectShukang/raspberry-pi-based-infrared-temperature-monitoring-and-safety-interlock-for-a-laboratory-plasma-reactor/docs/Engineering-Checklist.de.md)

## 软件如何工作

1. 根据 `Window` 字段查找 HikmicroAnalyzer 窗口。
2. 用户用 `Select ROI` 框选最高温文字区域。
3. 软件周期性截取该区域。
4. Tesseract OCR 把截图转换为文字。
5. 解析 `Max 89.9 C`、`90.1°C`、`最高 90.0 C` 这类温度。
6. 每条样本写入 CSV。
7. 任何有效读数 `>= ThresholdC` 时进入 `Tripped`。
8. `Tripped` 时软件会一次性断开所有启用的 interlock 通道，并保持锁存。
9. 只有读到低于阈值的有效温度后，才允许人工 `Reset`。
10. 如果启用 `Auto reset`，温度连续低于 `Recovery C` 并保持到设定稳定时间后，软件会自动重新闭合继电器组。

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
2. 安装 Windows 版 Tesseract OCR。实验室原型可以使用 UB Mannheim 这样的社区 Windows 安装包。请安装到正常 Windows 目录，例如 `C:\Program Files\Tesseract-OCR`。不要把安装器直接指向 `artifacts\...` 发布目录。
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

当前分支推荐发布目录和压缩包名：

```text
artifacts\ReactorSoftInterlock-win-x64-dsd-offline\
artifacts\ReactorSoftInterlock-win-x64-dsd-offline.zip
```

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

1. 解压 `ReactorSoftInterlock-win-x64-dsd-offline.zip`。
2. 双击运行 `ReactorSoftInterlock.Wpf.exe`。
3. 如果 Windows SmartScreen 弹出提示，先确认文件来自本项目，再继续运行。
4. 保持 `appsettings.json` 和 exe 在同一目录；点击保存设置时软件会更新这个文件。
5. 如果软件提示缺少 Tesseract、ROI、HikmicroAnalyzer、COM 口或继电器命令，先按提示修好，再开始监控。

## 第一次软件配置

1. 打开 HikmicroAnalyzer。
2. 确认最高温文字显示在画面上。
3. 保持 HikmicroAnalyzer 可见，建议最大化。
4. 启动本软件。
5. `Window` 填 HikmicroAnalyzer 窗口标题的一部分，例如 `Hikmicro`。
6. `Tesseract` 可以这样填写：
   - 如果发布包里自带完整可运行文件夹，就保持默认 `offline-deps\tesseract\tesseract.exe`
   - 如果单独安装到了系统里，就填完整路径，例如 `C:\Program Files\Tesseract-OCR\tesseract.exe`
7. 保持 `Dry run relay` 勾选。
8. 点击 `Save Settings`。
9. 点击 `Select ROI`。
10. 只框选最高温文字，不要框太大。
11. 点击 `Start`。
12. 观察 `Temperature`、`Raw OCR text`、折线图和历史表格。
13. `Engineering` 页只用于继电器和 interlock 工程测试。

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
4. 填写 `COM Port`、`Baud`、`停止命令`、`复位命令`。
5. 继电器触点仍保持不接 G2000。
6. 取消勾选 `Dry run relay`。
7. 点击 `Test Relay Stop`。
8. 用万用表测 `COM-NO` 和 `COM-NC` 的开闭变化。
9. 判断本实验需要“报警时开路”还是“报警时闭合”。
10. 将继电器触点接入确认过的 G2000 interlock / Not-Aus 回路。
11. 在不启用 plasma 或低风险状态下测试 G2000 对继电器动作的响应。
12. 确认无误后，才用于真实超温停机。

当前分支推荐的工程映射：

- CH1 -> Interlock A 负支路 -> `I1-I2`
- CH2 -> Interlock A 正支路 -> `I5-I6`
- CH3 -> Interlock B 负支路 -> `I3-I4`
- CH4 -> Interlock B 正支路 -> `I7-I8`
- 每一路优先使用 `COMx` + `NOx`，这样通道 ON 时闭合，OFF 时断开。
- 本分支的监控模式就是按“超温时全部断开、恢复时全部接通”设计的。
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
- 如果 `Test Relay Stop` 没反应，先检查波特率，再检查继电器命令。

当前分支里的继电器行为：

- 主界面不再让普通实验室用户直接编辑底层 AT 命令。
- `Engineering` 页提供成组操作：
  - `全部断开 Interlock`
  - `全部接通 Interlock`
  - CH1-CH4 独立 `断开` / `接通`
- `高级设置` 保留全部工程能力：
  - 波特率
  - CH1-CH4 的 open / close 命令
  - 通道启用开关
  - 恢复 DSD 默认模板
- 这些命令属于外部 USB 继电器板，不属于 G2000 本体。
- 当前 `relay-interlock-test` 分支里，已测试的继电器是 `DSD TECH SH-UR04A`。
- 当前默认串口参数：
  - `COM Port = COM3`（当时实测值，换电脑后可能不同）
  - `Baud = 9600`
  - `Data bits = 8`
  - `Parity = None`
  - `Stop bits = 1`
- 当前默认逐通道命令：

```text
CH1 断开 = AT+CH1=0
CH1 接通 = AT+CH1=1
CH2 断开 = AT+CH2=0
CH2 接通 = AT+CH2=1
CH3 断开 = AT+CH3=0
CH3 接通 = AT+CH3=1
CH4 断开 = AT+CH4=0
CH4 接通 = AT+CH4=1
```

当前分支的 UI 结构：

- `Monitor` 页：
  - 日常设置
  - OCR 监控
  - 折线图与历史表格
  - 成组继电器操作
- `Engineering` 页：
  - 明确的 interlock 测试 checklist
  - CH1-CH4 单独按钮
  - 全部接通 / 全部断开 操作
- `Tools -> 高级设置`：
  - 仅供工程调试使用的完整继电器命令编辑

- `appsettings.json` 里的配置键仍然叫 `StopCommandHex` / `ResetCommandHex`，这是为了兼容旧配置；但在当前分支里，里面存的已经不是 HEX 字节，而是 ASCII AT 命令。

如果还没有确定外部继电器板型号，保持 `Dry run relay` 勾选，不要把软件输出接到 G2000。

G2000 电脑控制：

- G2000 手册提到 CAN、RS485 等工业控制接口。这些是 G2000 原生接口，和外部 USB 继电器方案是两条不同路线。
- 当前原型还没有实现 G2000 CAN/RS485 报文。要让电脑直接控制 G2000，必须先解析手册里的协议页：接口针脚、总线类型、波特率、节点地址、消息/寄存器表、enable 命令、stop 命令、状态字，以及是否需要 watchdog 或周期报文。
- 在原生协议实现并测试前，推荐停机路径仍然是 external interlock / Not-Aus 回路，因为这个路径可以用万用表验证，不依赖 G2000 软件控制模式。

Dry Run 含义：

- 勾选：软件只模拟继电器动作，不打开 COM 口。
- 取消勾选：软件会真实打开 COM 口并发送配置好的继电器命令。

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
- 实验运行中可以直接保存温度、恢复、OCR、配方和 AMC2100 参数；当前监控任务和 G2000 控制器保持运行。
- 如果温度看起来不合理，查看 `Raw OCR text`。
- HIKMICRO 自动图像校准可能让叠加层短暂显示精确的 `0 C`。程序会把精确零度作为 `NO READING`，清零自动恢复稳定计时，不能单独使已触发的联锁恢复。
- HIKMICRO 窗口捕获和每个 Tesseract OCR 子进程都设有 5 秒超时。卡住的 Tesseract 会被终止；卡住的窗口捕获会被隔离为唯一一个在途任务，不会继续堆积捕获线程。两者都会记为 `NO READING`，Stop 或在线保存不再无限等待 `PrintWindow`。
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
- 确认 `停止命令` 非空并且符合继电器协议，例如当前 DSD 板使用 `AT+CH1=0`。

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
- 检查该继电器板是否需要交换 `停止命令` 和 `复位命令` 的状态定义。
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
- `Ocr.TesseractExePath`：可以是 `offline-deps\tesseract\tesseract.exe`、`tesseract.exe`，或完整安装路径。程序也会额外检查常见 Windows Tesseract 安装位置，例如 `C:\Program Files\Tesseract-OCR\tesseract.exe`。
- `Ocr.Language`：默认 `eng`。
- `Relay.DryRun`：默认 `true`。
- `Relay.PortName`：默认 `COM3`。
- `Relay.BaudRate`：默认 `9600`。
- `Relay.StopCommandHex`：当前分支里用于保存停止命令文本，默认是 `AT+CH1=0`。
- `Relay.ResetCommandHex`：当前分支里用于保存复位命令文本，默认是 `AT+CH1=1`。
- `DataDirectory`：默认 `data`。
- `Language`：`en`、`zh-CN` 或 `de`。
- `AutoResetEnabled`：默认 `true`。
- `RecoveryThresholdC`：默认 `85.0`。
- `RecoveryStableSeconds`：默认 `30`。
- 在已触发联锁时保存新的温度/恢复设置会原位更新现有状态机，不会清除逻辑停机锁存，也不会发送复位命令。只有有效温度低于新的恢复阈值后，新的稳定时间才开始计时。监控运行时不会热切换硬件连接参数。

## 开发者测试命令

```powershell
dotnet test .\src\ReactorSoftInterlock.sln
```

预期结果：所有测试通过。
