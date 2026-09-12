package dream.engine.physics2d
{
    /**
     * 物理 2D 静态门面：引擎用户接触物理的唯一入口（Unity Physics2D 风格）。
     *
     * 提供：
     *   - 全局重力（setGravity / gravityX / gravityY）
     *   - 固定物理步长（fixedDeltaTime）与求解迭代次数
     *   - 射线检测（raycast）
     *
     * 内部持有单例 PhysicsWorld2D（由 PhysicsSystem2D 构造时确保创建）。
     * 第三方物理库（Box2D）的类型绝不在此类或任何公开 API 中出现。
     */
    public final class Physics2D
    {
        private static var _world:PhysicsWorld2D = null;

        private static var _fixedDeltaTime:Number = 1 / 60;
        private static var _velocityIterations:int = 8;
        private static var _positionIterations:int = 3;

        /** 由 PhysicsSystem2D 构造时调用，确保物理世界存在。 */
        internal static function ensureWorld():void
        {
            if (_world == null) _world = new PhysicsWorld2D();
        }

        internal static function get world():PhysicsWorld2D
        {
            return _world;
        }

        // ── 重力 ──

        /** 当前重力 x 分量（世界单位/秒²）。 */
        public static function get gravityX():Number
        {
            return _world != null ? _world.gravityX : 0;
        }

        /** 当前重力 y 分量（世界单位/秒²）。默认 +10 指向屏幕下方（与渲染 y 轴一致）。 */
        public static function get gravityY():Number
        {
            return _world != null ? _world.gravityY : 10;
        }

        /** 设置全局重力向量（世界单位/秒²）。 */
        public static function setGravity(x:Number, y:Number):void
        {
            ensureWorld();
            _world.setGravity(x, y);
        }

        // ── 求解配置 ──

        /** 固定物理步长（秒）。渲染帧按此分片步进。默认 1/60。 */
        public static function get fixedDeltaTime():Number { return _fixedDeltaTime; }
        public static function set fixedDeltaTime(v:Number):void
        {
            _fixedDeltaTime = (v > 0 && isFinite(v)) ? v : (1 / 60);
        }

        /** 速度求解迭代次数（越大越稳、越慢）。默认 8。 */
        public static function get velocityIterations():int { return _velocityIterations; }
        public static function set velocityIterations(v:int):void
        {
            _velocityIterations = v > 0 ? v : 1;
        }

        /** 位置求解迭代次数。默认 3。 */
        public static function get positionIterations():int { return _positionIterations; }
        public static function set positionIterations(v:int):void
        {
            _positionIterations = v > 0 ? v : 1;
        }

        // ── 射线检测 ──

        /**
         * 世界空间射线检测：返回从 (x1,y1) 到 (x2,y2) 的最近命中。
         * 命中返回 RaycastHit2D（含点/法线/距离/碰撞器引用）；未命中返回 null。
         */
        public static function raycast(x1:Number, y1:Number, x2:Number, y2:Number):RaycastHit2D
        {
            ensureWorld();
            return _world.raycastClosest(x1, y1, x2, y2);
        }
    }
}
