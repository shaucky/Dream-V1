package dream.engine.comm
{
	CONFIG::STUDIO
	{
		import dream.engine.ecs.World;
		import dream.engine.render.RenderEngine;
		import dream.engine.scene.AudioGizmo;
		import dream.engine.scene.SceneSelection;
		import dream.engine.scene.TransformGizmo;

		/**
		 * 引擎运行控制处理器：设置 world.running 标志。
		 *
		 * 收到 engine.setRunning 时：
		 *   running=true  → 恢复组件运行时生命周期（start/onEnterFrame/onExitFrame）
		 *   running=false → 暂停逻辑刷新，仅保留系统更新与销毁清理
		 *
		 * 不重启 ADL 进程，仅切换逻辑刷新开关。由 Studio 暂停按钮触发。
		 *
		 * payload 格式：
		 *   {running:true}
		 */
		public final class EngineSetRunningHandler implements IMessageHandler
		{
			private var _world:World;
			private var _selection:SceneSelection;
			private var _gizmo:TransformGizmo;
			private var _render:RenderEngine;
			private var _audioGizmo:AudioGizmo;

			public function EngineSetRunningHandler(world:World, selection:SceneSelection = null,
				gizmo:TransformGizmo = null, render:RenderEngine = null, audioGizmo:AudioGizmo = null)
			{
				_world = world;
				_selection = selection;
				_gizmo = gizmo;
				_render = render;
				_audioGizmo = audioGizmo;
			}

			public function get messageType():String
			{
				return MessageTypes.EngineSetRunning;
			}

			public function handle(message:Message, channel:IChannel):void
			{
				var p:Object = message.payload;
				if (p == null) return;
				_world.running = Boolean(p.running);

				// 编辑器 overlay（选中高亮 / Gizmo 手柄 / 音频范围预览）只在编辑模式显示；
				// 视口导航（滚轮缩放/平移/点击拾取）同样仅在编辑模式启用。
				if (_selection != null) _selection.setSelectionVisible(!_world.running);
				if (_gizmo != null) _gizmo.setVisible(!_world.running);
				if (_audioGizmo != null) _audioGizmo.setVisible(!_world.running);
				if (_render != null) _render.viewportNavigationEnabled = !_world.running;
			}
		}
	}
}
