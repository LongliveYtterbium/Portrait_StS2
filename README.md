# STS2Portrait

《杀戮尖塔 2》（Slay the Spire 2）的竖屏与触屏 UI Mod，目前提供 PC 预览版。它调整战斗、手牌、地图、商店、休息处、奖励、牌组选择、事件和古神页面的显示与操作区域，并配有 78 张竖屏背景。

开发和当前验证在 PC 上完成，覆盖若干 9:16、9:19.5 与 9:20 场景。Android 启动器集成、物理 Android 客户端触控与性能验证仍待完成；当前发布不含 APK。完整连续一局、全部怪物和全部动态事件的验收仍未完成。

## 安装

从 [Releases](https://github.com/LongliveYtterbium/Portrait_StS2/releases) 下载预览版安装 ZIP，解压后将 Mod 文件夹放到游戏目录：

```text
<Game>/mods/STS2Portrait/
    STS2Portrait.dll
    STS2Portrait.pck
    mod_manifest.json
```

三项运行文件必须使用同一发布版。另行安装并启用 [STS2-RitsuLib](https://github.com/BAKAOLC/STS2-RitsuLib)，然后启用 STS2Portrait。使用游戏自身的窗口设置选择竖屏分辨率。切回横屏时恢复原布局；不同页面和版本组合仍可能存在未覆盖的问题。

已记录的验证环境为 Slay the Spire 2 `v0.111.0`（`public-beta`）、`.NET 9` 与 `STS2-RitsuLib 0.6.2`。其他版本组合未验证。Mod 包不附带游戏程序集、RitsuLib、存档或 Android 启动器。

## 当前内容

- 顶部房间、楼层与首领图标按手机尺寸放大，保留原图案和提示。
- 奥斯提模型与蓝火隐藏，初始遗物图标和原版红色血条显示在玩家头像上方。
- 奖励选牌再次点击同一张牌即可领取；兼容快速重打 Mod 的局内菜单按钮。
- 战斗手牌采用 5 × 2 布局，保留长按完整阅读与拖动出牌。
- 药水、玩家血量和状态靠近主要操作区域；敌人血量与意图围绕原模型显示。
- 地图、搜刮、选牌、商店、休息处、普通事件和古神页面调整为竖屏显示。
- 商店商品同屏显示；卡牌、按钮、图标、字体、阅读框与动态主体复用游戏原组件。
- 静态场景使用独立 Mod 资源包内的竖屏扩图背景。

这是仍在推进的预览版。局部场景有实际 PC 验证记录，不能视为全部内容或移动端已通过。

## 从源码构建

源码 checkout 可构建 DLL。运行还需要同版 `STS2Portrait.pck`；PCK 与背景 PNG 不存入 Git，其安装包通过 Release 提供。当前仓库没有独立完整的资源再生成流程。缺少 PCK，或资源包不包含当前 DLL 需要的背景，会让对应竖屏界面报资源加载错误；首次安装需使用完整安装包。

准备 Windows、PowerShell、可构建 `net9.0` 的 .NET SDK，以及已安装的游戏和 RitsuLib 引用包。本项目读取本机依赖，不自动安装游戏、框架或工具。以下默认路径可按实际 Steam 库位置调整：

```powershell
$env:STS2_GAME_DIR = 'C:\Program Files (x86)\Steam\steamapps\common\Slay the Spire 2'
$env:STS2_RITSULIB_REFS = 'C:\Program Files (x86)\Steam\steamapps\workshop\content\2868840\3747602295'

# A fresh clone needs obj/project.assets.json before the no-restore build.
# With the net9.0 targeting pack already installed, this local source avoids remote feeds.
dotnet restore .\STS2Portrait.csproj --source .
.\tools\build.ps1
```

`STS2_RITSULIB_REFS` 应指向包含 `RitsuLib.References.props` 的目录，引用包须包含 `compat/0.111.0`。项目将兼容目标固定为 `0.111.0`。首次 restore 负责生成 SDK 需要的项目资产记录；构建脚本之后使用 `--no-restore`。缺少 SDK 的 `net9.0` targeting pack 或本地引用时，需要先准备相应依赖。

构建产物默认位于 `bin/Debug/net9.0/STS2Portrait.dll`；日志和最新构建报告位于 `logs/`。构建脚本检查产物和输入，执行失败会返回非零退出码。

先正常退出游戏，再进行代码部署检查：

```powershell
# Dry run: inspect the proposed DLL and manifest deployment.
.\tools\deploy.ps1 -GameDir $env:STS2_GAME_DIR

# Apply only after a successful build and while the game is closed.
.\tools\deploy.ps1 -GameDir $env:STS2_GAME_DIR -Apply
```

部署工具只复制自己的 DLL 和 manifest；可通过 `-WithPdb` 附带调试符号。它不复制 PCK，因此首次安装仍须从同版发布包放入资源文件。覆盖前会在本项目日志目录保存备份。

## 来源与许可范围

本项目是非官方 Mod。Slay the Spire 2 及原游戏美术、角色、字体、图标归其相应权利人；竖屏背景以原游戏插画为参考进行 AI 扩图。RitsuLib 由其上游项目提供，按上游自身条款使用。

本仓库未另行授予 MIT 等开源许可证；公开源码不代表游戏素材或派生背景已获新的开源授权。素材说明和适用范围见[CREDITS.md](CREDITS.md)。
