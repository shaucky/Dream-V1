package dream.engine.scene
{
	CONFIG::STUDIO
	{
		import dream.engine.ecs.Element;
		import dream.engine.ecs.World;
		import dream.engine.render.Drawable;
		import dream.engine.render.DrawableContainer;
		import dream.engine.render.RenderEngine;
		import dream.engine.transform.Transform;
		import dream.engine.ui.CanvasSystem;
		import dream.engine.ui.RectTransform;

		import flash.display.Stage;
		import flash.events.KeyboardEvent;
		import flash.events.MouseEvent;
		import flash.geom.Matrix;
		import flash.geom.Point;
		import flash.ui.Keyboard;
		import flash.utils.getTimer;

		/**
		 * 变换 Gizmo：选中元素的移动/旋转/缩放手柄。
		 *
		 * 模式（W/E/R 切换，默认移动）：
		 *   W 移动：X 红轴条 + Y 绿轴条（拖轴单轴移动）+ 中心方块（自由 2D 移动）
		 *   E 旋转：圆环手柄（拖环旋转，delta 角度叠加到本地 rotation）
		 *   R 缩放：X/Y 方块（单轴缩放）+ 中心方块（等比缩放）
		 *
		 * 交互：
		 *   - 手柄绘制在渲染根顶层的覆盖层上（选中高亮之上），全部 touchable=true，
		 *     因此视口点击会优先命中手柄（DisplayObjectContainer.hitTest 按渲染序）。
		 *   - MOUSE_DOWN 由 DreamEngine.onScenePick 调用本类的 onMouseDown 先行消费：
		 *     命中手柄返回 true（进入拖拽，不再拾取）；否则返回 false 交给拾取。
		 *   - MOUSE_MOVE/MOUSE_UP 由本类自行监听 stage。
		 *   - 拖拽开始前调用 UndoRedoManager.record()（快照式撤销：撤销即回到拖拽前）。
		 *   - 拖拽结束（有实际变更）触发 onEditEnd 回调，由 DreamEngine 上报 Studio 置脏。
		 *
		 * 坐标说明：Gizmo 画在世界空间（与元素渲染一致），尺寸按相机缩放反算保持
		 * 屏幕像素恒定。位移先把屏幕 delta 转世界 delta，再经父级矩阵反变换为局部
		 * delta，使带父级元素在屏幕上移动精确；旋转 delta 为世界角增量；缩放因子
		 * 直接作用于本地 scale。
		 *
		 * UI 元素（带 RectTransform）：其矩形由锚点 + offsetMin/offsetMax 决定、画在
		 * 屏幕空间画布层上，Transform.x/y 不参与布局 —— 故：
		 *   - 手柄改挂「屏幕容器」（UI 画布层之上），尺寸直接用屏幕像素，手柄命中用
		 *     RenderEngine.hitTestScreen；
		 *   - 仅支持移动（当前 RectTransform 无 rotation/pivot，旋转/缩放无意义）；
		 *   - 拖拽把屏幕 delta ÷ 画布缩放比换算为画布逻辑单位，等量加到 offsetMin 与
		 *     offsetMax（矩形平移、尺寸不变）。
		 */
		public final class TransformGizmo
		{
			private static const MODE_MOVE:int = 0;
			private static const MODE_ROTATE:int = 1;
			private static const MODE_SCALE:int = 2;

			// 外观（屏幕像素，按相机缩放反算为世界单位）。
			private static const GizmoScreenSize:Number = 100;  // 手柄长度/环半径
			private static const HandleThickness:Number = 4;    // 轴条/手柄厚度
			private static const CenterScreenSize:Number = 12;  // 中心手柄边长
			private static const RingSegments:int = 24;         // 旋转环段数
			private static const MinScale:Number = 0.01;

			private static const ColorX:uint = 0xE5484D; // 红 X
			private static const ColorY:uint = 0x3FB950; // 绿 Y
			private static const ColorCenter:uint = 0xE6E6E6; // 白（自由/中心）
			private static const ColorRing:uint = 0x8AB4F8;   // 蓝（旋转环）

			private var _world:World;
			private var _render:RenderEngine;
			private var _selection:SceneSelection;
			private var _undoRedo:UndoRedoManager;
			private var _onEditEnd:Function;
			private var _onEditUpdate:Function;

			private var _overlay:DrawableContainer;
            private var _overlayAttached:Boolean = false;
            // 当前覆盖层父级（世界元素 = 渲染根；UI 元素 = 屏幕容器）；切换目标时改挂。
            private var _overlayParent:DrawableContainer = null;
            // 编辑器可见性：运行模式隐藏（EngineSetRunningHandler 联动），update 短路。
            private var _visible:Boolean = true;

			// 三种模式的手柄层与手柄列表（{d:Drawable, kind:String}）。
			private var _moveLayer:DrawableContainer;
			private var _rotateLayer:DrawableContainer;
			private var _scaleLayer:DrawableContainer;
			private var _moveHandles:Array = [];
			private var _rotateHandles:Array = [];
			private var _scaleHandles:Array = [];

			private var _mode:int = MODE_MOVE;

			// 拖拽状态。
			private var _dragging:Boolean = false;
			private var _dragKind:String = null;
			private var _dragChanged:Boolean = false;
			private var _startWorldMouse:Point = new Point();
			private var _startX:Number = 0;
			private var _startY:Number = 0;
			private var _startRot:Number = 0;
			private var _startSX:Number = 1;
			private var _startSY:Number = 1;
			private var _startAngle:Number = 0;
			private var _startDist:Number = 1;
			private var _handleLenWorld:Number = 1;

			// 批量变换：拖拽开始记录全部选中元素的起点状态（以 primary 为操作基准）。
			private var _startStates:Array = [];

			// UI 元素拖拽（RectTransform）：各选中元素的手柄所在画布层 + 起点（offsetMin/Max）。
			private var _uiDrag:Boolean = false;
			private var _uiStartStates:Array = [];

			// 拖拽中推送 Inspector 快照的节流间隔（毫秒）：不依赖 Studio 轮询定时器。
			private static const PushIntervalMs:int = 100;
			private var _lastPushMs:int = 0;

			public function TransformGizmo(world:World, render:RenderEngine, selection:SceneSelection,
										   undoRedo:UndoRedoManager, onEditEnd:Function,
										   onEditUpdate:Function, stage:Stage)
			{
				_world = world;
				_render = render;
				_selection = selection;
				_undoRedo = undoRedo;
				_onEditEnd = onEditEnd;
				_onEditUpdate = onEditUpdate;

				_overlay = render.createContainer();
				_moveLayer = buildMoveLayer();
				_rotateLayer = buildRotateLayer();
				_scaleLayer = buildScaleLayer();
				_overlay.addChild(_moveLayer);
				_overlay.addChild(_rotateLayer);
				_overlay.addChild(_scaleLayer);
				_overlay.visible = false;

				stage.addEventListener(MouseEvent.MOUSE_MOVE, onMouseMove);
				stage.addEventListener(MouseEvent.MOUSE_UP, onMouseUp);
				stage.addEventListener(KeyboardEvent.KEY_DOWN, onKeyDown);
			}

			// ── 手柄构建 ──

			private function buildMoveLayer():DrawableContainer
			{
				var layer:DrawableContainer = _render.createContainer();
				addHandle(layer, _moveHandles, _render.createQuad(1, 1, ColorX), "x");     // 0 轴条
				addHandle(layer, _moveHandles, _render.createQuad(1, 1, ColorX), "x");     // 1 箭头尖
				addHandle(layer, _moveHandles, _render.createQuad(1, 1, ColorY), "y");     // 2 轴条
				addHandle(layer, _moveHandles, _render.createQuad(1, 1, ColorY), "y");     // 3 箭头尖
				addHandle(layer, _moveHandles, _render.createQuad(1, 1, ColorCenter), "free"); // 4 中心
				return layer;
			}

			private function buildRotateLayer():DrawableContainer
			{
				var layer:DrawableContainer = _render.createContainer();
				for (var i:int = 0; i < RingSegments; i++)
					addHandle(layer, _rotateHandles, _render.createQuad(1, 1, ColorRing), "rotate");
				addHandle(layer, _rotateHandles, _render.createQuad(1, 1, ColorRing), "rotate"); // 起点指示
				return layer;
			}

			private function buildScaleLayer():DrawableContainer
			{
				var layer:DrawableContainer = _render.createContainer();
				addHandle(layer, _scaleHandles, _render.createQuad(1, 1, ColorX), "scaleX");     // 0
				addHandle(layer, _scaleHandles, _render.createQuad(1, 1, ColorY), "scaleY");     // 1
				addHandle(layer, _scaleHandles, _render.createQuad(1, 1, ColorCenter), "uniform"); // 2
				return layer;
			}

			private static function addHandle(layer:DrawableContainer, list:Array, d:Drawable, kind:String):void
			{
				layer.addChild(d);
				list.push({d: d, kind: kind});
			}

			// ── 每帧更新 ──

			/**
			 * 运行模式隐藏 Gizmo：直接隐藏覆盖层并短路 update，
			 * 使其不再参与每帧渲染树重排（removeChild/addChild）。
			 * 由 EngineSetRunningHandler 在 running 切换时联动调用。
			 */
			public function setVisible(v:Boolean):void
			{
				_visible = v;
				if (v) return;
				_overlay.visible = false;
				// 中止进行中的拖拽，避免运行模式下继续写回 Transform / RectTransform。
				if (_dragging)
				{
					_dragging = false;
					_dragKind = null;
					_uiDrag = false;
					if (_dragChanged && _onEditEnd != null) _onEditEnd();
					_dragChanged = false;
				}
			}

			/**
			 * 当前变换模式对应的 Transform override 键：拖拽结束时由 DreamEngine 记录到 prefab 实例上。
			 * 键与 Transform.getInspectableFields 的字段名一致（position / rotation / scale）。
			 */
			public function get transformOverrideKey():String
			{
				if (_mode == MODE_ROTATE) return "Transform.rotation";
				if (_mode == MODE_SCALE) return "Transform.scale";
				return "Transform.position";
			}

			/** 每帧调用：把 Gizmo 对齐到选中元素的变换，并保持覆盖层置顶。 */
			public function update():void
			{
				if (!_visible) return;

				var e:Element = _selection.selectedElement;
				var rt:RectTransform = (e != null) ? e.getComponent(RectTransform) as RectTransform : null;
				var ui:Boolean = (rt != null);
				var ur:Object = (ui && e != null && CanvasSystem.current != null)
					? CanvasSystem.current.editorRectOf(e) : null;
				var uiScreen:Boolean = (ui && ur != null && ur.space == "screen");

				// 手柄覆盖层父级：屏幕空间 UI → 屏幕容器；世界元素与世界空间 UI → 渲染根
				// （世界空间 UI 的层容器本就在渲染根下，手柄必须同处一个坐标系才能对齐）。
				var parent:DrawableContainer = uiScreen ? _render.screenContainer : _render.root;
				if (parent == null)
				{
					_overlay.visible = false;
					return;
				}
				ensureOverlay(parent);
				if (!_overlayAttached) return;

				// 保持覆盖层在渲染顺序顶端（元素与选中高亮之上）。
				parent.removeChild(_overlay);
				parent.addChild(_overlay);

				var t:Transform = (e != null) ? e.getComponent(Transform) as Transform : null;
				// UI 元素本帧尚未布局（拿不到矩形）时无从定位手柄，直接隐藏。
				if (e == null || t == null || (ui && ur == null))
				{
					_overlay.visible = false;
					return;
				}
				_overlay.visible = true;

				var cx:Number;
				var cy:Number;
				var size:Number;
				var th:Number;
				var cs:Number;

				if (ui)
				{
					// 矩形中心定位手柄：屏幕空间取 stage 像素矩形；世界空间取世界 AABB。
					cx = Number(ur.x) + Number(ur.w) * 0.5;
					cy = Number(ur.y) + Number(ur.h) * 0.5;
					// 世界空间按相机缩放反算，保持手柄屏幕像素尺寸恒定。
					var z:Number = uiScreen ? 1 : _render.cameraZoom;
					size = GizmoScreenSize / z;
					th = HandleThickness / z;
					cs = CenterScreenSize / z;
					// 仅移动：当前 RectTransform 无 rotation/pivot，旋转/缩放对 UI 无意义。
					_moveLayer.visible = true;
					_rotateLayer.visible = false;
					_scaleLayer.visible = false;
					positionMove(cx, cy, size, th, cs);
					return;
				}

				var zoom:Number = _render.cameraZoom;
				size = GizmoScreenSize / zoom;
				th = HandleThickness / zoom;
				cs = CenterScreenSize / zoom;

				var wm:Matrix = t.worldMatrix;
				cx = wm.tx;
				cy = wm.ty;

				_moveLayer.visible = _mode == MODE_MOVE;
				_rotateLayer.visible = _mode == MODE_ROTATE;
				_scaleLayer.visible = _mode == MODE_SCALE;

				if (_mode == MODE_MOVE) positionMove(cx, cy, size, th, cs);
				else if (_mode == MODE_ROTATE) positionRotate(cx, cy, size, th);
				else positionScale(cx, cy, size, th, cs);
			}

			private function positionMove(cx:Number, cy:Number, size:Number, th:Number, cs:Number):void
			{
				var xBar:Drawable = _moveHandles[0].d as Drawable;
				xBar.x = cx; xBar.y = cy - th * 0.5; xBar.width = size; xBar.height = th;
				var xTip:Drawable = _moveHandles[1].d as Drawable;
				xTip.x = cx + size - th; xTip.y = cy - th; xTip.width = th * 2; xTip.height = th * 2;
				var yBar:Drawable = _moveHandles[2].d as Drawable;
				yBar.x = cx - th * 0.5; yBar.y = cy; yBar.width = th; yBar.height = size;
				var yTip:Drawable = _moveHandles[3].d as Drawable;
				yTip.x = cx - th; yTip.y = cy + size - th; yTip.width = th * 2; yTip.height = th * 2;
				var center:Drawable = _moveHandles[4].d as Drawable;
				center.x = cx - cs * 0.5; center.y = cy - cs * 0.5; center.width = cs; center.height = cs;
			}

			private function positionRotate(cx:Number, cy:Number, size:Number, th:Number):void
			{
				var segSize:Number = Math.max(th * 2, 2 / _render.cameraZoom);
				var R:Number = size;
				var n:int = RingSegments;
				for (var i:int = 0; i < n; i++)
				{
					var a:Number = i * (Math.PI * 2 / n);
					var seg:Drawable = _rotateHandles[i].d as Drawable;
					seg.x = cx + Math.cos(a) * R - segSize * 0.5;
					seg.y = cy + Math.sin(a) * R - segSize * 0.5;
					seg.width = segSize;
					seg.height = segSize;
				}
				var start:Drawable = _rotateHandles[n].d as Drawable;
				var s:Number = segSize * 1.6;
				start.x = cx + R - s * 0.5; start.y = cy - s * 0.5;
				start.width = s; start.height = s;
			}

			private function positionScale(cx:Number, cy:Number, size:Number, th:Number, cs:Number):void
			{
				var xh:Drawable = _scaleHandles[0].d as Drawable;
				xh.x = cx + size - th; xh.y = cy - th; xh.width = th * 2; xh.height = th * 2;
				var yh:Drawable = _scaleHandles[1].d as Drawable;
				yh.x = cx - th; yh.y = cy + size - th; yh.width = th * 2; yh.height = th * 2;
				var center:Drawable = _scaleHandles[2].d as Drawable;
				center.x = cx - cs * 0.5; center.y = cy - cs * 0.5; center.width = cs; center.height = cs;
			}

			// ── 交互 ──

			/**
			 * MOUSE_DOWN 消费入口：命中当前模式手柄则开始拖拽并返回 true（不再拾取）。
			 * 由 DreamEngine.onScenePick 在拾取前调用。
			 */
			public function onMouseDown(stageX:Number, stageY:Number):Boolean
			{
				// 兜底：上一次拖拽未收到 MOUSE_UP（鼠标移出窗口等）时，先结束并提交。
				if (_dragging)
				{
					_dragging = false;
					_dragKind = null;
					_uiDrag = false;
					if (_dragChanged)
					{
						pushUpdate();
						if (_onEditEnd != null) _onEditEnd();
					}
				}

				var e:Element = _selection.selectedElement;
				if (e == null) return false;
				var t:Transform = e.getComponent(Transform) as Transform;
				if (t == null) return false;

				// UI 元素（RectTransform）：手柄与元素同坐标系 → 屏幕空间画布用屏幕容器命中，
				// 世界空间画布用渲染根命中；拖拽统一换算到各元素所属画布局部单位后写回 offset。
				var rt:RectTransform = e.getComponent(RectTransform) as RectTransform;
				if (rt != null)
				{
					var er:Object = (CanvasSystem.current != null)
						? CanvasSystem.current.editorRectOf(e) : null;
					if (er == null) return false;
					var uiHit:Drawable = (er.space == "screen")
						? _render.hitTestScreen(stageX, stageY)
						: _render.hitTestWorld(stageX, stageY);
					if (uiHit == null) return false;
					var uiKind:String = matchHandle(uiHit);
					if (uiKind == null) return false;

					_uiDrag = true;
					_dragging = true;
					_dragKind = uiKind;
					_dragChanged = false;
					_uiStartStates.length = 0;
					for each (var ue:Element in _selection.elements)
					{
						var urt:RectTransform = ue.getComponent(RectTransform) as RectTransform;
						if (urt == null) continue;
						var uer:Object = (CanvasSystem.current != null)
							? CanvasSystem.current.editorRectOf(ue) : null;
						var layer:DrawableContainer = (uer != null) ? uer.layer as DrawableContainer : null;
						var start:Point = (layer != null) ? layer.globalToLocal(stageX, stageY)
						                                  : new Point(stageX, stageY);
						_uiStartStates.push({rt: urt, layer: layer, start: start,
							minX: urt.offsetMinX, minY: urt.offsetMinY,
							maxX: urt.offsetMaxX, maxY: urt.offsetMaxY});
					}

					if (_undoRedo != null) _undoRedo.record();
					return true;
				}

				var hit:Drawable = _render.hitTestWorld(stageX, stageY);
				if (hit == null) return false;

				var kind:String = matchHandle(hit);
				if (kind == null) return false;

				// 记录拖拽起点状态；undo 快照在拖拽前记录，撤销即回到拖拽前。
				_uiDrag = false;
				_dragging = true;
				_dragKind = kind;
				_dragChanged = false;
				_startWorldMouse = _render.screenToWorld(stageX, stageY);
				_startX = t.x;
				_startY = t.y;
				_startRot = t.rotation;
				_startSX = t.scaleX;
				_startSY = t.scaleY;
				_handleLenWorld = GizmoScreenSize / _render.cameraZoom;

				// 批量：记录全部选中元素的起点，供拖拽中对每个元素应用相对增量。
				_startStates.length = 0;
				for each (var se:Element in _selection.elements)
				{
					var st:Transform = se.getComponent(Transform) as Transform;
					if (st == null) continue;
					_startStates.push({t: st, x: st.x, y: st.y, rotation: st.rotation,
						scaleX: st.scaleX, scaleY: st.scaleY});
				}

				var wm:Matrix = t.worldMatrix;
				var cx:Number = wm.tx;
				var cy:Number = wm.ty;
				if (kind == "rotate")
					_startAngle = Math.atan2(_startWorldMouse.y - cy, _startWorldMouse.x - cx);
				if (kind == "uniform")
					_startDist = Math.max(Point.distance(_startWorldMouse, new Point(cx, cy)), 1);

				if (_undoRedo != null) _undoRedo.record();
				return true;
			}

			private function onMouseMove(e:MouseEvent):void
			{
				if (!_dragging) return;
				if (_uiDrag)
				{
					moveUi(e.stageX, e.stageY);
					pushUpdateIfNeeded();
					return;
				}
				var wmPt:Point = _render.screenToWorld(e.stageX, e.stageY);
				switch (_dragKind)
				{
					case "x": moveAxis(wmPt, true, false); break;
					case "y": moveAxis(wmPt, false, true); break;
					case "free": moveAxis(wmPt, true, true); break;
					case "rotate": doRotate(wmPt); break;
					case "scaleX": doScaleAxis(wmPt, true, false); break;
					case "scaleY": doScaleAxis(wmPt, false, true); break;
					case "uniform": doScaleUniform(wmPt); break;
				}
				pushUpdateIfNeeded();
			}

			private function onMouseUp(e:MouseEvent):void
			{
				if (!_dragging) return;
				_dragging = false;
				_dragKind = null;
				_uiDrag = false;
				if (_dragChanged)
				{
					pushUpdate(); // 结束前直接推送最终状态
					if (_onEditEnd != null) _onEditEnd();
				}
			}

			private function onKeyDown(e:KeyboardEvent):void
			{
				if (!_render.viewportNavigationEnabled) return;
				if (_dragging) return;
				// UI 元素仅支持移动：忽略旋转/缩放模式切换。
				var uiOnly:Boolean = isUiTarget();
				switch (e.keyCode)
				{
					case Keyboard.W: _mode = MODE_MOVE; break;
					case Keyboard.E:
						if (uiOnly) return;
						_mode = MODE_ROTATE;
						break;
					case Keyboard.R:
						if (uiOnly) return;
						_mode = MODE_SCALE;
						break;
					default: return;
				}
			}

			/** 当前主选中是否为 UI 元素（带 RectTransform，仅支持移动）。 */
			private function isUiTarget():Boolean
			{
				var e:Element = _selection.selectedElement;
				return e != null && e.getComponent(RectTransform) != null;
			}

			/**
			 * UI 元素位移：把舞台点换算到各元素所属画布的局部坐标系，取相对起点的增量
			 * （按轴约束），等量加到 offsetMin 与 offsetMax —— 矩形平移、尺寸不变。
			 *
			 * 手柄轴与画布局部轴一致；世界空间画布带旋转时手柄按世界轴绘制而位移沿画布
			 * 局部轴（已知限制，未旋转画布无差异）。
			 */
			private function moveUi(stageX:Number, stageY:Number):void
			{
				if (_uiStartStates.length == 0) return;
				var useX:Boolean = (_dragKind != "y");
				var useY:Boolean = (_dragKind != "x");

				for each (var st:Object in _uiStartStates)
				{
					var rt:RectTransform = st.rt as RectTransform;
					if (rt == null) continue;
					var layer:DrawableContainer = st.layer as DrawableContainer;
					var p:Point = (layer != null) ? layer.globalToLocal(stageX, stageY)
					                              : new Point(stageX, stageY);
					var s0:Point = st.start as Point;
					var dx:Number = useX ? (p.x - s0.x) : 0;
					var dy:Number = useY ? (p.y - s0.y) : 0;
					rt.offsetMinX = Number(st.minX) + dx;
					rt.offsetMinY = Number(st.minY) + dy;
					rt.offsetMaxX = Number(st.maxX) + dx;
					rt.offsetMaxY = Number(st.maxY) + dy;
				}
				_dragChanged = true;
			}

			/** 拖拽中节流推送（约 100ms 一次），供引擎推送 Inspector 快照实时刷新。 */
			private function pushUpdateIfNeeded():void
			{
				var now:int = getTimer();
				if (now - _lastPushMs < PushIntervalMs) return;
				_lastPushMs = now;
				pushUpdate();
			}

			/** 立即推送一次 Inspector 快照（拖拽结束兜底）。 */
			private function pushUpdate():void
			{
				if (_onEditUpdate != null) _onEditUpdate();
			}

			// ── 拖拽数学 ──

			/** 位移：世界 delta（按轴约束）→ 各元素父级局部 delta → 写回全部选中元素的 local x/y。 */
			private function moveAxis(wmPt:Point, useX:Boolean, useY:Boolean):void
			{
				if (_startStates.length == 0) return;

				var worldDelta:Point = new Point(
					useX ? wmPt.x - _startWorldMouse.x : 0,
					useY ? wmPt.y - _startWorldMouse.y : 0);

				for each (var st:Object in _startStates)
				{
					var t2:Transform = st.t as Transform;
					if (t2 == null) continue;
					var localDelta:Point = worldDelta.clone();
					var parent:Transform = t2.parent;
					if (parent != null)
					{
						var inv:Matrix = parent.worldMatrix.clone();
						inv.invert();
						localDelta = inv.deltaTransformPoint(worldDelta);
					}
					t2.x = st.x + localDelta.x;
					t2.y = st.y + localDelta.y;
					t2.setLocalDirty();
				}
				_dragChanged = true;
			}

			/** 旋转：以 primary 元素世界原点为中心的世界角增量，叠加到全部选中元素的本地 rotation。 */
			private function doRotate(wmPt:Point):void
			{
				if (_startStates.length == 0) return;
				var t:Transform = _selection.selectedElement != null
					? (_selection.selectedElement.getComponent(Transform) as Transform) : null;
				var wm:Matrix = t != null ? t.worldMatrix : null;
				var angle:Number = wm != null
					? Math.atan2(wmPt.y - wm.ty, wmPt.x - wm.tx)
					: Math.atan2(wmPt.y - _startWorldMouse.y, wmPt.x - _startWorldMouse.x);
				var delta:Number = angle - _startAngle;

				for each (var st:Object in _startStates)
				{
					var t2:Transform = st.t as Transform;
					if (t2 == null) continue;
					t2.rotation = st.rotation + delta;
					t2.setLocalDirty();
				}
				_dragChanged = true;
			}

			/** 单轴缩放：因子 = 1 + 轴上位移 / 手柄长度，应用于全部选中元素。 */
			private function doScaleAxis(wmPt:Point, useX:Boolean, useY:Boolean):void
			{
				if (_startStates.length == 0) return;

				var f:Number = useX
					? 1 + (wmPt.x - _startWorldMouse.x) / _handleLenWorld
					: 1 + (wmPt.y - _startWorldMouse.y) / _handleLenWorld;

				for each (var st:Object in _startStates)
				{
					var t2:Transform = st.t as Transform;
					if (t2 == null) continue;
					if (useX) t2.scaleX = Math.max(st.scaleX * f, MinScale);
					if (useY) t2.scaleY = Math.max(st.scaleY * f, MinScale);
					t2.setLocalDirty();
				}
				_dragChanged = true;
			}

			/** 等比缩放：按鼠标到 primary 元素世界原点的距离比例，应用于全部选中元素。 */
			private function doScaleUniform(wmPt:Point):void
			{
				if (_startStates.length == 0) return;
				var t:Transform = _selection.selectedElement != null
					? (_selection.selectedElement.getComponent(Transform) as Transform) : null;
				var wm:Matrix = t != null ? t.worldMatrix : null;
				var ref:Point = wm != null ? new Point(wm.tx, wm.ty) : _startWorldMouse;
				var dist:Number = Math.max(Point.distance(wmPt, ref), 1);
				var f:Number = dist / _startDist;

				for each (var st:Object in _startStates)
				{
					var t2:Transform = st.t as Transform;
					if (t2 == null) continue;
					t2.scaleX = Math.max(st.scaleX * f, MinScale);
					t2.scaleY = Math.max(st.scaleY * f, MinScale);
					t2.setLocalDirty();
				}
				_dragChanged = true;
			}

			// ── 工具 ──

			private function matchHandle(hit:Drawable):String
			{
				var list:Array = activeHandles();
				for each (var h:Object in list)
				{
					if ((h.d as Drawable).sameAs(hit)) return h.kind;
				}
				return null;
			}

			private function activeHandles():Array
			{
				// UI 元素仅支持移动：显示层与命中层都取移动手柄（与 update 的显示保持一致）。
				if (isUiTarget()) return _moveHandles;
				if (_mode == MODE_MOVE) return _moveHandles;
				if (_mode == MODE_ROTATE) return _rotateHandles;
				return _scaleHandles;
			}

			private function ensureOverlay(parent:DrawableContainer):void
			{
				if (parent == null) return;
				if (_overlayAttached && _overlayParent === parent) return;
				if (_overlayAttached) _overlay.removeFromParent();
				parent.addChild(_overlay);
				_overlayParent = parent;
				_overlayAttached = true;
			}
		}
	}
}
