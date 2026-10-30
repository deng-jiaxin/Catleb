# 豹嗷喵 Windows 桌宠

一个独立运行的 Windows 桌宠原型。程序使用 C# WinForms、Windows 分层窗口和逐像素 Alpha 渲染，不需要 Python、浏览器或联网服务。

## 功能

- 透明悬浮桌宠窗口，透明区域鼠标穿透
- 点击播放互动动作
- 按住并拖动角色进入「拎起」动作
- 7 组动作：互动、巡逻、打扫、玩苹果、弹吉他、酷、拎起
- 右键菜单：动作选择、暂停、随机动作、巡逻、缩放、隐藏、找回、退出
- 系统托盘图标
- 可选置顶、75% / 100% / 125% 缩放和便携式位置设置
- 保留原始动作帧间隔和透明角色素材

## 运行

进入 `desktop-pet` 文件夹，双击 `豹嗷喵.exe`。运行时必须让 exe、`pet.ico` 和 `assets` 文件夹保持在一起。

## 构建

在 Windows PowerShell 中运行：

```powershell
./build.ps1 -OutputDirectory "F:/path/to/build"
```

构建脚本使用 Windows 自带的 .NET Framework C# 编译器。输出目录必须不存在，以避免覆盖已有构建。

## 素材格式

`desktop-pet/assets/pet.json` 是动作清单。每个动作由 320×320 的 RGBA PNG 帧组成，包含每帧时长、动作锚点和鼠标抓取点。巡逻和打扫动作还使用逐帧锚点，保留原动画的横向位移。

## 自检

```powershell
./豹嗷喵.exe --self-test report.json
./豹嗷喵.exe --smoke-test smoke-report.json
```

`--self-test` 检查全部素材和透明帧；`--smoke-test` 在离屏窗口中检查渲染、动作切换、暂停、缩放、拖拽状态和屏幕边界逻辑。

当前版本不包含音效、开机启动、联网或自动安装器；待机使用原始默认动作首帧。
