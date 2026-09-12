package dream.engine.ecs
{
	import flash.utils.Dictionary;
	import flash.utils.describeType;
	import flash.utils.getQualifiedClassName;

	/**
	 * 组件工厂：按短类名创建组件实例。
	 *
	 * 两个用途，故不设 CONFIG 门控（发布构建同样存在）：
	 *   1. 编辑器 Inspector 的 "Add Component" 取值来源（listNames）；
	 *   2. 场景反序列化按类型名重建组件（SceneSerializer.createComponent）。
	 *
	 * 注册项为 短类名 → 工厂函数，工厂函数无参返回 DreamComponent 实例。
	 *
	 * 反射策略：
	 *   组件候选列表由 Dream Studio 编译前扫描源码自动生成的 ComponentIndex.ALL 提供，
	 *   引用所有 DreamComponent 子类（触发类链接）。ensureInitialized 遍历候选，
	 *   通过 describeType 检查构造函数参数，自动注册无参构造且非 EXCLUDED 的子类。
	 *   新增组件只需在源码中编写类并继承 DreamComponent，重新编译即自动出现在列表中，
	 *   无需手写注册调用。
	 *
	 * 使用：
	 *   var c:DreamComponent = ComponentFactory.create("Rotator");
	 *   var names:Array = ComponentFactory.listNames();
	 */
	public final class ComponentFactory
	{
		private static var _registry:Dictionary = new Dictionary();
		private static var _types:Dictionary = new Dictionary();
		private static var _names:Array = [];
		private static var _initialized:Boolean = false;

		/**
		 * 不允许通过 Add Component 添加的组件。
		 * Transform 由 HierarchyCommandHandler 创建元素时自动添加，
		 * 重复添加会覆盖已有 Transform 导致层级关系丢失，故排除。
		 * Collider2D 为抽象基类（无几何），应添加 BoxCollider2D/CircleCollider2D。
		 * UIDrawable 为 UI 可绘制组件基类（自身不创建 drawable），应添加 Image/Text。
		 */
		private static const EXCLUDED:Array = ["Transform", "Collider2D", "UIDrawable"];

		/** 注册一个可创建的组件类型。name 为短类名，factory 返回新实例，cls 为组件 Class。 */
		public static function register(name:String, factory:Function, cls:Class):void
		{
			if (_registry[name] == null) _names.push(name);
			_registry[name] = factory;
			_types[name] = cls;
		}

		/** 按短类名创建组件实例；未注册返回 null。 */
		public static function create(name:String):DreamComponent
		{
			ensureInitialized();
			var f:Function = _registry[name];
			if (f == null) return null;
			return f() as DreamComponent;
		}

		/** 按短类名返回组件 Class；未注册返回 null。供 hasComponent 判重使用。 */
		public static function getType(name:String):Class
		{
			ensureInitialized();
			return _types[name] as Class;
		}

		/** 返回所有已注册组件短类名的快照（按注册顺序）。 */
		public static function listNames():Array
		{
			ensureInitialized();
			return _names.slice();
		}

		/**
		 * 反射注册可添加组件。首次调用 listNames/create 时自动触发。
		 * 遍历 ComponentIndex.ALL（编译前生成的所有 DreamComponent 子类引用），
		 * 用 describeType 检查构造函数参数：
		 *   - 无参构造（或参数全部 optional）且不在 EXCLUDED 列表 → 自动注册
		 *   - 否则跳过
		 */
		private static function ensureInitialized():void
		{
			if (_initialized) return;
			_initialized = true;
			for each (var cls:Class in ComponentIndex.ALL)
			{
				var shortName:String = shortClassName(cls);
				if (EXCLUDED.indexOf(shortName) >= 0) continue;
				if (!hasParameterlessConstructor(cls)) continue;
				register(shortName, (function(c:Class):Function {
					return function():DreamComponent { return new c() as DreamComponent; };
				})(cls), cls);
			}
		}

		/**
		 * 用 describeType 检查 cls 是否有可无参调用的构造函数。
		 * AS3 构造函数参数全部有默认值（optional=true）时也可无参 new。
		 */
		private static function hasParameterlessConstructor(cls:Class):Boolean
		{
			var desc:XML = describeType(cls);
			var constructor:XML = desc.factory.constructor[0];
			if (constructor == null) return true; // 无显式构造函数
			for each (var p:XML in constructor.parameter)
			{
				if (p.@optional != "true") return false;
			}
			return true;
		}

		private static function shortClassName(cls:Class):String
		{
			var full:String = getQualifiedClassName(cls);
			var colonIdx:int = full.lastIndexOf("::");
			var dotIdx:int = full.lastIndexOf(".");
			if (colonIdx >= 0) return full.substr(colonIdx + 2);
			if (dotIdx >= 0) return full.substr(dotIdx + 1);
			return full;
		}
	}
}
