package dream.engine.transform
{
    import dream.engine.ecs.DreamComponent;
    import dream.engine.ecs.Element;
    CONFIG::STUDIO
    {
        import dream.engine.ecs.FieldInfo;
    }

    import flash.geom.Matrix;
    import flash.geom.Point;

    /**
     * 变换组件：2D 空间位置、旋转、缩放。每个元素最多一个。
     * 支持父子层级：parent 变换影响子元素世界坐标。
     *
     * localToWorld 通过脏标记懒计算——字段变更时置脏，读取 worldMatrix 时才重算。
     * 渲染系统在每帧读取 worldMatrix 即可获得最新变换。
     *
     * 层级规则：
     *   - parent 必须也有 Transform 组件（由 setParent 校验）
     *   - 销毁元素时自动从父级解除挂载，子级 parent 置 null（不级联销毁）
     *   - worldMatrix = parent.worldMatrix × localMatrix
     */
    public final class Transform extends DreamComponent
    {
        // ── 本地变换字段 ──
        public var x:Number = 0;
        public var y:Number = 0;
        public var rotation:Number = 0;       // 弧度
        public var scaleX:Number = 1;
        public var scaleY:Number = 1;

        // ── 层级 ──
        private var _parent:Transform = null;
        private var _children:Vector.<Transform> = new Vector.<Transform>();

        // ── 世界矩阵（懒计算） ──
        private var _localDirty:Boolean = true;
        private var _worldDirty:Boolean = true;
        private var _localMatrix:Matrix = new Matrix();
        private var _worldMatrix:Matrix = new Matrix();

        public function Transform()
        {
        }

        // ── 层级 API ──

        public function get parent():Transform { return _parent; }

        public function get childCount():int { return _children.length; }

        public function getChildAt(i:int):Transform { return _children[i]; }

        /**
         * 递归查找名为 name 的后代 Transform（深度优先，不含自身）；无则 null。
         * 供动画轨道按子元素名定位目标元素（.dmclip 轨道 target 字段）。
         */
        public function findChildByName(name:String):Transform
        {
            if (name == null || name.length == 0) return null;
            for (var i:int = 0; i < _children.length; i++)
            {
                var c:Transform = _children[i];
                if (c == null) continue;
                if (c.owner != null && c.owner.name == name) return c;
                var d:Transform = c.findChildByName(name);
                if (d != null) return d;
            }
            return null;
        }

        /**
         * 按路径解析后代：路径以 "/" 分隔逐段匹配直接子级（如 "A/B/C"）；
         * 某段无直接同名时退化为递归同名查找（兼容仅记录元素名的旧轨道）。
         * 无匹配返回 null。
         */
        public function findChildByPath(path:String):Transform
        {
            if (path == null || path.length == 0) return null;
            var segments:Array = path.split("/");
            var node:Transform = this;
            for (var s:int = 0; s < segments.length; s++)
            {
                var seg:String = String(segments[s]);
                if (seg.length == 0) return null;
                var found:Transform = null;
                for (var i:int = 0; i < node._children.length; i++)
                {
                    var c:Transform = node._children[i];
                    if (c != null && c.owner != null && c.owner.name == seg) { found = c; break; }
                }
                if (found == null) found = node.findChildByName(seg);
                if (found == null) return null;
                node = found;
            }
            return node;
        }

        /**
         * 挂载到父级。parent=null 表示脱离层级成为根。
         * oldParent.children 自动移除 this，newParent.children 自动添加。
         */
        public function setParent(p:Transform, worldPositionStays:Boolean = true):void
        {
            if (p == _parent) return;
            if (p != null && p.owner == null) return;
            // 防止成环：p 不能是 this 的后代。
            if (p != null && isAncestorOf(p)) return;

            if (worldPositionStays)
            {
                // 保持世界坐标不变：记录旧世界坐标，重新挂载后反算 local。
                // 注意：worldMatrix getter 返回内部缓存引用，必须 clone 再修改，
                // 否则 invert/concat 会破坏被引用方的缓存。
                var oldWorld:Matrix = worldMatrix.clone();
                if (_parent != null) _parent._children.splice(_parent._children.indexOf(this), 1);
                _parent = p;
                if (p != null) p._children.push(this);
                var parentWorld:Matrix = p != null ? p.worldMatrix.clone() : new Matrix();
                parentWorld.invert();
                parentWorld.concat(oldWorld);
                // 反解出 local 字段
                x = parentWorld.tx;
                y = parentWorld.ty;
                scaleX = Math.sqrt(parentWorld.a * parentWorld.a + parentWorld.b * parentWorld.b);
                scaleY = Math.sqrt(parentWorld.c * parentWorld.c + parentMatrix_d(parentWorld));
                rotation = Math.atan2(parentWorld.b, parentWorld.a);
                _localDirty = true;
                markWorldDirty();
            }
            else
            {
                if (_parent != null) _parent._children.splice(_parent._children.indexOf(this), 1);
                _parent = p;
                if (p != null) p._children.push(this);
                markWorldDirty();
            }
            // 层级变化：祖先可用性影响本元素及其子树，重算 activeInHierarchy 并级联。
            if (owner != null) owner.refreshEnabled();
        }

        /** 计算矩阵的 d 分量，避免 Matrix 无 d 公开访问器时的歧义。 */
        private static function parentMatrix_d(m:Matrix):Number { return m.d; }

        /** this 是否是 candidate 的祖先（含自身）。用于 setParent 防环。 */
        public function isAncestorOf(candidate:Transform):Boolean
        {
            var t:Transform = candidate;
            while (t != null)
            {
                if (t == this) return true;
                t = t._parent;
            }
            return false;
        }

        // ── 矩阵访问 ──

        /** 本地变换矩阵（local → parent）。脏则重算。
         *  语义：M = T · R · S（先缩放，再旋转，再平移）。
         *  AS3 Matrix 的 translate/rotate/scale 是右乘（post-concat），
         *  按此顺序调用即得 T·R·S。 */
        public function get localMatrix():Matrix
        {
            if (_localDirty)
            {
                _localMatrix.identity();
                _localMatrix.scale(scaleX, scaleY);
                _localMatrix.rotate(rotation);
                _localMatrix.translate(x, y);
                _localDirty = false;
            }
            return _localMatrix;
        }

        /** 世界变换矩阵（local → world）。脏则级联重算。
         *  语义：world = parent · local。
         *  AS3 Matrix.concat(m) 是左乘（this = m × this），
         *  所以要得到 parent·local，需 this=local 然后 concat(parent)。 */
        public function get worldMatrix():Matrix
        {
            if (_worldDirty)
            {
                if (_parent != null)
                {
                    _worldMatrix = localMatrix.clone();
                    _worldMatrix.concat(_parent.worldMatrix);
                }
                else
                {
                    _worldMatrix = localMatrix.clone();
                }
                _worldDirty = false;
            }
            return _worldMatrix;
        }

        /** 字段变更后调用，标记本地与世界矩阵脏。 */
        public function setLocalDirty():void
        {
            _localDirty = true;
            markWorldDirty();
        }

        /** 标记自身及所有后代世界矩阵脏。 */
        public function markWorldDirty():void
        {
            _worldDirty = true;
            for each (var c:Transform in _children) c.markWorldDirty();
        }

        // ── 便捷变换 ──

        public function setPosition(x:Number, y:Number):void
        {
            this.x = x;
            this.y = y;
            setLocalDirty();
        }

        public function setRotation(rad:Number):void
        {
            this.rotation = rad;
            setLocalDirty();
        }

        public function setScale(sx:Number, sy:Number):void
        {
            this.scaleX = sx;
            this.scaleY = sy;
            setLocalDirty();
        }

        // ── 生命周期钩子 ──

        override protected function onDestroy():void
        {
            // 从父级解除挂载，子级 parent 置 null（不级联销毁，子级可能挂到别处）。
            if (_parent != null)
            {
                var idx:int = _parent._children.indexOf(this);
                if (idx >= 0) _parent._children.splice(idx, 1);
                _parent = null;
            }
            for each (var c:Transform in _children) c._parent = null;
            _children.length = 0;
        }

        // ── Inspector 反射 ──

        CONFIG::STUDIO
        override public function getInspectableFields():Array
        {
            return [
                new FieldInfo("position", "Position", "vector2", {x: x, y: y}),
                // rotation 以角度暴露（内部仍弧度）：Inspector/动画数据层统一用角度，更直观。
                new FieldInfo("rotation", "Rotation", "number", rotation * (180 / Math.PI)),
                new FieldInfo("scale", "Scale", "vector2", {x: scaleX, y: scaleY}),
            ];
        }

        override public function setFieldValue(fieldName:String, value:*):void
        {
            switch (fieldName)
            {
                case "position":
                    x = Number(value.x);
                    y = Number(value.y);
                    break;
                case "rotation":
                    // Inspector/动画数据层传入角度，转为内部弧度。
                    rotation = Number(value) * (Math.PI / 180);
                    break;
                case "scale":
                    scaleX = Number(value.x);
                    scaleY = Number(value.y);
                    break;
            }
            setLocalDirty();
        }
    }
}
