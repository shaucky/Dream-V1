package dream.engine.physics2d
{
    import Box2D.Common.Math.b2Vec2;
    import Box2D.Dynamics.b2Body;

    import dream.engine.ecs.DreamComponent;
    import dream.engine.transform.Transform;

    import flash.geom.Matrix;

    CONFIG::STUDIO
    {
        import dream.engine.ecs.FieldInfo;
    }

    /**
     * 刚体 2D 组件：赋予元素物理属性（Unity Rigidbody2D / Cocos RigidBody 风格）。
     * 每个元素至多一个；配合至少一个 Collider2D 才参与物理。
     *
     * 可编辑字段：
     *   bodyType        static | kinematic | dynamic（默认 dynamic）
     *   gravityScale    重力缩放（1 = 完全受重力）
     *   linearDamping / angularDamping   线性/角阻尼
     *   fixedRotation   锁定旋转
     *   allowSleep      允许休眠（静态后进入休眠省性能）
     *   bullet          连续碰撞检测（高速穿透防护）
     *   interpolation   none | interpolate | extrapolate（渲染帧插值平滑）
     *
     * 脚本 API：setVelocity / applyForce / applyImpulse / applyTorque，
     * 只读 velocityX / velocityY / mass。
     *
     * 脚本直接修改字段后调用 rebuild() 使系统重建底层刚体（Inspector 编辑自动生效）。
     * 底层 b2Body 不出现在任何公开 API 中。
     */
    public final class RigidBody2D extends DreamComponent
    {
        /** 同步判定容差。 */
        private static const SYNC_EPS:Number = 0.0001;

        // ── 可编辑字段 ──
        public var bodyType:String = "dynamic";       // "static" | "kinematic" | "dynamic"
        public var gravityScale:Number = 1;
        public var linearDamping:Number = 0;
        public var angularDamping:Number = 0;
        public var fixedRotation:Boolean = false;
        public var allowSleep:Boolean = true;
        public var bullet:Boolean = false;
        public var interpolation:String = "none";     // "none" | "interpolate" | "extrapolate"

        // ── 内部状态（PhysicsSystem2D 维护） ──
        internal var _dirty:Boolean = true;
        internal var _body:b2Body = null;

        // 物理帧位置（body 实际位置）与上次写回 Transform 的世界值。
        internal var _prevX:Number = 0, _prevY:Number = 0, _prevRot:Number = 0;
        internal var _currX:Number = 0, _currY:Number = 0, _currRot:Number = 0;
        internal var _lastWrittenX:Number = 0, _lastWrittenY:Number = 0, _lastWrittenRot:Number = 0;

        public function RigidBody2D()
        {
        }

        // ── 公开 API ──

        /** 脚本修改字段后调用，使系统重建底层刚体。 */
        public function rebuild():void
        {
            _dirty = true;
        }

        /** 设置线速度（世界单位/秒）。 */
        public function setVelocity(vx:Number, vy:Number):void
        {
            if (_body != null) _body.SetLinearVelocity(new b2Vec2(vx, vy));
        }

        /** 当前线速度 x 分量（世界单位/秒）。 */
        public function get velocityX():Number
        {
            return _body != null ? _body.GetLinearVelocity().x : 0;
        }

        /** 当前线速度 y 分量（世界单位/秒）。 */
        public function get velocityY():Number
        {
            return _body != null ? _body.GetLinearVelocity().y : 0;
        }

        /** 当前质量（由各碰撞器密度 × 面积合成）。 */
        public function get mass():Number
        {
            return _body != null ? _body.GetMass() : 0;
        }

        /** 在质心施加持续力（世界单位/秒² × 质量）。 */
        public function applyForce(fx:Number, fy:Number):void
        {
            if (_body != null && _body.GetType() == b2Body.b2_dynamicBody)
                _body.ApplyForce(new b2Vec2(fx, fy), _body.GetWorldCenter());
        }

        /** 在质心施加瞬时冲量。 */
        public function applyImpulse(ix:Number, iy:Number):void
        {
            if (_body != null && _body.GetType() == b2Body.b2_dynamicBody)
                _body.ApplyImpulse(new b2Vec2(ix, iy), _body.GetWorldCenter());
        }

        /** 施加角冲量（扭矩）。 */
        public function applyTorque(torque:Number):void
        {
            if (_body != null && _body.GetType() == b2Body.b2_dynamicBody)
                _body.ApplyTorque(torque);
        }

        /** 是否仍绑定底层刚体。 */
        public function get hasBody():Boolean { return _body != null; }

        // ── 包内 API（PhysicsSystem2D 驱动） ──

        /** 刚体创建后注入并初始化同步状态。 */
        internal function onBodyAttached(b:b2Body, wx:Number, wy:Number, wrot:Number):void
        {
            _body = b;
            _prevX = _currX = _lastWrittenX = wx;
            _prevY = _currY = _lastWrittenY = wy;
            _prevRot = _currRot = _lastWrittenRot = wrot;
        }

        /** 刚体销毁前调用。 */
        internal function onBodyDetached():void
        {
            _body = null;
        }

        /**
         * 步进前调用：把 Transform 世界变换写入刚体。
         * 仅当与上次写回值不同（外部编辑）时传送，避免覆盖物理运动。
         */
        internal function pushTransformToBody():void
        {
            if (_body == null || owner == null) return;
            var t:Transform = owner.getComponent(Transform) as Transform;
            if (t == null) return;

            var wx:Number, wy:Number, wrot:Number;
            if (t.parent == null)
            {
                wx = t.x; wy = t.y; wrot = t.rotation;
            }
            else
            {
                var m:Matrix = t.worldMatrix;
                wx = m.tx; wy = m.ty;
                wrot = Math.atan2(m.b, m.a);
            }
            if (Math.abs(wx - _lastWrittenX) > SYNC_EPS ||
                Math.abs(wy - _lastWrittenY) > SYNC_EPS ||
                Math.abs(wrot - _lastWrittenRot) > SYNC_EPS)
            {
                _body.SetPositionAndAngle(new b2Vec2(wx, wy), wrot);
                // 传送：物理帧位置同步为新位置，避免插值闪现。
                _prevX = _currX = wx; _prevY = _currY = wy; _prevRot = _currRot = wrot;
                _lastWrittenX = wx; _lastWrittenY = wy; _lastWrittenRot = wrot;
            }
        }

        /** 物理步进后调用：记录当前物理帧位置（prev = curr；curr = body 位置）。 */
        internal function captureBodyState():void
        {
            if (_body == null) return;
            _prevX = _currX; _prevY = _currY; _prevRot = _currRot;
            var pos:b2Vec2 = _body.GetPosition();
            _currX = pos.x; _currY = pos.y; _currRot = _body.GetAngle();
        }

        /**
         * 渲染同步调用：把（可能插值的）body 位置写回 Transform。
         * 仅 dynamic 且 interpolation != none 时插值；其余直接写当前物理帧位置。
         */
        internal function pullBodyToTransform(alpha:Number):void
        {
            if (_body == null || owner == null) return;
            var t:Transform = owner.getComponent(Transform) as Transform;
            if (t == null) return;

            var rx:Number = _currX, ry:Number = _currY, rrot:Number = _currRot;
            if (bodyType == "dynamic" && interpolation != "none")
            {
                var a:Number = alpha < 0 ? 0 : (alpha > 1 ? 1 : alpha);
                if (interpolation == "extrapolate")
                {
                    rx = _currX + (_currX - _prevX) * a;
                    ry = _currY + (_currY - _prevY) * a;
                    rrot = _currRot + (_currRot - _prevRot) * a;
                }
                else
                {
                    rx = _prevX + (_currX - _prevX) * a;
                    ry = _prevY + (_currY - _prevY) * a;
                    rrot = _prevRot + (_currRot - _prevRot) * a;
                }
            }

            writeWorldTransform(t, rx, ry, rrot);
            _lastWrittenX = rx; _lastWrittenY = ry; _lastWrittenRot = rrot;
        }

        // ── 私有工具 ──

        private static function typeFromString(t:String):uint
        {
            switch (t)
            {
                case "kinematic": return b2Body.b2_kinematicBody;
                case "dynamic":   return b2Body.b2_dynamicBody;
                default:          return b2Body.b2_staticBody;
            }
        }

        internal static function toBodyType(t:String):uint
        {
            return typeFromString(t);
        }

        /** 把世界位置/旋转写回 Transform（父级存在时转本地坐标）。 */
        private static function writeWorldTransform(t:Transform, wx:Number, wy:Number, wrot:Number):void
        {
            var lx:Number = wx, ly:Number = wy, lr:Number = wrot;
            if (t.parent != null)
            {
                var pw:Matrix = t.parent.worldMatrix.clone();
                var parentRot:Number = Math.atan2(pw.b, pw.a);
                pw.invert();
                lx = pw.a * wx + pw.c * wy + pw.tx;
                ly = pw.b * wx + pw.d * wy + pw.ty;
                lr = wrot - parentRot;
            }

            var dirty:Boolean = false;
            if (t.x != lx) { t.x = lx; dirty = true; }
            if (t.y != ly) { t.y = ly; dirty = true; }
            if (t.rotation != lr) { t.rotation = lr; dirty = true; }
            if (dirty) t.setLocalDirty();
        }

        // ── Inspector 反射 ──

        CONFIG::STUDIO
        override public function getInspectableFields():Array
        {
            var bt:FieldInfo = new FieldInfo("bodyType", "Body Type", "string", bodyType);
            bt.options = ["static", "kinematic", "dynamic"];
            var ip:FieldInfo = new FieldInfo("interpolation", "Interpolation", "string", interpolation);
            ip.options = ["none", "interpolate", "extrapolate"];
            return [
                bt,
                new FieldInfo("gravityScale", "Gravity Scale", "number", gravityScale),
                new FieldInfo("linearDamping", "Linear Damping", "number", linearDamping),
                new FieldInfo("angularDamping", "Angular Damping", "number", angularDamping),
                new FieldInfo("fixedRotation", "Fixed Rotation", "boolean", fixedRotation),
                new FieldInfo("allowSleep", "Allow Sleep", "boolean", allowSleep),
                new FieldInfo("bullet", "Bullet", "boolean", bullet),
                ip,
            ];
        }

        override public function setFieldValue(fieldName:String, value:*):void
        {
            switch (fieldName)
            {
                case "bodyType":
                    bodyType = String(value);
                    break;
                case "gravityScale":
                    gravityScale = Number(value);
                    break;
                case "linearDamping":
                    linearDamping = Number(value);
                    break;
                case "angularDamping":
                    angularDamping = Number(value);
                    break;
                case "fixedRotation":
                    fixedRotation = Boolean(value);
                    break;
                case "allowSleep":
                    allowSleep = Boolean(value);
                    break;
                case "bullet":
                    bullet = Boolean(value);
                    break;
                case "interpolation":
                    interpolation = String(value);
                    break;
                default:
                    return;
            }
            _dirty = true;
        }
    }
}
