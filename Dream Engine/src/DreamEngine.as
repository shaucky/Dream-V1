package
{
	CONFIG::STUDIO
	{
		import dream.engine.comm.ChannelEvent;
		import dream.engine.comm.ClipPreviewHandler;
		import dream.engine.comm.HierarchyBridge;
		import dream.engine.comm.InspectorAddComponentHandler;
		import dream.engine.comm.InspectorEditHandler;
		import dream.engine.comm.InspectorListComponentsHandler;
		import dream.engine.comm.InspectorPasteComponentHandler;
		import dream.engine.comm.InspectorRemoveComponentHandler;
		import dream.engine.comm.InspectorRequestHandler;
		import dream.engine.comm.InspectorSetEnabledHandler;
		import dream.engine.comm.EngineSetRunningHandler;
		import dream.engine.comm.AudioSettingsHandler;
		import dream.engine.comm.Message;
		import dream.engine.comm.MessageDispatcher;
		import dream.engine.comm.MessageTypes;
		import dream.engine.comm.PrefabExportHandler;
		import dream.engine.comm.PrefabRevertHandler;
		import dream.engine.comm.PrefabSourcesHandler;
		import dream.engine.comm.SceneLoadHandler;
		import dream.engine.comm.SceneSaveHandler;
		import dream.engine.comm.TcpChannel;
		import dream.engine.comm.handlers.HierarchyCommandHandler;
		import dream.engine.comm.handlers.PingHandler;
		import dream.engine.comm.handlers.ResourceIndexHandler;
		import dream.engine.comm.handlers.SceneRedoHandler;
		import dream.engine.comm.handlers.SceneUndoHandler;
		import dream.engine.comm.handlers.SceneSelectHandler;
		import dream.engine.comm.handlers.SpriteAtlasMapHandler;
		import dream.engine.comm.InspectorShared;
		import dream.engine.ecs.Element;
		import dream.engine.render.CameraComponent;
		import dream.engine.scene.DocumentOverlay;
		import dream.engine.scene.SceneSelection;
		import dream.engine.scene.TransformGizmo;
		import dream.engine.scene.AudioGizmo;
		import dream.engine.scene.UndoRedoManager;
		import dream.engine.transform.Transform;
		import dream.engine.ui.RectTransform;

		import flash.events.KeyboardEvent;
		import flash.ui.Keyboard;
	}

	// 发布构建（CONFIG::STUDIO=false）的启动路径同样需要：产物清单与启动场景都按文件读取。
	import dream.engine.scene.SceneSerializer;

	import flash.filesystem.File;
	import flash.filesystem.FileMode;
	import flash.filesystem.FileStream;

	import dream.engine.ecs.World;
	import dream.engine.ecs.systems.RenderSystem;
	import dream.engine.audio.AudioManager;
	import dream.engine.input.InputManager;
	import dream.engine.input.InputSystem;
	import dream.engine.physics2d.PhysicsSystem2D;
	import dream.engine.render.RenderEngine;
	import dream.engine.render.ResourceManager;
	import dream.engine.ui.CanvasSystem;

	import flash.desktop.NativeApplication;
	import flash.display.Sprite;
	import flash.display.StageAlign;
	import flash.display.StageScaleMode;
	import flash.events.Event;
	import flash.events.InvokeEvent;
	import flash.events.UncaughtErrorEvent;
	import flash.utils.getTimer;

	public class DreamEngine extends Sprite
	{
		CONFIG::STUDIO
		private var channel:TcpChannel;

		CONFIG::STUDIO
		private var dispatcher:MessageDispatcher = new MessageDispatcher();

		// ECS 世界：所有元素与系统的顶层管理器。独立运行时也存在（不依赖 Studio 通信）。
		private var _world:World = new World();
		// 渲染引擎：封装 Starling 初始化与渲染循环。内联适配层，对外只暴露引擎 API。
		private var _renderEngine:RenderEngine;
		private var _lastFrameMs:int;

		// 发布构建的场景反序列化器：按产物清单加载启动场景时惰性创建。
		private var _runtimeSerializer:SceneSerializer;

		CONFIG::STUDIO
		private var _hierarchy:HierarchyBridge;

		CONFIG::STUDIO
		private var _serializer:SceneSerializer;

		// 当前文档是否为预制体（按路径扩展名判定，见 onDocumentLoaded）。
		// 预制体是"场景子树资产"，不注入自动相机等场景级内容（否则会被存回 .prefab，污染所有实例）。
		CONFIG::STUDIO
		private var _prefabDocument:Boolean = false;

		// 左上角返回按钮：仅在预制体文档中显示（场景文档没有"上一个文档"可回）。
		CONFIG::STUDIO
		private var _docOverlay:DocumentOverlay;

		CONFIG::STUDIO
		private var _hierarchyCmd:HierarchyCommandHandler;

		CONFIG::STUDIO
		private var _undoRedo:UndoRedoManager;

		CONFIG::STUDIO
		private var _selection:SceneSelection;

		CONFIG::STUDIO
		private var _gizmo:TransformGizmo;

		CONFIG::STUDIO
		private var _audioGizmo:AudioGizmo;

		// Engine 端剪贴板（元素 ID 数组）：Ctrl+C 复制、Ctrl+V 粘贴。
		// 与 Studio Hierarchy 面板的剪贴板各自独立——焦点在 Engine 窗口时由这里兜底。
		CONFIG::STUDIO
		private var _clipboardIds:Array = [];

		public function DreamEngine()
		{
			super();

			stage.color = 0x16417C;

			// 先初始化渲染引擎与 ECS，使 _renderEngine 在 STUDIO handler 注册时可用
			// （HierarchyCommandHandler 创建元素时需注入 _renderEngine 生成默认方块）。
			initEcs();

			CONFIG::STUDIO
			{
				trace("AIR runtime initalized - runtime version: " + NativeApplication.nativeApplication.runtimeVersion);

				// Studio 接管即编辑器模式：暂停组件运行时生命周期，
			// 仅保留系统更新（RenderSystem 同步变换）与销毁清理。
			// 后续 Play 按钮可通过消息设置 _world.running = true 切到运行模式。
			_world.running = false;

			// 启用编辑器视口导航：滚轮缩放、中键/空格+左键平移。
			// 导航操作 RenderEngine 内部的编辑器相机（查看用），不移动场景对象。
			_renderEngine.viewportNavigationEnabled = true;

			// 注册消息处理器（OCP：新增消息只加处理器）。
			_serializer = new SceneSerializer(_world, _renderEngine);
			_undoRedo = new UndoRedoManager(_serializer);

			dispatcher.add(new PingHandler());
			dispatcher.add(new ResourceIndexHandler());
			// 图集覆盖映射：Studio 打包后推送，精灵解析时优先命中。
			dispatcher.add(new SpriteAtlasMapHandler());
			// 视口拾取选中：注册 scene.select 处理器，并绑定左键点击回调。
			_selection = new SceneSelection(_world, _renderEngine, stage);
			_selection.onSelectionChange = pushSelection;
			dispatcher.add(new SceneSelectHandler(_selection));
			_renderEngine.onClick = onScenePick;

			// Hierarchy 命令（创建/销毁/重命名/改层级/复制）需 serializer 与 selection 支持。
			_hierarchyCmd = new HierarchyCommandHandler(_world, _renderEngine, _undoRedo, _serializer, _selection);
			dispatcher.add(_hierarchyCmd);
			dispatcher.add(new InspectorRequestHandler(_world));
			dispatcher.add(new InspectorEditHandler(_world, _undoRedo, _serializer));
			dispatcher.add(new ClipPreviewHandler(_world));
			dispatcher.add(new AudioSettingsHandler());
			dispatcher.add(new InspectorListComponentsHandler());
			dispatcher.add(new InspectorAddComponentHandler(_world, _undoRedo, _serializer));
			dispatcher.add(new InspectorRemoveComponentHandler(_world, _undoRedo, _serializer));
			dispatcher.add(new InspectorPasteComponentHandler(_world, _renderEngine, _undoRedo));
			dispatcher.add(new InspectorSetEnabledHandler(_world, _serializer));
			dispatcher.add(new SceneSaveHandler(_serializer));
			dispatcher.add(new SceneLoadHandler(_serializer, _undoRedo, onDocumentLoaded));
			dispatcher.add(new PrefabExportHandler(_serializer));
			dispatcher.add(new PrefabSourcesHandler(_serializer, _world, _selection));
			dispatcher.add(new PrefabRevertHandler(_serializer, _world, _selection));
			dispatcher.add(new SceneUndoHandler(_undoRedo, _world, _selection));
			dispatcher.add(new SceneRedoHandler(_undoRedo, _world, _selection));

			// 变换 Gizmo：选中元素的移动/旋转/缩放手柄（W/E/R 切换模式）。
			_gizmo = new TransformGizmo(_world, _renderEngine, _selection, _undoRedo,
				onGizmoEditEnd, onGizmoEditUpdate, stage);

			// 音频范围预览：选中元素含 AudioSource/AudioListener 时显示配置范围。
			_audioGizmo = new AudioGizmo(_world, _renderEngine, _selection);

			// 文档返回按钮：预制体文档左上角的返回入口（点击 → document.back 给 Studio）。
			_docOverlay = new DocumentOverlay(_renderEngine, onDocumentBack);

			// 运行控制：切换 running 时联动编辑器 overlay 显示与视口导航状态。
			dispatcher.add(new EngineSetRunningHandler(_world, _selection, _gizmo, _renderEngine, _audioGizmo));

			// Hierarchy 桥接器：元素变化时发送快照到 Studio。
				_hierarchy = new HierarchyBridge(_world);

				// 捕获未处理异常与安全错误，通过通信上报到 Studio Console。
				loaderInfo.uncaughtErrorEvents.addEventListener(UncaughtErrorEvent.UNCAUGHT_ERROR, onUncaughtError);

				// 通过 INVOKE 事件读取启动参数（含 --comm-port）。
				NativeApplication.nativeApplication.addEventListener(InvokeEvent.INVOKE, onInvoke);
			}

			// 发布构建（CONFIG::STUDIO=false）：无通信、无编辑器，启动即按产物清单进入运行态。
			if (!isStudioBuild())
			{
				// 等渲染根就绪再加载：纹理解码与顶点缓冲都要求 Stage3D 上下文已创建
				// （见 RenderEngine.onReady），在构造期直接加载会让所有资源静默回退成白块。
				if (_renderEngine.isReady) initRuntime();
				else _renderEngine.onReady = initRuntime;
			}
		}

		/**
		 * CONFIG::STUDIO 的运行期镜像：编译期常量在编辑器/发布两种构建下分别为 true / false。
		 * 用于分支而不引入第二个编译常量（AS3 条件编译块不支持 else 分支）。
		 */
		private static function isStudioBuild():Boolean
		{
			var studio:Boolean = false;
			CONFIG::STUDIO { studio = true; }
			return studio;
		}

		/** 产物清单文件名：由 Dream Studio 构建时生成，位于应用根目录（app:/）。 */
		private static const ManifestFileName:String = "resource.manifest.json";

		/**
		 * 发布构建启动：读取产物清单 → 注册资源 GUID 与图集覆盖映射 → 加载启动场景 → 运行。
		 *
		 * 清单只包含启动场景实际引用到的资源（构建时收集），其中路径为相对应用根的相对路径，
		 * 这里解析为绝对原生路径——ResourceManager 的既有解析全部按绝对路径工作
		 * （编辑器模式由 Studio 推送绝对路径），两条链路因此共用同一套加载实现。
		 *
		 * 任何一步失败都只 trace 不抛出：产物损坏时窗口仍应正常显示，便于定位问题。
		 */
		private function initRuntime():void
		{
			var manifest:File = File.applicationDirectory.resolvePath(ManifestFileName);
			if (!manifest.exists)
			{
				trace("[boot] 缺少产物清单：" + manifest.nativePath);
				return;
			}

			var data:Object = null;
			try { data = JSON.parse(readTextFile(manifest)); }
			catch (e:Error) { trace("[boot] 产物清单解析失败：" + e); return; }
			if (data == null) return;

			var rm:ResourceManager = ResourceManager.current;
			var resources:Array = data.resources as Array;
			if (rm != null && resources != null)
			{
				for each (var r:Object in resources)
				{
					if (r == null || r.guid == null || r.path == null) continue;
					rm.registerGuid(String(r.guid), appPath(String(r.path)),
						r.sprite != null ? String(r.sprite) : null);
				}
				// 图集覆盖：精灵 GUID → 图集纹理 GUID + 图集内矩形；未覆盖的精灵回落源纹理。
				rm.setAtlasEntries(data.atlas as Array);
			}

			var sceneRel:String = data.startupScene != null ? String(data.startupScene) : "";
			if (sceneRel.length > 0) loadRuntimeScene(appPath(sceneRel));
		}

		/** 反序列化启动场景并进入运行态。失败仅 trace，不阻断启动。 */
		private function loadRuntimeScene(scenePath:String):void
		{
			try
			{
				var file:File = new File(scenePath);
				if (!file.exists)
				{
					trace("[boot] 启动场景不存在：" + scenePath);
					return;
				}
				if (_runtimeSerializer == null)
					_runtimeSerializer = new SceneSerializer(_world, _renderEngine);
				if (_runtimeSerializer.deserialize(JSON.parse(readTextFile(file))))
				{
					_world.running = true;
					trace("[boot] 启动场景已加载：" + scenePath);
				}
			}
			catch (e:Error) { trace("[boot] 启动场景加载失败：" + e); }
		}

		/**
		 * 相对应用根的路径 → 可用的绝对路径。
		 *
		 * 取 url（app:/…）而不是 nativePath：包内文件在移动端（Android / iOS）不是真实文件
		 * 系统条目，用 nativePath 拼出来的字符串再交给 new File 未必能定位到包内资源；
		 * app:/ 形式的 File 由 AIR 自身解析，桌面与移动端一致可用。
		 */
		private static function appPath(relative:String):String
		{
			return File.applicationDirectory.resolvePath(relative).url;
		}

		/** 读取文本文件全部内容（UTF-8）。 */
		private static function readTextFile(file:File):String
		{
			var fs:FileStream = new FileStream();
			fs.open(file, FileMode.READ);
			var text:String = fs.readUTFBytes(fs.bytesAvailable);
			fs.close();
			return text;
		}

		/**
		 * 初始化渲染引擎与 ECS：注册系统，挂 ENTER_FRAME 驱动 world.update(dt)。
		 *
		 * RenderEngine 构造异步等待 Starling ROOT_CREATED；RenderSystem 内部
		 * 在 root==null 时跳过同步，就绪后自动接管显示对象挂载与变换同步。
		 */
		private function initEcs():void
		{
			_renderEngine = new RenderEngine(stage);
			new ResourceManager(); // 设置 ResourceManager.current
			new InputManager(stage); // 设置 InputManager.current（统一输入查询）
			new AudioManager();      // 设置 AudioManager.current（音频播放）
			_renderEngine.start();

			// 系统注册顺序即每帧执行顺序（后注册的能读到前面系统的结果）。
			// 物理系统：固定步进 + 变换双向同步 + 碰撞事件分发，位于渲染之前。
			_world.addSystem(new PhysicsSystem2D());
			// 画布系统：UI 布局/渲染/指针分发。**须早于 RenderSystem** —— 世界空间画布的
			// 层容器每帧登记到 RenderEngine，RenderSystem 排序渲染根子级时要读本帧的层列表。
			_world.addSystem(new CanvasSystem());
			_world.addSystem(new RenderSystem(_renderEngine));
			// 输入系统最后注册：帧状态（pressed/released/wheelDelta）是「上一帧清理后累积到
			// 本帧」的边沿量，必须等 CanvasSystem 等消费者读取完再重置。
			_world.addSystem(new InputSystem());

			_lastFrameMs = getTimer();
			addEventListener(Event.ENTER_FRAME, onEnterFrame);
		}

		private function onEnterFrame(e:Event):void
		{
			var now:int = getTimer();
			var dt:Number = (now - _lastFrameMs) / 1000.0;
			_lastFrameMs = now;
			_world.update(dt);
			// 音频帧驱动：淡入淡出等时间相关逻辑（编辑器与运行模式都需要）。
			var audio:AudioManager = AudioManager.current;
			if (audio != null) audio.update(dt);
			CONFIG::STUDIO
			{
				// 层级快照属于数据同步（非编辑器 overlay），两种模式都必须发送：
				// Studio 允许运行中编辑场景（Hierarchy 增删/重排），若运行模式下不发快照，
				// 引擎端已生效而 Studio Hierarchy 面板收不到新顺序，会一直保持原样。
				if (_hierarchy != null) _hierarchy.update(dt);

				// 编辑器 overlay（选中高亮 / Gizmo 手柄）仅在编辑模式驱动；
				// 运行模式下组件生命周期接管，编辑器 overlay 隐藏且不参与渲染树重排。
				if (!_world.running)
				{
					if (_selection != null) _selection.update();
					if (_gizmo != null) _gizmo.update();
					if (_audioGizmo != null) _audioGizmo.update();
					if (_docOverlay != null) _docOverlay.update();
				}
			}
		}

		CONFIG::STUDIO
		private function onInvoke(event:InvokeEvent):void
		{
			var args:Array = event.arguments;
			var port:int = parsePort(args);

			// 场景存档自动加载：--scene=<path> 指定文档路径（.space 场景或 .prefab 预制体，格式同构），
		// 存在则反序列化重建场景。必须在 connect 前完成，使 onConnected 发送的初始
		// Hierarchy/Inspector 快照反映加载后的场景而非空场景。
		// 文档类型（预制体与否）由路径扩展名判定，见 onDocumentLoaded。
		var scenePath:String = parseArg(args, "--scene=");
		if (scenePath != null && scenePath.length > 0)
			loadSceneFile(scenePath);

		// 空场景保证有且仅有一个不可移除的根元素。
		// 场景加载后调用：若存档已含元素则取第一个为根，否则创建新根。
		ensureRoot();

		// 初始加载或新建场景后清空 undo/redo 历史，避免把 ensureRoot 等初始化步骤当作可撤销操作。
		if (_undoRedo != null) _undoRedo.clear();

		// 运行模式：--mode=run 启动即运行（world.running=true）；
		// 缺省或 --mode=edit 为编辑器模式（running=false，仅系统更新）。
		var mode:String = parseArg(args, "--mode=");
		_world.running = (mode == "run");

		// 视口导航仅在编辑器模式启用，运行模式关闭。
		_renderEngine.viewportNavigationEnabled = !_world.running;

		// Engine 窗口快捷键：焦点在嵌入的 ADL 窗口时 Studio 收不到键盘事件，
		// 需在此兜底实现复制/粘贴/撤销等（与 Studio 菜单/面板快捷键一一对应）。
		initEditorShortcuts();

		if (port > 0) connect(port);
	}

	/** 注册 Engine 窗口快捷键（Ctrl+C/V/D/Z/Y）。由 onInvoke 调用。 */
		CONFIG::STUDIO
		private function initEditorShortcuts():void
		{
			stage.addEventListener(KeyboardEvent.KEY_DOWN, onEditorKeyDown);
		}

		/**
		 * Engine 窗口快捷键分发。与 Studio 端快捷键一一对应：
		 *   Ctrl+C 复制选中、Ctrl+V 粘贴到选中项下、Ctrl+D 复制副本、
		 *   Ctrl+Z 撤销、Ctrl+Y 重做、Ctrl+S 保存、Ctrl+Shift+S 另存为。
		 * 保存/另存为依赖 Studio 文件对话框，这里发 engine.shortcut 由 Studio 执行。
		 * 注意：若在此新增/修改快捷键，须同步 Studio 端（HierarchyPanel.OnPanelKeyDown、
		 * MainWindow Edit/File 菜单），反之亦然。
		 */
		CONFIG::STUDIO
		private function onEditorKeyDown(e:KeyboardEvent):void
		{
			if (!e.ctrlKey) return;
			switch (e.keyCode)
			{
				case Keyboard.C:
					copySelection();
					break;
				case Keyboard.V:
					pasteClipboard();
					break;
				case Keyboard.D:
					duplicatePrimary();
					break;
				case Keyboard.Z:
					if (_undoRedo != null) _undoRedo.undo();
					break;
				case Keyboard.Y:
					if (_undoRedo != null) _undoRedo.redo();
					break;
				case Keyboard.S:
					// 保存/另存为：对话框依赖 Studio 端，发请求由 Studio 执行保存流程。
					if (channel != null && channel.connected)
					{
						channel.send(new Message(MessageTypes.EngineShortcut,
							{action: e.shiftKey ? "saveAs" : "save"}));
					}
					break;
			}
		}

		/** Ctrl+C：复制当前选中元素到引擎剪贴板（根元素不可复制，过滤掉）。 */
		CONFIG::STUDIO
		private function copySelection():void
		{
			if (_selection == null) return;
			_clipboardIds = [];
			for each (var id:int in _selection.selectedIds)
			{
				if (id != _world.rootElementId) _clipboardIds.push(id);
			}
		}

		/** Ctrl+V：将剪贴板元素粘贴为当前选中 primary 的子级；未选中则粘贴到根元素下。 */
		CONFIG::STUDIO
		private function pasteClipboard():void
		{
			if (_clipboardIds.length == 0) return;
			var targetId:int = -1;
			if (_selection != null && _selection.contains(_selection.selectedElementId))
				targetId = _selection.selectedElementId;
			if (targetId < 0) targetId = _world.rootElementId;
			// 一次记录：整批粘贴作为单步撤销。
			if (_undoRedo != null) _undoRedo.record();
			var last:* = null;
			for each (var cid:int in _clipboardIds)
				last = _hierarchyCmd.duplicateElement(cid, targetId, 0, 0);
			if (last != null && _selection != null)
				_selection.setSelection([last.id], last.id);
		}

		/** Ctrl+D：复制当前主选中元素为同父副本（位置偏移，缺省 +20/+20）。 */
		CONFIG::STUDIO
		private function duplicatePrimary():void
		{
			if (_selection == null) return;
			var primary:int = _selection.selectedElementId;
			if (primary < 0 || !_selection.contains(primary)) return;
			if (primary == _world.rootElementId) return; // 根元素不可复制
			if (_undoRedo != null) _undoRedo.record();
			_hierarchyCmd.duplicateElement(primary);
		}

		/**
		 * 确保场景有且仅有一个根元素，并记录其 ID 到 _world.rootElementId。
	 * - 已有元素：取第一个存活的为根。
	 * - 无元素：创建一个名为 "Root" 的根元素（Transform + 默认显示）。
	 * 根元素不可被销毁（HierarchyCommandHandler.handleDestroy 会保护）。
	 */
	CONFIG::STUDIO
	private function ensureRoot():void
	{
		var first:Element = null;
		for each (var e:Element in _world.elements)
		{
			if (e.alive) { first = e; break; }
		}
		if (first != null)
		{
			_world.rootElementId = first.id;
			// 预制体文档不加相机：它只描述一棵子树，相机属于场景（见 _prefabDocument）。
			if (!_prefabDocument) ensureCameraOn(first);
			return;
		}
		// 空场景：创建根元素。复用已注册的 HierarchyCommandHandler 的创建逻辑（仅 Transform）。
		_hierarchyCmd.executeCreate("Root", -1);
		// 取刚创建的根元素 ID
		for each (var e2:Element in _world.elements)
		{
			if (e2.alive) { _world.rootElementId = e2.id; ensureCameraOn(e2); break; }
		}
	}

	/**
	 * 场景必须有主相机（运行语境）：无则创建 "Camera" 元素作为根的子节点。
	 * 位置 (0,0) 即世界原点（根自身不动），运行模式视图居中。
	 * 编辑器模式视图由 RenderEngine 的编辑器导航相机驱动，与场景相机区分开。
	 */
	CONFIG::STUDIO
	private function ensureCameraOn(rootEl:Element):void
	{
		if (findCameraElement() != null) return;
		var rootT:Transform = rootEl.getComponent(Transform) as Transform;
		if (rootT == null) return;
		var camEl:Element = _world.createElement();
		camEl.name = "Camera";
		camEl.addComponent(new Transform());
		var camT:Transform = camEl.getComponent(Transform) as Transform;
		camT.setParent(rootT);
		camEl.addComponent(new CameraComponent());
		_world.markHierarchyDirty();
	}

	/** 返回主相机元素（第一个 enabled 的 CameraComponent）；无则 null。 */
	CONFIG::STUDIO
	private function findCameraElement():Element
	{
		var cams:Vector.<Element> = _world.query(Transform, CameraComponent);
		for each (var e:Element in cams)
		{
			var camC:CameraComponent = e.getComponent(CameraComponent) as CameraComponent;
			if (camC != null && camC.enabled) return e;
		}
		return null;
	}

		/** 读取 --scene 指定的文档（.space 场景或 .prefab 预制体，两者格式同构）
		 *  并反序列化到当前 World。失败仅 trace，不阻断启动。 */
		CONFIG::STUDIO
		private function loadSceneFile(path:String):void
		{
			try
			{
				var file:File = new File(path);
				if (!file.exists) return;
				var fs:FileStream = new FileStream();
				fs.open(file, FileMode.READ);
				var json:String = fs.readUTFBytes(fs.bytesAvailable);
				fs.close();
				var data:Object = JSON.parse(json);
				if (_serializer.deserialize(data)) onDocumentLoaded(path);
			}
			catch (e:Error)
			{
				trace("[scene] 存档加载失败：" + e);
			}
		}

		/**
		 * 文档加载完成（启动 --scene，或 Studio 的 scene.load）：按扩展名判定文档类型。
		 *
		 * 这里是"当前文档是不是预制体"的唯一判定点——扩展名即 Studio 的分类依据
		 * （见 EngineSession.IsEditingPrefab），双端规则一致才不会各自跑偏。
		 * 预制体文档才显示左上角返回按钮：场景文档没有"上一个文档"可回。
		 */
		CONFIG::STUDIO
		private function onDocumentLoaded(path:String):void
		{
			_prefabDocument = isPrefabPath(path);
			if (_docOverlay != null) _docOverlay.setVisible(_prefabDocument);
		}

		/** 路径是否为预制体文档（按扩展名，大小写不敏感）。 */
		CONFIG::STUDIO
		private static function isPrefabPath(path:String):Boolean
		{
			if (path == null) return false;
			return path.length > 7 && path.substr(path.length - 7).toLowerCase() == ".prefab";
		}

		/**
		 * 返回按钮点击 → 请求 Studio 切回上一个文档。
		 * 引擎只负责发请求：文档栈与切换流程都在 Studio 侧（见 MainWindow），
		 * 引擎同一时刻只挂载一个文档，无从自行回退。
		 */
		CONFIG::STUDIO
		private function onDocumentBack():void
		{
			if (channel != null && channel.connected)
				channel.send(new Message(MessageTypes.DocumentBack));
		}

		/** 解析 --key=value 形式的启动参数，返回 value；容忍 ADL 透传的 "--" 分隔符。 */
		CONFIG::STUDIO
		private static function parseArg(args:Array, prefix:String):String
		{
			if (!args) return null;
			for (var i:int = 0; i < args.length; i++)
			{
				var a:String = String(args[i]);
				if (a == "--") continue;
				if (a.indexOf(prefix) == 0)
					return a.substring(prefix.length);
			}
			return null;
		}

		/** 解析 --comm-port=NNNN；容忍 ADL 可能透传的 "--" 分隔符。 */
		CONFIG::STUDIO
		private static function parsePort(args:Array):int
		{
			if (!args) return 0;
			var prefix:String = "--comm-port=";
			for (var i:int = 0; i < args.length; i++)
			{
				var a:String = String(args[i]);
				if (a == "--") continue;
				if (a.indexOf(prefix) == 0)
					return parseInt(a.substring(prefix.length));
			}
			return 0;
		}

		CONFIG::STUDIO
		private function connect(port:int):void
		{
			channel = new TcpChannel();
			channel.addEventListener(ChannelEvent.MESSAGE, onMessage);
			channel.addEventListener(ChannelEvent.CLOSED, onClosed);
			channel.addEventListener(Event.CONNECT, onConnected);
			channel.connect("127.0.0.1", port);
		}

		CONFIG::STUDIO
		private function onConnected(event:Event):void
		{
			// 上报就绪与问候，验证 Engine → Studio 方向。
			channel.send(new Message(MessageTypes.Ready));
			channel.send(new Message(MessageTypes.Log, "Channel connected"));
			// 绑定通道到 Hierarchy 桥接器，并发送一次初始快照。
			if (_hierarchy != null)
			{
				_hierarchy.attach(channel);
				_hierarchy.sendInitial();
			}
		}

		CONFIG::STUDIO
		private function onMessage(event:ChannelEvent):void
		{
			// 分发到处理器；PingHandler 会回传 pong，验证 Studio → Engine → Studio 往返。
			dispatcher.dispatch(event.message, channel);
		}

		CONFIG::STUDIO
		private function onClosed(event:ChannelEvent):void
		{
			// 通道关闭：开发阶段仅占位，不做重连。
		}

		/**
		 * 视口左键点击 → 返回按钮优先；其次 Gizmo 手柄；最后拾取元素：
		 *   - 命中元素：Shift/Ctrl = 增删选择（toggle），否则单选替换；
		 *   - 空白处：开始框选（拖拽矩形选多个，点击空白清空）。
		 * 选择集变化经 pushSelection 回传 Studio。
		 */
		CONFIG::STUDIO
		private function onScenePick(stageX:Number, stageY:Number, shiftKey:Boolean, ctrlKey:Boolean):void
		{
			// 返回按钮浮在最上层：先于 Gizmo 与场景拾取消费点击，避免点按钮时顺带选中元素。
			if (_docOverlay != null && _docOverlay.onMouseDown(stageX, stageY)) return;
			if (_selection == null) return;
			// Gizmo 手柄命中则进入拖拽（消费本次点击，不再拾取）。
			if (_gizmo != null && _gizmo.onMouseDown(stageX, stageY)) return;

			var id:int = _selection.hitAt(stageX, stageY);
			if (id >= 0)
			{
				if (shiftKey || ctrlKey) _selection.toggle(id);
				else _selection.select(id);
				// select/toggle 已触发 onSelectionChange → pushSelection。
			}
			else
			{
				// 空白：开始框选（结束时空框清空选择）。
				_selection.beginMarquee(stageX, stageY);
			}
		}

		/** 选择集变化 → 回传 Studio（scene.picked，含全部 ID 与 primary）。 */
		CONFIG::STUDIO
		private function pushSelection():void
		{
			if (channel != null && channel.connected && _selection != null)
			{
				channel.send(new Message(MessageTypes.ScenePicked, {
					elementIds: _selection.selectedIds,
					primaryId: _selection.selectedElementId
				}));
			}
		}

		/** Gizmo 拖拽结束（有实际变更）：通知 Studio 场景已修改。 */
		CONFIG::STUDIO
		private function onGizmoEditEnd():void
		{
			// prefab 实例：把本次拖拽改动的 Transform 字段记为 override，源 prefab 改动传播时不覆盖它。
			// UI 元素（RectTransform）走画布布局、没有对应字段，跳过。
			if (_serializer != null && _selection != null && _gizmo != null)
			{
				var key:String = _gizmo.transformOverrideKey;
				for each (var id:int in _selection.selectedIds)
				{
					var el:Element = InspectorShared.findElement(_world, id);
					if (el == null) continue;
					if (el.getComponent(RectTransform) != null) continue;
					_serializer.recordOverride(id, key);
				}
			}
			if (channel != null && channel.connected)
				channel.send(new Message(MessageTypes.SceneModified));
		}

		/** Gizmo 拖拽过程中（约 100ms 节流）直接推送 Inspector 快照，实时刷新 Studio。 */
		CONFIG::STUDIO
		private function onGizmoEditUpdate():void
		{
			if (channel != null && channel.connected && _selection != null)
				InspectorShared.sendSnapshot(_world, _selection.selectedElementId, channel);
		}

		/** 未捕获异常上报到 Studio Console；通道未就绪时仅吞掉，避免循环报错。 */
		CONFIG::STUDIO
		private function onUncaughtError(event:UncaughtErrorEvent):void
		{
			event.preventDefault(); // 阻止默认的弹窗/退出
			if (channel != null && channel.connected)
			{
				channel.send(new Message(MessageTypes.Log, "Uncaught: " + event.error));
			}
		}
	}
}
