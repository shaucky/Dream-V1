package dream.engine.scene
{
	CONFIG::STUDIO
	{
		import dream.engine.ecs.Element;
		import dream.engine.ecs.World;
		import dream.engine.ecs.systems.RenderSystem;
		import dream.engine.render.CameraComponent;
		import dream.engine.render.DisplayComponent;
		import dream.engine.render.Drawable;
		import dream.engine.render.DrawableContainer;
		import dream.engine.render.RenderEngine;
		import dream.engine.transform.Transform;
		import dream.engine.ui.Canvas;
		import dream.engine.ui.CanvasSystem;
		import dream.engine.ui.RectTransform;

		import flash.display.Stage;
		import flash.events.MouseEvent;
		import flash.geom.Matrix;
		import flash.geom.Point;
		import flash.geom.Rectangle;

		/**
		 * 编辑器场景选中管理：多选 + 高亮框 + 视口框选。
		 *
		 * 选择集为有序数组，最后一个（最新加入/操作）为 primary（主选中），
		 * 批量变换以 primary 为操作基准。视口内单点拾取命中由 hitAt 完成，
		 * 选择语义（单选/toggle）由调用方（DreamEngine.onScenePick）决定。
		 *
		 * 高亮：为每个选中元素绘制半透明填充 + 4 条细边组成的矩形框。世界元素用
		 * 世界空间覆盖层（挂渲染根，随相机变换，边框厚度按相机缩放反算）；UI 元素
		 * （带 RectTransform，画在屏幕空间画布层）用另一套屏幕空间覆盖层（挂屏幕
		 * 容器，矩形取 CanvasSystem 解析出的 stage 像素矩形）。两个覆盖层每帧重新
		 * 追加到各自父级末尾保持置顶；全部 touchable=false，使拾取自动忽略它们。
		 * primary 用高亮色、其余用普通色区分。
		 *
		 * 拾取：先查屏幕空间画布的 UI（屏幕容器恒在渲染根之上），再按 RenderSystem 的渲染顺序
		 * 自上层往下查渲染根内容 —— 世界元素与世界/相机空间画布层在同一序列里比较（画布层内
		 * 按画布绘制序判定），故拾取顺序与绘制顺序一致。
		 *
		 * 框选：空白处按下并拖拽（onScenePick 空白分支调用 beginMarquee），
		 * 监听 stage MOUSE_MOVE/UP 更新矩形并判定与元素世界 AABB 相交，
		 * 结束时通过 setSelection 覆盖选择集，并触发 onSelectionChange。
		 */
		public final class SceneSelection
		{
			// 高亮外观：primary 亮黄描边，其余青蓝描边 + 半透明填充。
			private static const PrimaryColor:uint = 0xFFD700;
			private static const OutlineColor:uint = 0x5AC8FA;
			private static const FillAlpha:Number = 0.25;
			private static const PixelThickness:Number = 2; // 边框屏幕像素宽

			// 框选外观。
			private static const MarqueeColor:uint = 0x8AB4F8;
			private static const MarqueeAlpha:Number = 0.15;

			// 相机视口预览外观（选中相机元素时显示其可视范围）。
			private static const CameraPreviewColor:uint = 0xFFFFFF;
			// 最小框选拖拽距离（世界单位）：小于该尺寸视为"点击未拖拽"，不参与 AABB 相交判定。
			private static const MarqueeMinDrag:Number = 2.0;

			private var _world:World;
			private var _render:RenderEngine;
			private var _stage:Stage;

			// 选择集：有序数组，末尾为 primary。
			private var _ids:Array = [];

			// 选择变化回调开关：false 时静默设置（SceneSelectHandler 收到 Studio
			// 回发的 scene.select 时关闭，避免"picked → select → picked"回环风暴）。
			private var _notifyEnabled:Boolean = true;

			// 高亮覆盖层与边框池（fill + 4 边为一套，按选中数量增删）。
            private var _overlay:DrawableContainer;
            private var _boxes:Array = [];
            private var _overlayAttached:Boolean = false;
            // 编辑器可见性：运行模式隐藏（EngineSetRunningHandler 联动），update 短路。
            private var _visible:Boolean = true;

			// UI 元素高亮：屏幕空间覆盖层（挂屏幕容器，位于 UI 画布层之上），
			// 与 _overlay/_boxes 分开，因为 UI 元素不随相机变换。
			private var _uiOverlay:DrawableContainer;
			private var _uiBoxes:Array = [];
			private var _uiOverlayAttached:Boolean = false;

			// 框选状态。
			private var _marqueeActive:Boolean = false;
			private var _marqueeStart:Point = new Point();
			private var _marqueeCur:Point = new Point();
			private var _marqueeFill:Drawable;
			private var _marqueeFrame:Array = [];

			// 相机视口预览框（4 条边）。
			private var _cameraFrame:Array = [];

			/** 选择集变化（选中/取消/框选结束）时回调，由 DreamEngine 用于回传 Studio。 */
			public var onSelectionChange:Function;

			public function SceneSelection(world:World, render:RenderEngine, stage:Stage)
			{
				_world = world;
				_render = render;
				_stage = stage;

				_overlay = render.createContainer();
				_uiOverlay = render.createContainer();

				_marqueeFill = render.createQuad(1, 1, MarqueeColor);
				_marqueeFill.touchable = false;
				_marqueeFill.alpha = MarqueeAlpha;
				_overlay.addChild(_marqueeFill);
				for (var i:int = 0; i < 4; i++)
				{
					var q:Drawable = render.createQuad(1, 1, MarqueeColor);
					q.touchable = false;
					_marqueeFrame.push(q);
					_overlay.addChild(q);
				}

				_stage.addEventListener(MouseEvent.MOUSE_MOVE, onStageMouseMove);
				_stage.addEventListener(MouseEvent.MOUSE_UP, onStageMouseUp);
			}

			// ── 选择集访问 ──

			/** 当前选中数量。 */
			public function get count():int { return _ids.length; }

			/** 主选中元素 ID（最后操作）；-1 表示未选中。 */
			public function get selectedElementId():int
			{
				return _ids.length > 0 ? int(_ids[_ids.length - 1]) : -1;
			}

			/** 当前主选中的存活元素；无选中或已销毁返回 null。 */
			public function get selectedElement():Element
			{
				return findElement(selectedElementId);
			}

			/** 全部选中元素 ID 的副本（数组顺序即选择顺序，末尾为 primary）。 */
			public function get selectedIds():Array { return _ids.concat(); }

			/** 全部选中元素中存活的（按选择顺序，末尾为 primary）。 */
			public function get elements():Array
			{
				var list:Array = [];
				for each (var id:int in _ids)
				{
					var e:Element = findElement(id);
					if (e != null) list.push(e);
				}
				return list;
			}

			/** 是否已选中指定元素。 */
			public function contains(id:int):Boolean { return _ids.indexOf(id) >= 0; }

			/**
			 * 暂停/恢复选择变化回调。SceneSelectHandler 收到 Studio 的 scene.select
			 * 时先关后开，静默应用外部选择集，防止与 pushSelection 形成回环。
			 */
			public function setNotifyEnabled(v:Boolean):void { _notifyEnabled = v; }

			// ── 选择操作 ──

			/** 单选替换（Hierarchy 点击、scene.select、视口单选拾取）。id=-1 清空。 */
			public function select(id:int):void
			{
				_ids.length = 0;
				if (id >= 0) _ids.push(id);
				notifyChanged();
			}

			/** Shift/Ctrl 点击：在选中集与未选中间切换。 */
			public function toggle(id:int):void
			{
				if (id < 0) return;
				var i:int = _ids.indexOf(id);
				if (i >= 0) _ids.splice(i, 1);
				else _ids.push(id);
				notifyChanged();
			}

			/** 批量设置选择集（框选结束、Studio 多选）。primaryId 必须存在于 ids 才生效。 */
			public function setSelection(ids:Array, primaryId:int):void
			{
				_ids.length = 0;
				for each (var id:int in ids)
				{
					if (id >= 0 && _ids.indexOf(id) < 0 && findElement(id) != null) _ids.push(id);
				}
				if (primaryId >= 0 && _ids.indexOf(primaryId) < 0 && findElement(primaryId) != null)
					_ids.push(primaryId);
				notifyChanged();
			}

			/** 清空选择。 */
			public function clear():void
			{
				_ids.length = 0;
				notifyChanged();
			}

			// ── 拾取 ──

			/**
			 * 纯命中检测：返回点击处命中的元素 ID（-1 表示空白/未命中）。
			 * 不修改选择集——选择语义由调用方决定（单选/toggle/框选）。
			 *
			 * 精确命中完全在引擎侧计算（绕开 Starling 矩阵 getter / hitTest 路径）：
			 *   渲染矩阵 M = worldMatrix × T(-pivot)（与 RenderSystem 同一公式）
			 *   → 逆变换世界点 → 本地矩形判定（对矩形网格等价于三角形判定，
			 *     旋转后包围盒角落逆变换回本地会落在矩形外，不误命中）
			 *   → 纹理像素 alpha 判定（透明像素不命中）。
			 * 拾取顺序：屏幕空间画布 UI（屏幕容器恒在渲染根之上）→ 渲染根内容按
			 * RenderSystem 的渲染顺序自上层往下（世界元素与世界/相机空间画布层在同一
			 * 序列里比较，画布层内再按画布绘制序判定），返回首个命中元素。
			 */
			public function hitAt(stageX:Number, stageY:Number):int
			{
				if (!_render.isReady) return -1;

				var cs:CanvasSystem = CanvasSystem.current;
				if (cs != null)
				{
					var ui:Element = cs.hitTestScreenElement(stageX, stageY);
					if (ui != null) return ui.id;
				}

				var rs:RenderSystem = RenderSystem.current;
				var world:Point = _render.screenToWorld(stageX, stageY);
				if (rs == null || world == null) return -1;

				// 按渲染顺序自上层往下：铺满视口的背景层因排序最底而最后才被拾取，
				// 不会像按元素数组下标那样抢走其它元素的点击。
				var order:Vector.<Element> = rs.renderOrder;
				for (var i:int = order.length - 1; i >= 0; i--)
				{
					var e:Element = order[i];
					if (e == null || !e.alive) continue;

					// 世界/相机空间画布层：命中交给其内部 UI（按画布内绘制序）。
					if (e.getComponent(Canvas) != null)
					{
						if (cs == null) continue;
						var inner:Element = cs.hitTestCanvasElement(e, stageX, stageY);
						if (inner != null) return inner.id;
						continue;
					}

					var t:Transform = e.getComponent(Transform) as Transform;
					var dc:DisplayComponent = e.getComponent(DisplayComponent) as DisplayComponent;
					if (t == null || dc == null || dc.displayObject == null || !dc.displayObject.visible) continue;
					if (hitTestElement(t, dc, world.x, world.y)) return e.id;
				}
				return -1;
			}

			/** 引擎侧精确命中：逆变换 + 本地矩形 + 像素 alpha。 */
			private function hitTestElement(t:Transform, dc:DisplayComponent, wx:Number, wy:Number):Boolean
			{
				// 渲染矩阵 M = worldMatrix × T(-pivot)（与 RenderSystem 保持同一公式）。
				var m:Matrix = t.worldMatrix.clone();
				var px:Number = dc.effectivePivotX;
				var py:Number = dc.effectivePivotY;
				if (px != 0 || py != 0)
				{
					m.tx -= m.a * px + m.c * py;
					m.ty -= m.b * px + m.d * py;
				}

				var inv:Matrix = m.clone();
				inv.invert();
				var lp:Point = inv.transformPoint(new Point(wx, wy));

				var b:Rectangle = dc.displayObject.localBounds;
				if (b == null || b.width <= 0 || b.height <= 0) return false;
				if (lp.x < b.x || lp.y < b.y || lp.x > b.x + b.width || lp.y > b.y + b.height) return false;

				return dc.displayObject.alphaHit(lp.x, lp.y);
			}

			// ── 框选 ──

			/** 空白处按下：开始框选（记录世界坐标起点）。 */
			public function beginMarquee(stageX:Number, stageY:Number):void
			{
				_marqueeActive = true;
				_marqueeStart = worldPoint(stageX, stageY);
				_marqueeCur.x = _marqueeStart.x;
				_marqueeCur.y = _marqueeStart.y;
			}

			/** 框选是否进行中。 */
			public function get marqueeActive():Boolean { return _marqueeActive; }

			/** 结束框选：判定矩形内所有可拾取元素并覆盖选择集（框内为空则清空）。
			 *  未发生有效拖拽（点击未拖动，矩形小于 MarqueeMinDrag）：视为空白点击，
			 *  直接清空选择——单点命中已由 hitAt 精确判定完成，零尺寸矩形若再与对象
			 *  AABB 求交会把"包围盒内非可视区域"的空白点击误选成对象。 */
			private function endMarquee():void
			{
				if (!_marqueeActive) return;
				_marqueeActive = false;

				var rect:Rectangle = marqueeRect();
				if (rect.width < MarqueeMinDrag && rect.height < MarqueeMinDrag)
				{
					setSelection([], -1);
					return;
				}

				var ids:Array = [];
				for each (var e:Element in _world.elements)
				{
					if (!e.alive) continue;
					var dc:DisplayComponent = e.getComponent(DisplayComponent) as DisplayComponent;
					if (dc == null || dc.displayObject == null || !dc.displayObject.visible) continue;
					var b:Rectangle = worldBounds(dc.displayObject);
					if (b != null && rectsIntersect(rect, b)) ids.push(e.id);
				}
				setSelection(ids, ids.length > 0 ? int(ids[ids.length - 1]) : -1);
			}

			/** 当前框选矩形（世界坐标，已规范化）。 */
			private function marqueeRect():Rectangle
			{
				var x:Number = Math.min(_marqueeStart.x, _marqueeCur.x);
				var y:Number = Math.min(_marqueeStart.y, _marqueeCur.y);
				return new Rectangle(x, y,
					Math.abs(_marqueeCur.x - _marqueeStart.x),
					Math.abs(_marqueeCur.y - _marqueeStart.y));
			}

			private function onStageMouseMove(e:MouseEvent):void
			{
				if (!_marqueeActive) return;
				var p:Point = worldPoint(e.stageX, e.stageY);
				_marqueeCur.x = p.x;
				_marqueeCur.y = p.y;
			}

			private function onStageMouseUp(e:MouseEvent):void
			{
				if (!_marqueeActive) return;
				endMarquee();
			}

			// ── 每帧更新 ──

			/**
			 * 运行模式隐藏选中高亮：隐藏覆盖层与全部高亮框，取消进行中的框选，
			 * 并短路 update 使其不再参与每帧渲染树重排（removeChild/addChild）。
			 * 由 EngineSetRunningHandler 在 running 切换时联动调用。
			 */
			public function setSelectionVisible(v:Boolean):void
			{
				_visible = v;
				if (v)
				{
					_overlay.visible = true;
					if (_uiOverlay != null) _uiOverlay.visible = true;
					return;
				}
				_overlay.visible = false;
				if (_uiOverlay != null) _uiOverlay.visible = false;
				_marqueeActive = false;
				_marqueeFill.visible = false;
				for each (var q:Drawable in _marqueeFrame) q.visible = false;
				for each (var box:Object in _boxes)
				{
					(box.fill as Drawable).visible = false;
					for each (var hf:Drawable in box.frame) hf.visible = false;
				}
				for each (var ubox:Object in _uiBoxes)
				{
					(ubox.fill as Drawable).visible = false;
					for each (var uhf:Drawable in ubox.frame) uhf.visible = false;
				}
				hideCameraFrame();
			}

			/** 每帧调用：对齐所有选中元素的高亮框，绘制框选矩形，保持覆盖层置顶。 */
			public function update():void
			{
				if (!_visible) return;
				ensureOverlay();
				var root:DrawableContainer = _render.root;
				if (root != null && _overlayAttached)
				{
					// 保持覆盖层置顶：重新追加到渲染根末尾（异步纹理加载的元素可能后挂载）。
					root.removeChild(_overlay);
					root.addChild(_overlay);
				}

				// 框选矩形绘制在元素高亮之上。
				updateMarquee();

				// 选中集按空间拆分：UI 元素（带 RectTransform）用屏幕空间高亮，其余用世界空间。
				var elements:Array = [];
				var uiItems:Array = [];
				for each (var id:int in _ids)
				{
					var e:Element = findElement(id);
					if (e == null) continue;
					var rt:RectTransform = e.getComponent(RectTransform) as RectTransform;
					if (rt != null)
					{
						// UI 元素：屏幕空间画布 → 屏幕覆盖层（stage 像素）；世界空间画布 → 世界覆盖层。
						var er:Object = (CanvasSystem.current != null)
							? CanvasSystem.current.editorRectOf(e) : null;
						if (er != null)
						{
							if (er.space == "world")
								elements.push({id: id, rect: new Rectangle(er.x, er.y, er.w, er.h)});
							else
								uiItems.push({id: id, rect: er});
						}
						continue;
					}
					var t:Transform = e.getComponent(Transform) as Transform;
					var dc:DisplayComponent = e.getComponent(DisplayComponent) as DisplayComponent;
					if (t == null || dc == null || dc.displayObject == null || !dc.displayObject.visible) continue;
					var b:Rectangle = worldBounds(dc.displayObject);
					if (b != null) elements.push({id: id, rect: b});
				}

				var zoom:Number = _render.cameraZoom;
				var thickness:Number = zoom > 0 ? PixelThickness / zoom : PixelThickness;
				var pad:Number = thickness * 0.5;

				var visibleCount:int = 0;
				for each (var item:Object in elements)
				{
					var box:Object = boxAt(visibleCount);
					var isPrimary:Boolean = (item.id == selectedElementId);
					positionBox(box, item.rect, thickness, pad, isPrimary ? PrimaryColor : OutlineColor);
					visibleCount++;
				}

				// 多余的高亮框隐藏（选择集缩小时）。
				while (_boxes.length > visibleCount)
				{
					var hide:Object = _boxes.pop();
					hide.fill.visible = false;
					for each (var hf:Drawable in hide.frame) hf.visible = false;
				}

				// UI 元素高亮（屏幕空间）。
				updateUiBoxes(uiItems);

				// 相机元素：绘制其视口可视范围框。
				updateCameraPreview();
			}

			/**
			 * 主选中为相机元素时，绘制其视口可视范围框（世界坐标，跟随相机旋转）。
			 * Unity 正交模型：世界可视高度固定为 2×orthographicSize，
			 * 宽度 = 高度 × 窗口宽高比。
			 */
			private function updateCameraPreview():void
			{
				var e:Element = selectedElement;
				var camC:CameraComponent = e != null ? (e.getComponent(CameraComponent) as CameraComponent) : null;
				var t:Transform = e != null ? (e.getComponent(Transform) as Transform) : null;
				var ortho:Number = camC != null ? camC.orthographicSize : 0;
				if (camC == null || t == null || ortho <= 0)
				{
					hideCameraFrame();
					return;
				}

				var viewH:Number = 2 * ortho;
				var viewW:Number = viewH * (_stage.stageWidth / _stage.stageHeight);
				var wm:Matrix = t.worldMatrix;
				var cx:Number = wm.tx;
				var cy:Number = wm.ty;
				var rot:Number = Math.atan2(wm.b, wm.a);
				var cosR:Number = Math.cos(rot);
				var sinR:Number = Math.sin(rot);
				var hw:Number = viewW * 0.5;
				var hh:Number = viewH * 0.5;

				// 世界 4 角：中心 + R(θ)·(±hw, ±hh)。
				var corners:Array = [
					new Point(cx + cosR * -hw - sinR * -hh, cy + sinR * -hw + cosR * -hh),
					new Point(cx + cosR * hw - sinR * -hh, cy + sinR * hw + cosR * -hh),
					new Point(cx + cosR * hw - sinR * hh, cy + sinR * hw + cosR * hh),
					new Point(cx + cosR * -hw - sinR * hh, cy + sinR * -hw + cosR * hh)
				];

				var camZoom:Number = _render.cameraZoom;
				var th:Number = camZoom > 0 ? PixelThickness / camZoom : PixelThickness;
				for (var i:int = 0; i < 4; i++)
				{
					var a:Point = corners[i];
					var b:Point = corners[(i + 1) % 4];
					var dx:Number = b.x - a.x;
					var dy:Number = b.y - a.y;
					var len:Number = Math.sqrt(dx * dx + dy * dy);
					if (len <= 0) continue;
					var edge:Drawable = cameraEdgeAt(i);
					edge.color = CameraPreviewColor;
					edge.visible = true;
					// 边矩阵 = T(a)·R(θ)·S(len, th)：绕起点旋转沿 b 方向延伸。
					// 注意 AS3 Matrix 的 scale/rotate/translate 是前置乘（this = op × this），
					// 必须按 scale → rotate → translate 顺序调用才得到 T·R·S；
					// 反序会得到 S·R·T，平移量被 len 倍缩放导致边跑到视野外。
					var m:Matrix = new Matrix();
					m.scale(len, th);
					m.rotate(Math.atan2(dy, dx));
					m.translate(a.x, a.y);
					edge.transformationMatrix = m;
				}
			}

			/** 取第 i 条相机预览边（不足则创建）。 */
			private function cameraEdgeAt(i:int):Drawable
			{
				while (_cameraFrame.length <= i)
				{
					var q:Drawable = _render.createQuad(1, 1, CameraPreviewColor);
					q.touchable = false;
					_overlay.addChild(q);
					_cameraFrame.push(q);
				}
				return _cameraFrame[i] as Drawable;
			}

			/** 隐藏相机预览框。 */
			private function hideCameraFrame():void
			{
				for each (var q:Drawable in _cameraFrame) q.visible = false;
			}

			private function updateMarquee():void
			{
				if (!_marqueeActive)
				{
					_marqueeFill.visible = false;
					for each (var q:Drawable in _marqueeFrame) q.visible = false;
					return;
				}
				var rect:Rectangle = marqueeRect();
				if (rect.width <= 0.01 && rect.height <= 0.01)
				{
					_marqueeFill.visible = false;
					for each (var q0:Drawable in _marqueeFrame) q0.visible = false;
					return;
				}
				_marqueeFill.visible = true;
				_marqueeFill.x = rect.x;
				_marqueeFill.y = rect.y;
				_marqueeFill.width = rect.width;
				_marqueeFill.height = rect.height;

				var zoom:Number = _render.cameraZoom;
				var th:Number = zoom > 0 ? PixelThickness / zoom : PixelThickness;
				var pad:Number = th * 0.5;
				var x:Number = rect.x;
				var y:Number = rect.y;
				var w:Number = rect.width;
				var h:Number = rect.height;

				var t0:Drawable = _marqueeFrame[0];
				t0.x = x - pad; t0.y = y - pad; t0.width = w + pad * 2; t0.height = th;
				var t1:Drawable = _marqueeFrame[1];
				t1.x = x - pad; t1.y = y + h + pad - th; t1.width = w + pad * 2; t1.height = th;
				var t2:Drawable = _marqueeFrame[2];
				t2.x = x - pad; t2.y = y - pad; t2.width = th; t2.height = h + pad * 2;
				var t3:Drawable = _marqueeFrame[3];
				t3.x = x + w + pad - th; t3.y = y - pad; t3.width = th; t3.height = h + pad * 2;

				for each (var q1:Drawable in _marqueeFrame) q1.visible = true;
			}

			private function positionBox(box:Object, rect:Rectangle, thickness:Number, pad:Number, color:uint):void
			{
				var fill:Drawable = box.fill as Drawable;
				fill.color = color;
				fill.visible = true;
				fill.x = rect.x;
				fill.y = rect.y;
				fill.width = rect.width;
				fill.height = rect.height;

				var frame:Array = box.frame as Array;
				var t0:Drawable = frame[0];
				t0.color = color; t0.visible = true;
				t0.x = rect.x - pad; t0.y = rect.y - pad; t0.width = rect.width + pad * 2; t0.height = thickness;
				var t1:Drawable = frame[1];
				t1.color = color; t1.visible = true;
				t1.x = rect.x - pad; t1.y = rect.y + rect.height + pad - thickness; t1.width = rect.width + pad * 2; t1.height = thickness;
				var t2:Drawable = frame[2];
				t2.color = color; t2.visible = true;
				t2.x = rect.x - pad; t2.y = rect.y - pad; t2.width = thickness; t2.height = rect.height + pad * 2;
				var t3:Drawable = frame[3];
				t3.color = color; t3.visible = true;
				t3.x = rect.x + rect.width + pad - thickness; t3.y = rect.y - pad; t3.width = thickness; t3.height = rect.height + pad * 2;
			}

			/** 取第 i 套高亮框（fill + 4 边），不足则创建。 */
			private function boxAt(i:int):Object
			{
				while (_boxes.length <= i)
				{
					var fill:Drawable = _render.createQuad(1, 1, OutlineColor);
					fill.touchable = false;
					fill.alpha = FillAlpha;
					_overlay.addChild(fill);
					var frame:Array = [];
					for (var k:int = 0; k < 4; k++)
					{
						var q:Drawable = _render.createQuad(1, 1, OutlineColor);
						q.touchable = false;
						_overlay.addChild(q);
						frame.push(q);
					}
					_boxes.push({fill: fill, frame: frame});
				}
				return _boxes[i];
			}

			/**
			 * UI 元素高亮：挂屏幕容器（UI 画布层之上）。矩形已是 stage 像素，
			 * 厚度即屏幕像素，无需按相机缩放反算。
			 */
			private function updateUiBoxes(items:Array):void
			{
				var sc:DrawableContainer = _render.screenContainer;
				if (sc == null)
				{
					if (_uiOverlay != null) _uiOverlay.visible = false;
					return;
				}
				// 保持置顶：画布层每帧按 sortingOrder 重排子级索引，覆盖层需重新追加。
				if (_uiOverlayAttached) sc.removeChild(_uiOverlay);
				sc.addChild(_uiOverlay);
				_uiOverlayAttached = true;
				_uiOverlay.visible = true;

				var thickness:Number = PixelThickness;
				var pad:Number = thickness * 0.5;
				var count:int = 0;
				for each (var item:Object in items)
				{
					var r:Object = item.rect as Object;
					var box:Object = uiBoxAt(count);
					positionBox(box, new Rectangle(r.x, r.y, r.w, r.h), thickness, pad,
						(item.id == selectedElementId) ? PrimaryColor : OutlineColor);
					count++;
				}
				while (_uiBoxes.length > count)
				{
					var hide:Object = _uiBoxes.pop();
					hide.fill.visible = false;
					for each (var hf:Drawable in hide.frame) hf.visible = false;
				}
				if (count == 0) _uiOverlay.visible = false;
			}

			/** 取第 i 套 UI 高亮框（fill + 4 边），不足则创建；挂 UI 覆盖层。 */
			private function uiBoxAt(i:int):Object
			{
				while (_uiBoxes.length <= i)
				{
					var fill:Drawable = _render.createQuad(1, 1, OutlineColor);
					fill.touchable = false;
					fill.alpha = FillAlpha;
					_uiOverlay.addChild(fill);
					var frame:Array = [];
					for (var k:int = 0; k < 4; k++)
					{
						var q:Drawable = _render.createQuad(1, 1, OutlineColor);
						q.touchable = false;
						_uiOverlay.addChild(q);
						frame.push(q);
					}
					_uiBoxes.push({fill: fill, frame: frame});
				}
				return _uiBoxes[i];
			}

			// ── 工具 ──

			/** 选择集变化回调：仅在开关开启时通知（静默设置用于外部回环场景）。 */
			private function notifyChanged():void
			{
				if (_notifyEnabled && onSelectionChange != null) onSelectionChange();
			}

			/** 渲染根就绪后将覆盖层挂入根容器（仅一次）。 */
			private function ensureOverlay():void
			{
				if (_overlayAttached) return;
				var root:DrawableContainer = _render.root;
				if (root == null) return;
				root.addChild(_overlay);
				_overlayAttached = true;
			}

			/** 按 ID 查找存活元素。 */
			private function findElement(id:int):Element
			{
				if (id < 0) return null;
				for each (var e:Element in _world.elements)
				{
					if (e.alive && e.id == id) return e;
				}
				return null;
			}

			/** native stage 坐标 → 世界坐标。 */
			private function worldPoint(stageX:Number, stageY:Number):Point
			{
				var p:Point = _render.screenToWorld(stageX, stageY);
				return p != null ? p : new Point(stageX, stageY);
			}

			/**
			 * 取可绘制对象的世界空间轴对齐包围盒。
			 * Starling DisplayObject.bounds 是"父坐标系"下的包围盒——元素显示对象
			 * 直接挂在渲染根容器（携带相机变换）下，父坐标系即世界空间，且已包含
			 * 位移/旋转/缩放/pivot，无需再乘任何矩阵。
			 */
			private static function worldBounds(d:Drawable):Rectangle
			{
				var b:Rectangle = d.bounds;
				if (b == null || b.width <= 0 || b.height <= 0) return null;
				return b;
			}

			private static function rectsIntersect(a:Rectangle, b:Rectangle):Boolean
			{
				return a.x < b.x + b.width && a.x + a.width > b.x &&
					   a.y < b.y + b.height && a.y + a.height > b.y;
			}
		}
	}
}
