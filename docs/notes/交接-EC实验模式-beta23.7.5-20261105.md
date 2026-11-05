# 交接：L-Mechrevo EC 实验模式（beta23.7.5）

日期：2026-11-05
范围：`tools/dev/wpf-p0` + `source/MechrevoLite.Core` 实验分支
状态：**实验 DLL，未发正式 Release**

---

## 一、当前结论

这台机器（GM5HG0A / 翼龙15 Pro / Gen40 + 50 GCU）实测：

```text
现代同方 ACPI 通道
ACPIDriver 可用
NVRAM = UniWillVariable
UWACPIDriver / GCU 服务已安装
NVIDIA 独显存在
传统 AcpiTest_MULong 不可用
```

因此实验版采用：

```text
GCU 可连接 → GCU 原路径
GCU 连续失败 5 次，或实验直启 → EC-only 路径
EC 路径激活后不自动回 GCU，重启程序才回
```

实验直启由脚本写配置：

```text
gcu_ec_experiment=1
gcu_ec_direct=1
```

---

## 二、EC 模式与门控（重要）

### 1. 内置档

```text
静音：1963=0x00, 1873=0xA0, 1895=0x28, 1115=1
平衡：1963=0x00, 1873=0x00, 1895=0x20, 1115=0
狂暴：1963=0x10, 1873=0x10, 1895=0x20, 1115=0
```

写入顺序：

```text
clamp 1873
mode 1963
trigger 1895
turbo flag 1115
1798=64（必须在 trigger 之后）
清 1830 bit7 / 1831 bit6
清 1989 bit7
1927=0x83
```

### 2. 自定义档

```text
1963=0x10
1895=0x20
1798=65（必须在 trigger 之后）
1830 |= 0x80
1831 |= 0x40
1989 |= 0x80
1927=0x81
保留 1873 强冷位 bit6
```

自定义实际写入 PL/TCC 由 `NucPerformanceMode.TryApplyCustom` 完成，随后再调用 `GcuEcExperimental.TryApplyMode(3)` 补门控。

### 3. 退出自定义

```text
1798=64 必须在 1895=0x20 之后
清 1830/1831 白灯位
清 1989 bit7
1927=0x83
1963 写目标内置模式
```

### 4. 禁止事项

**不要把 `NucPerformanceMode.TryApply` 套到同方 GCU 内置档上。**

它会写 `1963` 低 4 位风扇模式，导致模式字节变成：

```text
0x01 / 0x03 / 0x11
```

表现为“模式切不走/实际还在自定义”。

同方 GCU 的内置档由固件表接管，应用只写上面那几个字节。

---

## 三、主要代码位置

### `source/MechrevoLite.Hardware/GcuEcExperimental.cs`

- `Enabled`：`gcu_ec_experiment` 或环境变量 `LM_GCU_EC_EXPERIMENT=1`
- `DirectMode`：`gcu_ec_direct` 或 `LM_GCU_EC_DIRECT=1`
- `TryReadVisualMode`：优先读 `1798==65` 判自定义，再判 1963/1873
- `TryApplyMode`：静音/平衡/狂暴/自定义写入与门控
- 地址：
  - 1963 模式
  - 1895 trigger
  - 1115 turbo trigger
  - 1873 fan clamp
  - 1798 custom flag
  - 1830 / 1831 白灯位
  - 1927 fan ramp
  - 1989 fan isolated

### `source/MechrevoLite.Hardware/NucPerformanceMode.cs`

- `TryApplyCustom`：自定义 PL1/PL2/PL4 + TCC + trigger + 1798=65
- TCC：
  - Intel：0x80 | (TjMax - 目标温度)
  - AMD：0x80 | 目标摄氏度
- `TryApply` 是 **NUC/UMI 内置档**逻辑，不得用于 GCU 内置档。

### `source/MechrevoLite.Hardware/EcGpuPower.cs`

GPU TGP / DB：

```text
1859 DB_CTRL1
1860 TGP_OFFSET
1861 DB_FLAG1
1862 DB_WATTS1
```

能力计算：

```text
优先 NVML
→ 失败直接 nvidia-smi --query-gpu=power.default_limit,power.max_limit
→ 再失败才用 hw.GpuTgpMaximum
```

nvidia-smi 输出可能是小数（115.00 / 140.00），必须按 `double` 解析后四舍五入。

本机实测：

```text
default 115W / max 140W
⇒ 固定基频：base 115W，DB max 25W
```

写入：

```text
1861=0xFF
1859=0x07（开 DB）/0x05（关）
1860=offset（固定基频为 0）
1862=DB 瓦数
```

### `tools/dev/wpf-p0/BackendProbe.cs`

- `CurrentGpuMode`：GCU 模式；无 GCU 时从 NVRAM 解码
- `PendingGpuTarget`：实验路由按 Mark 时间保留待重启，不再比较 NVRAM 路由
- `ApplyGpuOverclockAsync`：NVAPI 直写回退
  - 核心 0 → +105
  - 显存 0 → +500
  - 最后写 `OverClockingSwitch`
- 快捷开关：
  - 强冷：EC 1873 bit6
  - Fn/摄像头/Win/触摸板/来电自启：`NucQuickSwitches`
- 键盘灯回退：单区 `NucKeyboardLighting`，否则 `EcKeyboardLight`

### `tools/dev/wpf-p0/CustomModeWindow.xaml.cs`

- `CurrentHw = WpfBackend.Hw ?? Program.hw`（EC 离线 Hw 依赖此项）
- `RefreshDeviceLimits()`：每次打开自定义窗口重算范围
  - CPU PL 范围
  - TCC
  - GPU TGP/DB（NVML 物理值）
  - FPPT（Turbo PL4 + 50，最大 255×单位）
