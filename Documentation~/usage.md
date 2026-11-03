# 使用指南

[返回首页](../README.md)

## 安装后先做一次只读检查

确认Package Manager显示UI Agent Tools 0.1.0、依赖解析结束且Console没有Package编译错误。正常使用不需要启用testables或运行全套测试。

在自己的Unity项目创建 `Assets/Editor/UIAgentFirstUse.cs`：

```csharp
using UnityEditor;
using UnityEngine;
using UIAgent.Tools.Editor;

public static class UIAgentFirstUse
{
    [MenuItem("Tools/UI Agent First Use/Inspect Active Scene")]
    public static void InspectActiveScene()
    {
        Debug.Log(UiHierarchyInspector.InspectActiveSceneJson());
    }

    [MenuItem("Tools/UI Agent First Use/Audit Active Scene")]
    public static void AuditActiveScene()
    {
        Debug.Log(DesktopMenuAuditor.AuditActiveSceneJson());
    }
}
```

编译后执行两个菜单，在Console查看JSON。它们不修改或保存场景，不需要字体和素材；空场景不是菜单，审计可能合理地报告问题。这两个菜单来自上面的消费项目脚本，不是Package自动安装的窗口或菜单。

## Agent 路线

1. 在目标Unity项目安装Package与Funplay；另用Bridge 0.3.0安装器为该项目部署MCP。Bridge仍要求Windows `py` 启动器提供Python 3.11和依赖下载网络。
2. 配置并重载对应服务，核对项目路径、Editor readiness、22个Bridge工具及8项capability。Funplay的180工具清单是另一层发现结果；健康检查本身不证明Package API可用。
3. 准备已保存且无未保存改动的普通场景，退出Play Mode和Prefab Mode。准备位于Assets下、Material非空且覆盖所有文案的TMP Font Asset；英文首例可导入TMP Essential Resources后用Liberation Sans，中文需要另外准备字形。
4. 描述界面。Agent查询已导入素材，必要时提供本机HTML候选页。选择后重新核对Sprite GUID、local file ID、Rect、Border与依赖，合并到Spec；不自动修复导入器、不自动切片。
5. Validate → Prepare → 展示完整计划 → 开发者授权 → Generate一次。使用 `Assets/AgentDemos/UIAgent/Prefabs/` 下的新路径；既有输出会被拒绝。
6. 保留原Spec，读回Prefab并审计，再在Unity查看、测试输入。有问题时先诊断，形成新Spec、新路径和新计划；当前不提供原地Prefab更新。超时或断连后的未知写入先读回调查，不盲目重放。

v2即使只有Panel也要求字体引用。候选页提交只选择资源；确认参数与指纹检查计划一致性，不证明操作者身份，调用方仍负责取得和记录开发者授权。

## C# API 路线

Editor入口位于 `UIAgent.Tools.Editor`。可从自己的Editor脚本调用，无需安装Bridge或Agent。

| 入口 | 用途 |
| --- | --- |
| `UiHierarchyInspector.InspectActiveSceneJson()` / `InspectPrefabJson(path)` | 读取场景或Prefab层级 |
| `DesktopMenuAuditor.AuditActiveSceneJson()` | 只读桌面菜单审计 |
| `UiCompositionSpecValidator.ValidateJson(specJson)` | v2树结构与字段校验 |
| `UiCompositionSpriteCatalog.QueryJson(...)` / `UiCompositionSpriteImportAuditor.AuditJson(...)` | 查询精确Sprite身份与导入就绪性 |
| `UiCompositionGenerator.MeasureLayoutJson(specJson)` | 用Unity原生布局测量参考尺寸 |
| `UiCompositionGenerator.PrepareJson(specJson)` | 检查项目资源并准备计划，无生成写入 |
| `UiCompositionGenerator.GenerateJson(specJson, confirmation)` | 核对当前计划后生成新Prefab |
| `UiCompositionAssetAuditor.AuditPrefabJson(specJson)` | 保存后的Prefab与原Spec对照 |
| `UiCompositionDiagnostics.DiagnoseJson(specJson)` | 隔离参考分辨率下测量文字、越界与拉伸风险 |

具体参数和返回类型见 [Editor源码](../Editor)。v2 Schema定义见 [UiCompositionSpecValidator.cs](../Editor/UiCompositionSpecValidator.cs)，生成确认类型见 [UiCompositionGenerator.cs](../Editor/UiCompositionGenerator.cs)。v1 `UiSpecValidator` 与 `MainMenuGenerator` 保留原契约。

v1生成新Prefab和场景对象；v2只写新Prefab及明确归属的文字效果材质。二者都不自动保存活动场景、不生成游戏业务代码。保存资源的审计不代表视觉、运行输入或全部分辨率验收。

## 可选 Samples

在Package Manager的Samples页按需导入：

| Sample | 提供什么 | 项目需配置什么 |
| --- | --- | --- |
| [Confirmation Dialog Integration](../Samples~/Confirmation%20Dialog%20Integration/README.md) | 弹窗打开与确认/取消接入 | 生成的弹窗、EventSystem和业务回调 |
| [Audio Mixer Slider Integration](../Samples~/Audio%20Mixer%20Slider%20Integration/README.md) | Slider映射AudioMixer暴露参数 | 三Slider、Mixer、参数名和音源路由 |
| [Menu Flow Integration](../Samples~/Menu%20Flow%20Integration/README.md) | 页面与焦点、显式Save/Defaults | 页面、按钮、独立偏好存储键；音频另导入上项 |

导入脚本位于 `Assets/Samples/UI Agent Tools/0.1.0/`。导入不会自动创建或连接Prefab、Mixer和演示场景；请按Sample README分配Inspector引用。Escape取消行为与实体手柄未纳入当前契约。

## 测试

常规使用无需运行回归。开发时在一次性或已备份的Unity项目中，把 `com.hongdouni.ui-agent-tools` 加入 `Packages/manifest.json` 的 `testables` 数组，再从 **Window → General → Test Runner** 运行EditMode测试。

测试需要TMP Liberation Sans测试字体位于 `Assets/TextMesh Pro/Resources/Fonts & Materials/LiberationSans SDF.asset`，以及匹配当前输入设置的模块。交互测试前保存当前场景、退出Prefab Mode，不保留未保存工作。测试只创建/清理自身的临时场景与 `Assets/AgentDemos/UIAgent/` 下的资源；批处理有专用的空scratch场景处理。

最新核心基线321/321不包含另外12项开发Sandbox的Sample验收测试。此前空项目r4已验证依赖编译、上面的两个菜单和三Sample导入；首次公开准备只改许可/文档及展示图，C#与asmdef保持一致。
