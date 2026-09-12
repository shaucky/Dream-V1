package game
{
    import dream.engine.ecs.DreamComponent;
    import dream.engine.transform.Transform;

    /**
     * Flappy 鸟：重力 + 跳跃 + 俯仰展示。
     * 位置由宿主元素 Transform 承载；显示与碰撞盒参数由 FlappyGame 构建。
     *
     * 三种运动状态：
     *   hovering（READY）：不受重力，原地小幅浮动等待起飞；
     *   alive（PLAYING）：重力下落，flap 起跳，随竖直速度俯仰；
     *   !alive（坠落）：撞管后只受重力，转至 90° 贴地为止。
     */
    public final class Bird extends DreamComponent
    {
        /** 竖直速度（世界单位/秒，y 向下为正）。 */
        public var velocity:Number = 0;

        /** 存活标志：死亡后不再接受跳跃，仅坠落。 */
        public var alive:Boolean = true;

        /** READY 悬浮模式：不受重力，原地摆动。 */
        public var hovering:Boolean = true;

        /** 碰撞半宽/半高（略小于显示尺寸，手感宽容）。 */
        public var halfW:Number = 0.3;
        public var halfH:Number = 0.22;

        // ── 运动参数（由 FlappyGame 从场景配置注入） ──
        internal var gravity:Number = 16;
        internal var jumpVelocity:Number = -5.2;
        internal var maxFallSpeed:Number = 8.5;

        private var _hoverTime:Number = 0;

        /** 向上跳（仅存活且非悬浮时有效）。 */
        public function flap():void
        {
            if (!alive) return;
            velocity = jumpVelocity;
        }

        /** 立即停止下落（贴地/卡死保护用）。 */
        public function stop():void
        {
            velocity = 0;
        }

        override protected function onEnterFrame(dt:Number):void
        {
            var t:Transform = owner.getComponent(Transform) as Transform;
            if (t == null) return;

            if (hovering)
            {
                _hoverTime += dt;
                t.y = Math.sin(_hoverTime * 3.4) * 0.14;
                t.rotation = Math.sin(_hoverTime * 3.4 + 0.6) * 0.08;
                t.setLocalDirty(); // Transform 字段是普通变量，改后须标脏，否则渲染读不到新矩阵
                velocity = 0;
                return;
            }

            velocity += gravity * dt;
            if (velocity > maxFallSpeed) velocity = maxFallSpeed;
            t.y += velocity * dt;

            if (alive)
            {
                // 俯仰：上仰 -0.42rad，随下落速度渐转俯冲 1.5rad。
                var target:Number = velocity <= 0
                    ? -0.42
                    : -0.42 + (0.42 + 1.5) * Math.min(1, velocity / maxFallSpeed);
                t.rotation += (target - t.rotation) * Math.min(1, dt * 9);
            }
            else
            {
                // 坠落：转向 90°，贴地停住。
                t.rotation = Math.min(Math.PI * 0.5, t.rotation + 7 * dt);
                var groundY:Number = FlappyGame.GroundY - halfH;
                if (t.y > groundY)
                {
                    t.y = groundY;
                    velocity = 0;
                }
            }

            t.setLocalDirty(); // 同上：变更后标脏，驱动渲染矩阵刷新
        }
    }
}