- 自定义应用：
  - `NucPerformanceMode.TryApplyCustom`
  - `GcuEcExperimental.TryApplyMode(3)`
  - `EcGpuPower.TryApply`
  - GPU OC
  - 风扇表 + FanIsolated + FanRamp
- 数值列已改 `TextBox`，回车/失焦提交
- 删除自定义：回相邻上一个配置，不再固定第一个
- 新建自定义：默认设置，不再从当前配置复制

### `tools/dev/wpf-p0/FanCurveWindow.xaml.cs`

- `_hw = WpfBackend.Hw ?? Program.hw`
- EC 路由：
  - `NucFanTable.TryApply`
  - `FanIsolated`
  - `FanRamp`（读保存的 `FanSwitchSpeedOn/FanSwitchSpeedMs`）
  - 恢复默认 `TryRestoreDefault`
- 风扇占空比写入值 = 实际百分比 × 2
  - 55% = 110
  - 80% = 160
  - 90% = 180
  - 100% = 200

### `tools/dev/wpf-p0/OriginalStyleWindow.xaml.cs`

- `TryStartExperimentalOfflineBackend`：
  - 挂 `Program.hw`
  - `EnableLocalMonitoring`
  - 状态显示“EC 实验模式”
- 双路：GCU 成功 → Gcu；失败 5 次 → Ec
- 性能键：
  - 在线：静音 → 平衡 → 狂暴 → 自定义 → 静音
  - 离电：静音 ↔ 平衡
- 休眠/唤醒：
  - Suspend：`ResetDirectGpuOverclockForSleep()` + 暂停采样
  - Resume：8 秒后恢复采样，按需恢复 GPU OC
- 诊断导出：完成询问是否打开目录，不再自动打开
- 触摸板磁贴：使用 OSD 同款 `Assets/Icons/icons8_touchpad_96.png`

---

## 四、当前版本与交付

项目版本：

```text
0.293.7.5 / beta23.7.5
```

实验包目录：

```text
D:\openLM\dist\update\experimental-gcu-ec\
```

最新 DLL：

```text
L-Mechrevo.dll
218476C7007CDD5FA0F9C9BC34C675DE84D41FAE3C7F5808C5D0F83B21B3FAA8

MechrevoLite.Core.dll
10A7FCBDE3BBD1DCE19B0C92206CCF48B80E906498E06F1E32CE377641BF83B7
```

包内还有：

```text
启动实验版.cmd
关闭实验版.cmd
GcuEcProbe.ps1
GcuEcSnapshot.ps1
README-实验说明.txt
```

说明：`GcuEcSnapshot.ps1` 用 ACPIDriver 只读 `0x0700~0x07FF`，可对比 OpenRevo 切模式前后差异。

---

## 五、已验证 / 待验证

### 已验证

```text
EC-only 离线首页可启动
EC 模式写入/回读 OK
FPPT 显示
DB 写入 25W（日志 gpu-power-ec OK）
风扇曲线/风扇隔离/ramp 写入 OK
自定义白灯位/1798 门控
休眠唤醒 NVAPI 释放
性能键 EC 循环
```

### 待验证 / 已知问题

1. **键盘灯类型判定不完整**
   OpenRevo 顺序是：
   ```text
   HID PerKey → HID 4Zone → EC SingleZone → EC MonochromeWhite → None
   ```
   当前 EC 回退只做：
   ```text
   EC SingleZone（1894 bit2）→ EcKeyboardLight 白光
   ```
   还没有优先判断 HID PerKey/4Zone。HID 键盘可能被错误接到 EC 单区/白光。

2. **DB 不生效**
   最新日志已能写 `DB=25W ... ⇒ OK`。若界面/实际仍不对，先看：
   ```text
   [gpu-power-ec] ...
   ```
   若出现“GPU 物理功耗上限不可用”，检查 nvidia-smi 路径/输出解析。

3. **FPPT 上限**
   默认上限 = `Turbo PL4 + 50`，最大 `255×单位`。
   PL4 单位由 EC 1831 bit7 决定（1W/2W）。
   Intel 才有单/双倍切换，AMD 不显示。

4. **休眠 0xC0000005**
   已加 NVAPI 释放 + 8 秒延迟恢复，仍需多轮休眠/唤醒实测。

5. **自定义风扇曲线**
   已写 EC 表；若与强冷同时启用，强冷优先级需确认。

---

## 六、测试步骤

```text
1. 双击 启动实验版.cmd
2. 确认顶部“EC 实验模式”
3. 性能模式逐档切换
4. 自定义：改 PL/DB/GPU TGP/OC/风扇曲线 → 应用
5. 看日志：
   [gcu-ec]
   [gpu-power-ec]
   [gpu-oc]
   [fan]
6. 休眠/唤醒，确认不崩、超频按需恢复
7. 停止 GCU 测试：
   Stop-Service GCUBridge -Force
   启动实验版
```

恢复 GCU：

```powershell
Start-Service GCUBridge
```

---

## 七、仓库状态

当前本地 `main` 最新提交：

```text
76ec45d2 exp: EC键盘灯回退与风扇灵敏度上限
```

工作区有大量未跟踪参考目录/笔记（`_openrevo-dev`、`_baseline-*`、各类实验 txt），不影响代码构建。

实验代码未推送、未发 Release。
正式稳定版仍是 beta23.7.4。

---

## 八、纪律提醒

- 机主没说「打包/重新打包」→ 不打包、不做安装包。
- 只修已确认问题，不做顺手扩大改动。
- 代码注释不写反编译/逆向/官方组件来源。
- OpenRevo 协作：不主动提 PR、不主动构建 exe，等机主明确要求。
- GPU OC 默认值（core/mem）归属机主，不要把归属写成上游实现。
