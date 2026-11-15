# 战斗 Buff 显示（BattleBuffDisplay）

独立 CesiumLoader 小 MOD，不依赖 AstralProbe 主 MOD，也无需安装旧版 SDK。
保留 `AstralProbe.Features.Battle` 命名空间和 `FightBuff*`、`NativeBuff*` 业务类名，方便后续对照旧项目。

## 功能与配置

- **始终显示**：战斗攻击方和防守方的原版 Buff 列表、层数和剩余回合，不增加多余的启用开关。
- **可选特效**：在此 MOD 自己的 `config.json` 中设置 `battle.fightBuffEffects: true`，启用游戏原本未在 PK 画面显示的额外 Buff 特效，默认 `false`。
- 已由原版渲染的特效不会重复创建，进入其他场景或结束战斗时清理附加面板和特效。

## 构建

放在 CesiumLoader 仓库的 `mods/BattleBuffDisplay/`（或设置 `CesiumLoaderRoot`、`CesiumSdkDll`、`GameRefsDir`），使用对应版本的 SDK 和游戏托管引用编译：

```powershell
dotnet build BattleBuffDisplay.csproj -c Release
```

将 `BattleBuffDisplay.dll` 和 `BattleBuffDisplay.json` 放在 `AstralParty_ModLoader/mods/BattleBuffDisplay/`（参见加载器文档）。
配置位于此 MOD 自己的 `config.json`；若之前启用过主 MOD 的 `battle.fightBuffEffects`，可手动迁移该值。

**注意**：源码包不含已编译的 DLL。本轮未在游戏内完成战斗表现验证。
