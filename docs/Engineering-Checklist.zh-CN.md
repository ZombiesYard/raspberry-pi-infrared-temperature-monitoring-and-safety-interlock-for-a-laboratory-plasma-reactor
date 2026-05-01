# Engineering 页实操版 Checklist

语言：[English](Engineering-Checklist.md) | [中文](Engineering-Checklist.zh-CN.md) | [Deutsch](Engineering-Checklist.de.md)

这个 checklist 对应当前 `relay-interlock-test` 分支里的 `Engineering` 页面。

它适用于这样一种状态：继电器本身已经能被软件控制，不再需要反复手拆跳线，而是通过软件按钮去验证 G2000 的真实 interlock 行为。

## 目标

- 确认软件断开继电器后，G2000 是否真的禁止 HV / 停止 plasma。
- 确认软件重新闭合继电器后，G2000 的恢复行为。
- 确认是一条支路够用、一组 interlock 够用，还是四条支路都要一起接管。

## 测试前提

- 万用表在手边。
- 螺丝刀在手边。
- G2000 处于低风险实验条件。
- 最好旁边有人一起看。
- `Dry run relay` 已关闭。
- COM 口已经选对。
- 已记录继电器通道和 G2000 端子对应关系。
- 静态 interlock 测试阶段不要求 OCR 一定开着。

## 先记录当前接线

开始前先填：

```text
CH1 -> ______
CH2 -> ______
CH3 -> ______
CH4 -> ______
```

当前分支推荐映射：

```text
CH1 -> I1-I2
CH2 -> I5-I6
CH3 -> I3-I4
CH4 -> I7-I8
```

## 第一阶段：软件静态 interlock 测试

这一阶段先不要真正运行 plasma，只用 `Engineering` 页。

### 1. 基线确认

- 点击 `Connect All Interlocks`
- 给 G2000 上电
- 确认没有 interlock fault
- 确认允许手动启动

记录：

```text
基线正常：是 / 否
```

### 2. 单通道测试

保持其他通道闭合，每次只断开一条：

- `Disconnect CH1`
- `Disconnect CH2`
- `Disconnect CH3`
- `Disconnect CH4`

每次记录：

```text
CH1 only open: fault __ / HV allowed __
CH2 only open: fault __ / HV allowed __
CH3 only open: fault __ / HV allowed __
CH4 only open: fault __ / HV allowed __
```

### 3. 整组测试

按 interlock 组一起断开：

- Interlock A open：同时断开 CH1 和 CH2
- Interlock B open：同时断开 CH3 和 CH4

记录：

```text
Interlock A open: fault __ / HV allowed __
Interlock B open: fault __ / HV allowed __
```

### 4. 全断开测试

- 点击 `Disconnect All Interlocks`

记录：

```text
All open: fault __ / HV allowed __
```

## 第二阶段：判断正式控制范围

根据静态测试结果判断：

- 如果断开单条支路就会禁用 HV，理论上单条支路已经有效。
- 如果必须断开一整组，后续正式控制至少接管这一组。
- 如果 A 和 B 都明显参与，继续保留四路方案。

当前分支的正式监控逻辑故意保守：

```text
Trip = open all enabled channels
Restore = close all enabled channels
```

## 第三阶段：动态热断开测试

只有在静态测试已经清楚、并且实验室同意的前提下再做。

- 点击 `Connect All Interlocks`
- 让 G2000 进入实验室允许的最低风险运行状态
- 触发你以后准备正式使用的动作：
  - 推荐最终测试：`Disconnect All Interlocks`
  - 也可以先做单通道或单组测试

立刻观察：

- HV 是否停止
- plasma 是否停止
- 屏幕报什么错误

记录：

```text
Dynamic action: ______
HV stop: yes / no
Plasma stop: yes / no
Error text: ______
```

## 第四阶段：恢复测试

在动态断开之后：

- 点击 `Connect All Interlocks`
- 观察恢复行为

记录：

```text
Fault clears automatically: yes / no
Returns to ready: yes / no
HV resumes automatically: yes / no
Manual ON required: yes / no
Manual reset required: yes / no
Power-cycle required: yes / no
```

## 最终汇总模板

```text
Current wiring:
CH1 -> __
CH2 -> __
CH3 -> __
CH4 -> __

Static tests:
CH1 only open: fault __ / HV allowed __
CH2 only open: fault __ / HV allowed __
CH3 only open: fault __ / HV allowed __
CH4 only open: fault __ / HV allowed __
Interlock A open: fault __ / HV allowed __
Interlock B open: fault __ / HV allowed __
All open: fault __ / HV allowed __

Dynamic test:
Action: __
HV stop: __
Plasma stop: __
Error: __

Recovery test:
Fault clears automatically: __
Ready: __
HV resumes automatically: __
Manual ON required: __
Manual reset required: __
Power-cycle required: __
```
