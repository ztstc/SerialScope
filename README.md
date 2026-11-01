# SerialScope · 串口波形工作台

一个使用 **WinUI 3 + Win2D** 编写的 Windows 串口显示器。交互方式参考 VOFA+：左边配置串口与协议，中间实时绘制多通道波形，也可以在数据终端里收发原始字节。

界面采用 WinUI 3 原生控件、标准标题栏和系统主题色；浅色/深色模式跟随 Windows 设置，按钮强调色跟随系统的“个性化 → 颜色”设置。

## 功能

- **串口连接**：端口枚举、刷新、可编辑波特率、数据位、校验位、停止位、流控、DTR/RTS。
- **实时多通道波形**：自动识别 1–8 个通道，通道颜色、当前值和可见性一览。
- **高性能绘制**：Win2D/CanvasControl 渲染，30 fps 刷新，支持暂停后鼠标悬停读取各通道数值。
- **可视化控制**：暂停/继续、清空波形、自动 Y 轴缩放或手动范围、5/10/30/60 秒时间窗口、显示/隐藏单条曲线。
- **数据终端**：文本/HEX 显示、自动滚动、复制、清空、本地时间戳（HEX 模式）。
- **发送面板**：文本/HEX 发送、CR/LF/CRLF 行尾、循环发送与可调间隔。
- **CSV 导出**：把缓冲区中的全部帧导出为 `Time(s),CH1..CH8` 的 CSV 文件。
- **内置演示信号**：不需要串口硬件即可体验 6 通道实时波形和终端数据；启动时加 `--demo` 可以直接进入演示。
- **VOFA+ 兼容协议**：FireWater、JustFloat、RawData。
- 原生 Windows 外观、高 DPI 适配。

## 协议

| 协议 | 数据格式 | 波形 |
| --- | --- | --- |
| `FireWater` | ASCII 十进制数，逗号（也接受空格/分号）分隔，`\n` 结束一帧，例如 `1.25, 3.14, -0.5\n` | 多通道 |
| `JustFloat` | 每个通道 4 字节小端 `float32`，帧尾 `00 00 80 7F`（+Infinity），可连续发送多帧 | 多通道 |
| `RawData` | 任意字节流 | 无波形，仅在数据终端查看 |

- 单帧最多解析 **8 个通道**。
- `FireWater` 的首个有效帧决定通道数量；后续帧缺失的通道补 0。
- `JustFloat` 帧内的 float 数量决定通道数量，帧尾不计入通道。
- 终端在非 HEX 模式下按 UTF-8 文本显示；HEX 模式会自动加上接收时间戳。

## 快速开始

### 使用已编译版本

```powershell
# 直接运行发布目录中的程序
.\artifacts\SerialScope\SerialScope.exe

# 或解压可分发包后运行
# artifacts\SerialScope-win-x64.zip  ->  SerialScope\SerialScope.exe
```

发布包已自带 Windows App SDK 运行时，无需单独安装 Windows App Runtime；需要本机有 **.NET 8 运行时（x64）**。

### 从源码构建

环境要求：

- Windows 10 1809+ / Windows 11
- .NET 8 SDK
- Visual Studio 2022（可选；安装“.NET 桌面开发”和“Windows 应用 SDK”工作负载更省事）

```powershell
dotnet restore .\src\SerialScope\SerialScope.csproj
dotnet build .\src\SerialScope\SerialScope.csproj -c Release -p:Platform=x64
```

或：

```powershell
powershell -ExecutionPolicy Bypass -File .\build.ps1
```

生成的可执行文件位于：

```text
src\SerialScope\bin\x64\Release\net8.0-windows10.0.19041.0\win-x64\SerialScope.exe
```

> 项目使用 `WindowsAppSDKSelfContained=true`，因此构建产物较大，但目标机器不需要预装 Windows App SDK Runtime。

### 命令行参数

```text
SerialScope.exe --demo               启动内置演示信号
SerialScope.exe --window=1440x900    指定窗口逻辑尺寸 (DIP)
```

## 使用说明

1. 点击右侧刷新按钮枚举串口，选择端口和波特率。
2. 在“数据协议”中选择与固件端一致的协议。
3. 点击“连接设备”开始接收。首帧到达后，右侧会自动创建通道。
4. 也可以打开“演示信号”，直接查看多通道波形。
5. 切到“数据终端”可查看原始数据；连接串口后可在右侧发送文本或 HEX。
6. 波形页可暂停、清空、导出 CSV；取消右侧通道勾选可以单独隐藏曲线。

### 生成 FireWater 数据的固件示例

```c
printf("%.3f,%.3f,%.3f\n", ch1, ch2, ch3);
```

### 生成 JustFloat 数据的固件示例

```c
float frame[3] = { ch1, ch2, ch3 };
Serial.write((uint8_t*)frame, sizeof(frame));
const uint8_t tail[4] = { 0x00, 0x00, 0x80, 0x7F };  // +Inf
Serial.write(tail, 4);
```

> 注意字节序：JustFloat 使用小端 float32。STM32/ESP32 等常见 MCU 原生即小端。

## 项目结构

```text
src/SerialScope/
  Controls/WaveformView.*       Win2D 波形绘制、坐标轴、十字线
  Models/                       通道、串口参数、协议枚举
  Services/
    SerialConnection.cs         串口读写与读线程
    ProtocolDecoders.cs         FireWater / JustFloat / RawData 解码
    WaveformBuffer.cs           线程安全环形缓冲区与快照
    AcquisitionEngine.cs        采集、解析、时间戳、统计
    TerminalLog.cs              终端环形日志
    DemoSignalSource.cs         内置演示信号发生器
  MainWindow.*                  界面与交互
  Program.cs                    应用入口、可选诊断日志
  App.xaml                      原生卡片样式与共享资源
```

## 已知边界

- 单帧最多 8 通道，更多通道会被截断。
- 不支持数据回放、脚本协议或用户自定义帧结构。
- 串口拔出后不会自动重连，会显示错误提示。
- CSV 导出包含当前环形缓冲区中保留的全部帧，不是无限历史。
- 首次启动打开串口时，部分开发板会因 DTR 变化复位；可在“高级参数”中关闭 DTR 后重新连接。

## 开发

打开 `SerialScope.sln`（Visual Studio 2022）或直接使用 `dotnet`。主项目目标框架为 `net8.0-windows10.0.19041.0`，仅提供 x64 配置。

如果启动异常，可设置环境变量 `SERIALSCOPE_DIAGNOSTICS=1`，程序会在可执行文件目录写入 `startup.log`。

## 许可

本项目为示例工程，可自由修改用于个人或商业项目。随附的第三方 NuGet 包遵循其各自许可证。
