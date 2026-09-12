# AGENTS

面向 AI 代理的工程说明，避免踩已知的坑。人类开发者可跳过。

## 项目结构

```
<仓库根>\
├── Dream Studio\            # Studio 宿主（C# 13.0 / Avalonia 11.3 / net9.0）
├── Dream Engine\            # 引擎源码模板（ActionScript 3.1 / AIR 50.0）及第三方库 SWC —— 改引擎代码必须改这里
├── FlappyTest\              # 当前克隆源：人工测试工程（EnginePaths.TemplateSourcePath 指向它）
├── .workspace\Project\      # Dream 工作副本 —— 每次 Studio 启动被清空重建，禁止直接编辑
└── Dream Engine Third Party\  # 第三方依赖库（源文件）（例如 Box2D / Starling）
```

## 最重要的规则：引擎代码改模板，不要改工作副本

- 引擎源码的**唯一真实来源**是 `Dream Engine\src`（引擎模板目录）。
- `.workspace\Project` 只是工作副本：Studio 每次启动时 `TemplateCloner`
  （`Dream Studio\Engine\TemplateCloner.cs`）会**先删除整个目录再整体复制克隆源**（跳过 bin/obj）。
  对 `.workspace` 的任何修改都会在下次启动时被覆盖丢失。
- **克隆源只是"开箱即用的人工测试工程"，不是真实工程**：它的作用仅仅是让 Studio 启动后立刻有个
  能编译、能运行、能点开各面板的项目，免去每次手动选项目；指向哪个工程随当前开发阶段而定，
  改 `EnginePaths.TemplateSourcePath`（`Dream Studio\Engine\EnginePaths.cs`）一行即可切换。
  它和上面的"引擎模板目录 `Dream Engine`"是两回事。
