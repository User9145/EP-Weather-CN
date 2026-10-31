# EP「资讯与兴趣」本地版 v1.0 · 源码包

在 Windows 11（21H2–25H2）+ ExplorerPatcher 上还原 Win10 任务栏「资讯和兴趣」组件的本地实现。
技术细节见 `docs/技术说明.md`。

## 目录结构

```
src/
  WeatherGen.cs      页面生成器（天气 Open-Meteo / 新闻 RSS / 行情 qt.gtimg.cn → w.html + news.html）
  EpCmdServer.cs     本地命令通道 + 内容调度 + 弹窗窗口外壳自愈 + COM 保活（127.0.0.1:24381）
  PatchDll.cs        ep_weather_host.dll 二进制补丁工具（URL 槽 + 视口高度 600→1200）
  GuiStrings.cs      Win32 字符串资源改写工具（设置界面 "Weather" → "News and Interests"）
scripts/
  deploy20.bat       本机热更新：编译产物 → C:\Program Files\ExplorerPatcher\ep_weather_local
  install.bat        发行安装脚本（管理员运行，含 DLL 自愈）
  uninstall.bat      卸载脚本
  shortname.ps1      8.3 短名查询（安装脚本辅助）
assets/
  icons/             天气图标集
  fallback_w.html    离线兜底页面
docs/
  技术说明.md
```

## 构建

仅需系统自带的 .NET Framework 4.0 csc（无需安装任何 SDK/包）：

```bat
set CSC=C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe
%CSC% /nologo /target:winexe /out:ep_cmd_server.exe      src\EpCmdServer.cs
%CSC% /nologo /target:winexe /out:ep_weather_local.exe   src\WeatherGen.cs
%CSC% /nologo                /out:ep_patch_dll.exe       src\PatchDll.cs
%CSC% /nologo                /out=GuiStrings.exe         src\GuiStrings.cs
```

- 两个常驻组件必须 `/target:winexe`（无控制台窗口，开机自启不弹黑窗）。
- 两个控制台工具（PatchDll / GuiStrings）保留默认子系统，安装脚本需要看它们的输出。

## 打包发布

1. 新建目录 `EP_Weather_Local_v1.0\`，拷入 4 个 exe、两个 `.exe.config`（内容为
   `<configuration><runtime><gcConcurrent enabled="true"/></runtime></configuration>`）、
   `scripts\` 下的 install/uninstall/shortname、`assets\icons\`、`assets\fallback_w.html`、
   EP 官方 `ep_weather_host.dll` 副本（放 `official\` 供安装时自愈）、运行一次生成器产出的
   `w.html`/`news.html`、以及面向用户的 `说明.txt`。
2. 压缩为 `EP_Weather_Local_v1.0_vNN.zip`。包内版本号恒为 v1.0，迭代号只在文件名上体现。

## 运行依赖

- ExplorerPatcher（启用 Win10 任务栏）
- WebView2 Runtime（install.bat 缺失时自动补装）
- .NET Framework 4.0（Win10/11 自带）
