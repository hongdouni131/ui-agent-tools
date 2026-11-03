# UI Agent Tools 0.1.0

首次公开版提供从UI Spec到可编辑uGUI Prefab的工具链，适合桌面游戏菜单与素材拼装原型。[首页](README.md)展示实际效果，[使用指南](Documentation~/usage.md)介绍安装后操作。

## 包含内容

- `Editor/`：层级检查、v1/v2校验、计划与新Prefab生成、Sprite查询与导入审计、布局测量、保存后审计和诊断。
- `Runtime/`：生成确认弹窗使用的通用控制器与节点身份组件。
- `Tests/Editor/`：默认不启用的核心EditMode测试。
- `Samples~/`：弹窗、AudioMixer Slider、菜单流程与偏好保存三个可选C#接入示例。
- `Documentation~/`：使用指南、三张实际效果截图及其来源说明；加上README、CHANGELOG、LICENSE、package.json和Unity元数据。

不包含字体/美术源文件、烘焙图集、生成Prefab、演示场景、内部记忆、Unity缓存、日志/配置、离线Python工具、Funplay或Bridge。展示截图不是可安装的演示场景。

## 安装与Agent

下载ZIP并解压，在Package Manager的Add package from disk中选择 `package/package.json`。该目录被引用，安装后不要移动。Package位于仓库根，也可通过Git UPM使用固定标签：

```text
https://github.com/hongdouni131/ui-agent-tools.git#v0.1.0
```

Agent路线另需Funplay和 [Bridge v0.3.0](https://github.com/hongdouni131/codex-funplay-unity-bridge/releases/tag/v0.3.0)，Bridge部署要求Python3.11。Package不会注册MCP，Bridge也不会安装Package。

## 验证与限制

实测Unity **2022.3.62f3c1**，核心回归 **321 passed / 0 failed / 0 skipped**。此前内部r4空项目安装验证了依赖编译、两个首次使用菜单、三Sample导入与四类型编译；独立安装版Bridge及重启后的常驻会话已完成真实只读配对。首次公开整理没有修改C#或asmdef，不重复运行相同回归。

安装、编译、自动化事件或数字音频信号验证不能代替真实游戏接线、硬件输入和主观听感。当前不提供原地Prefab更新、任意C#生成、移动端或SpriteAtlas打包支持；其他Unity版本与Player构建未验收。生成仍需完整Prepare计划和开发者授权，未知写入不自动重放。

## 许可

MIT，Copyright (c) 2026 hongdouni131，见 [LICENSE](LICENSE)。截图资源来源见 [image credits](Documentation~/image-credits.md)，第三方依赖适用各自许可。