- 因此所有 `.as` 文件的改动必须落在 `Dream Engine\` 下，改 `.workspace` 等于没改。
- **克隆源自带的引擎副本要当成镜像看**：`FlappyTest\src\dream\engine\**` 是引擎源码的一份镜像
  （它的 asconfig `source-path` 就是 `src`），工作副本编译的是这份镜像而非 `Dream Engine\src`——
  改完引擎必须同步镜像（SHA256 应逐字节一致），否则 Studio 跑的还是旧引擎。
- Studio 启动时会自动用 AIR SDK 的 mxmlc 重新编译引擎（并重新生成 `ComponentIndex.as`），
  无需手动编译；但改完 `.as` 需要**重启 Studio** 才生效。

## 双端消息协议

- Studio ↔ Engine 走 TCP，消息类型常量定义在两处，**必须保持一致**：
  - C#：`Dream Studio\Engine\Comm\Message.cs`（`MessageTypes`）
  - AS：`Dream Engine\src\dream\engine\comm\MessageTypes.as`
- Engine handler 均包在 `CONFIG::STUDIO` 条件编译块内（asconfig.json 里 define `CONFIG::STUDIO=true`）。

## 构建

- Studio：`dotnet build "Dream.Studio.csproj" -c Debug`（工作目录 `Dream Studio\`）
- Engine：由 Studio 启动流程自动编译，无需手动命令。
- 发布构建（APK / bundle）：由 Studio 构建面板驱动，链路见下节。

## 发布构建链路

数据流：`BuildWindow.CollectPlan()` 收集设置 → `EngineBuilder.Stage()` 暂存 → `AdtPackager.Run()` 调用 ADT 打包。

- **`EngineBuilder.Stage` 步骤是固定的**：校验 → 清暂存目录 → 生成 `ComponentIndex` →
  release 编译（`CONFIG::STUDIO=false`）→ 收集资源 → 写描述符与清单 → 生成图标。
- 构建窗口四个标签页：Windows / Android / iOS / Command line，**默认打开 Windows**
  （`PackPlatform` 默认 `"windows"`，`SelectStartupTab` 按 `Tag == _settings.Build.Platform` 选中）。
  注意 `SavePlatformSettings()` 首行会把 `_settings.Build.Platform = SelectedPlatform`，即切页就改默认平台。
- ADT 目标矩阵（`AdtPackageCommand.DefaultTarget`）：
  - 桌面（windows / macos / linux）→ `bundle`（另支持 `cmdline`）
  - android → `apk` / `apk-debug` / `apk-emulator` / `apk-captive-runtime` / `aab` / `aab-debug`
  - **iOS 尚未接通**：`DefaultTarget` 返回空串 → `ForPlan` 返回空列表 → 命令行清空、Package 按钮禁用
- **平台差异（实测得出，不要在平台间共用同一套参数模板）**：
  - `-tsa`：桌面下发；Android 不接受（报 `-tsa option not supported`）。
  - `-platformsdk`：仅 aab 需要。
  - `-arch`：桌面 / apk 接受，aab 不接受。
  - **参数顺序相反**：桌面签名参数在 `-target` 之前；Android 签名参数必须在 `-target` **之后**。
  - aab 缺 Android SDK 时应在 UI 告警（`RunStageAsync` 里判断 `IsAndroidBundleTarget`）。
- 配置分两层，不要混：
  - 工程级 `dream.project.json`（`DreamProjectSettings`）：`build.platform` + 各平台
    `target` / 架构 / 证书 / 输出路径。
  - 机器级 `studio.settings.json`（`%LOCALAPPDATA%\Dream Studio\`，`StudioSettings`）：
    `AirSdkPath`、`AndroidSdkPath`。
- 开发证书 `DevCertificate`：`<项目根>\.dream\build\dev-cert.p12`，口令 `dreamdev`，
  **生成时未设 alias**，`TimestampUrl = "none"`。
- 描述符与图标：`AppDescriptorPath`（windows / android / iPhone 三套）、`DescriptorSchema.For`、
  `IconSizeCatalog.SizesFor`（air 优先、sdk 备选）；`AdtCapabilities` 后台跑 `adt -help`
  探测可用架构与目标。`WriteAppDescriptor` 只改写 `<filename>` 与 `<initialWindow><content>`。
- 已知缺口：aab 未在实机验证（需 Android SDK）；Android 权限需在描述符里加
  `<android><manifestAdditions>`；Android 图标需在构建面板勾选后才会收集。

## 发布构建的运行时启动链路

- 入口是各工程主类（`FlappyTest.as` / 模板 `DreamEngine.as`，两者结构同构），构造顺序：
  `super() → stage.color → initEcs() → CONFIG::STUDIO{} → if (!isStudioBuild()) 启动运行时`。
- `initEcs()` 的**系统注册顺序是固定的**：`PhysicsSystem2D → CanvasSystem → RenderSystem → InputSystem`
  （CanvasSystem 早于 RenderSystem，见"相机空间画布"一节）。
- **必须等 Stage3D 上下文就绪再加载资源**：Starling 的纹理解码、顶点/索引缓冲都要求
  `Starling.context` 已创建，否则抛 `MissingContextError`；而 `ResourceManager.loadAsync` 会把它
  catch 掉并静默回退（纹理变白块，无报错）。
  正确挂载点是 `RenderEngine.onReady`（`ROOT_CREATED` 之后触发）：
  `if (_renderEngine.isReady) initRuntime(); else _renderEngine.onReady = initRuntime;`
  - 症状特征：构造期创建的 `Image` 全白，而运行期 spawn 的 `SpriteRenderer` 正常
    （同一加载机制、仅时序不同）。
  - 这份 Starling 副本**没有** `Starling.handleLostContext`（只有 `AssetManager` 注释里提过），
    不要照上游 Starling 的印象写代码。
- 资源路径必须用 `File.applicationDirectory.resolvePath(rel).url`（`app:/…`），
  **不能用 `.nativePath`**：移动端包内文件不是真实文件系统条目，用 nativePath 会让场景加载失败
  （表现为只有舞台底色）。
- 运行时资源：`resource.manifest.json` 由 `BuildAssetCollector` 生成，引擎侧 `ResourceManager`
  用 `registerGuid` / `setAtlasEntries` 消费。顶层字段 `version` / `startupScene` / `resources` / `atlas`；
  `resources` 元素为 `{guid,path,sprite}`，`atlas` 元素为 `{spriteGuid,textureGuid,x,y,w,h}`。

## UI 画布体系

- Canvas 三种 `renderMode`：
  - `screenSpace`（默认）：层挂 `re.screenContainer`，不随相机，层缩放 `S(s)`。
  - `worldSpace`：层挂 `re.root`，随相机，层缩放 `1/referencePixelsPerUnit`，
    画布矩形取 RectTransform 或 CanvasScaler 参考分辨率。
  - `cameraSpace`：布局同 screenSpace（铺满视口），但层挂 `re.root` 参与世界排序；
    画布自身 Transform 不参与布局。
- **相机空间画布的层矩阵由 RenderSystem 延迟反算，不要在 CanvasSystem 里写**：
  CanvasSystem 早于 RenderSystem 执行，用上一帧相机视图写层矩阵会导致屏幕边缘露空。
  `fitLayerToViewport()` 在 `applyCameraView` 之后反算 `(相机矩阵 · S(s))⁻¹`，
  使层局部 `(0,0)~(stageW/s, stageH/s)` 恰好铺满整个视口。
- `CanvasScaler.computeScaleFactor`：`scaleWithScreenSize` + `matchWidthOrHeight` 在**对数空间**插值
  `pow(2, logW + (logH - logW) * m)`；`m=0` 跟宽、`m=1` 跟高。
  - UI 画布不配 CanvasScaler 时画布逻辑单位 = 物理像素，同一字号在不同 DPI 设备上观感差异很大
    （Android 端字号显著小于 Windows 端就是这个原因），需要按参考分辨率缩放。
- **Text 没有尺寸字段**：尺寸由**同元素上的 RectTransform** 经 CanvasSystem 每帧写入 Starling
  TextField（`wordWrap=true`，宽决定换行、高配合 verticalAlign）。

## 编辑器拾取（按渲染顺序）

- `RenderSystem.renderOrder` 是从底层到顶层的 `Element` 快照，与写入根容器的子级顺序同源；
  **它是上一帧的快照**（元素刚增删时会差一帧，已有 alive 保护）。
- `SceneSelection.hitAt` 判定顺序：先 `CanvasSystem.hitTestScreenElement`（屏幕空间画布最优先），
  再沿 `RenderSystem.renderOrder` **从上层往下**判定；命中元素若带 `Canvas` 组件 →
  `hitTestCanvasElement`，否则走普通 `hitTestElement`（含 `alphaHit`）。
- 新增一种画布模式时要同步四处：`CanvasSystem.update` 布局分支、`sortCanvases` 条目、
  命中入口（`hitTestScreenElement` / `hitTestCanvasElement`），以及
  `RenderEngine.setWorldCanvasLayers` 的层描述（cameraSpace 用 `{fill:true, scale}`）。

## 关键约定（2026-09 现状）

- **运行模式下允许编辑**：Inspector（查看/改值/增删/粘贴组件）、Hierarchy 命令、撤销/重做在运行期间均可用
  （Studio 与引擎两侧的运行门控已移除）。仅场景切换/加载保持运行期禁用（会用存档替换运行中的世界）。
- 新增引擎组件：继承 `DreamComponent` 放在 `Dream Engine\src` 任意位置即可，
  `ComponentIndex` 会在编译前自动扫描注册；要求无参构造且短类名非 `Transform`。
  需要 Inspector 显示字段就重写 `getInspectableFields()` / `setFieldValue()`，
  否则组件在 Inspector 中显示为空内容（属正常行为）。
- 旧版 Avalonia 拖拽 API（`IDataObject` / `FileNames` / `DragEventArgs.Data`）在 11.3 已过时但仍可用，
  通过 `#pragma warning disable CS0618` 抑制告警（ProjectPanel、InspectorPanel 均如此），
  迁移前需先确认新旧数据格式互通。
- Inspector 字段类型约定（Studio 渲染用）：`number` / `string` / `boolean` / `vector2` / `color` /
  `resource`（GUID 拖放）/ 带 `options` 的字符串下拉。
