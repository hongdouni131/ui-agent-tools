# Menu Flow Integration

可选接入示例：将生成的主菜单、设置页与 `ConfirmationDialog` 组合成页面流程，并演示显式保存音量。此目录不包含固定美术Prefab；消费项目分配自己的生成结果。

## 导入与连接

1. 在Package Manager的Samples导入 **Menu Flow Integration**。需要AudioMixer时另导入 **Audio Mixer Slider Integration**，创建并暴露项目自己的三个Mixer参数。
2. 在场景放置主菜单、设置、确认弹窗Prefab实例和一个匹配输入配置的EventSystem。保持确认弹窗初始隐藏。
3. 创建始终活动的接入对象，添加 `MenuFlowIntegrationExample`，分配两页、设置/返回/确认入口/测试入口按钮、设置初始焦点、弹窗、主菜单状态文字和EventSystem。
4. 同一活动对象添加 `VolumeSettingsIntegrationExample`，分配三个Slider、Save和Defaults按钮、设置状态文字；为项目填写独立的 `preferenceKey`。这两个组件不能放在会被自己隐藏的页面根上。
5. AudioMixer示例组件也放在始终活动的对象上，分配相同的三个Slider。AudioSource须路由到匹配Mixer组，Mixer才会影响听见的声音。
6. `onTestSignal` 是可选UnityEvent，示例使用它播放测试音；Confirm/Cancel只更新状态计数，不执行退出/加载游戏。

## 状态政策

- 拖动立即影响当前UI和已连接的Mixer；只有Save写入PlayerPrefs并调用Save落盘。
- Defaults只恢复当前默认预览；再次Save才覆盖已保存值。
- Back保留当前内存预览，不自动保存、不自动撤销。重新进入同一运行会话仍保留当前值；新的运行会话读取最后显式保存值。
- 首次Start读取存储，随后启用/禁用不重复加载；事件订阅和解除成对处理。
- 记录为version=1的规范化三通道JSON。字段缺失、未知版本、非数值或超出0..1会整体拒绝，使用默认预览并显示提示；不会自动覆盖损坏记录。
- PlayerPrefs适合小型本地偏好，不是加密存档或事务数据库。例子不使用DeleteAll，也不替消费项目定义存储迁移。

## Sandbox演示

开发Sandbox中的 `Assets/AgentDemos/UIAgent/Scenes/DeepSpaceMenuFlowSandboxDemo.unity` 已连接深空风三页、Mixer与两段自行生成的测试音。声音默认关闭，点击Test Signal播放约2秒。音量映射为线性分贝（默认-80..0 dB），不是线性振幅；首次默认值82/65/72可能较轻。

独立演示场景和音源属于Sandbox，不随本Sample自动导入。完整设置过程、证据和限制见源码项目 `docs/menu-flow-integration.md`。
