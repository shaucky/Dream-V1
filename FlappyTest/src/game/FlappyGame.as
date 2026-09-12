package game
{
    import dream.engine.ecs.DreamComponent;
    import dream.engine.ecs.Element;
    import dream.engine.input.InputKey;
    import dream.engine.input.InputManager;
    import dream.engine.scene.SceneSerializer;
    import dream.engine.transform.Transform;
    import dream.engine.ui.Text;

    CONFIG::STUDIO
    {
        import dream.engine.ecs.FieldInfo;
    }

    /**
     * Flappy 总控（预制体版）：一组管道是 Pipe.prefab 资产，本组件在运行期按
     * pipePrefabGuid 实例化 PipeCount 组、池化复用（回收即重定位实例根），
     * 自身只做状态机（READY → PLAYING → OVER）、输入、滚动、计分与碰撞。
     *
     * 场景约定（挂本组件元素的子树，按名字查找）：
     *   "Bird"                              鸟（Transform + 显示 + game.Bird）
     *   "Pipes"                             管道池容器（空节点，生成的实例挂到它下面）
     *   "Score" / "Title" / "Hint"          UI 文本（Text 组件，位于屏幕空间 Canvas 下）
     *
     * 管道预制体约定（Pipe.prefab）：顶层根节点 + 四个子节点
     *   PipeTop / PipeCapTop / PipeBottom / PipeCapBottom。
     * 缺口高度由 pipeGap 在运行期重新计算子级偏移（预制体里烘的是默认缺口），
     * 因此调整 pipeGap 不需要改资产；实例根只承载"整组管道的摆放"（x + 缺口中心 y）。
     *
     * 游戏参数（重力、跳跃、管道速度/缺口/间距、管道预制体）可经 Inspector 编辑并随场景序列化。
     */
    public final class FlappyGame extends DreamComponent
    {
        /** 地面（草顶）世界 y，与场景 Grass 元素对齐。 */
        public static const GroundY:Number = 4.1;

        // ── 场景布局常量（与场景/素材对应） ──
        private static const PipeWidth:Number = 1.3;
        private static const CapWidth:Number = 1.55;
        private static const CapHeight:Number = 0.74;
        private static const PipeBodyHeight:Number = 12;
        private static const SpawnX:Number = 5.4;
        private static const PipeCount:int = 4;
        private static const GapMinY:Number = -1.7;
        private static const GapMaxY:Number = 1.7;
        private static const DeathDelay:Number = 0.5;

        // ── 可调参数（Inspector / 场景序列化） ──
        /** 鸟的下落重力（世界单位/秒²）。 */
        public var gravity:Number = 16;
        /** 跳跃初速（负 = 向上）。 */
        public var jumpVelocity:Number = -5.2;
        /** 最大下落速度。 */
        public var maxFallSpeed:Number = 8.5;
        /** 鸟固定的世界 x。 */
        public var birdX:Number = -1.6;
        /** 管道水平速度。 */
        public var pipeSpeed:Number = 2.4;
        /** 管道缺口高度。 */
        public var pipeGap:Number = 2.9;
        /** 相邻管道的水平间距。 */
        public var pipeSpacing:Number = 3.9;
        /** 管道预制体资源 GUID：运行期据此 spawn 出 PipeCount 组管道。 */
        public var pipePrefabGuid:String = "";

        private static const StateReady:int = 0;
        private static const StatePlaying:int = 1;
        private static const StateOver:int = 2;

        private var _state:int = StateReady;
        private var _score:int = 0;
        private var _overDelay:Number = 0;

        private var _bird:Bird = null;
        private var _birdT:Transform = null;
        // 每项 { rootT, topT, capTopT, botT, capBotT, x, gapY, scored }；
        // rootT 是预制体实例的顶层节点（一组管道的摆放节点）。
        private var _pipes:Array = [];
        private var _scoreText:Text = null;
        private var _titleText:Text = null;
        private var _hintText:Text = null;
        private var _titleEl:Element = null;
        private var _hintEl:Element = null;

        // ── 生命周期 ──

        override protected function start():void
        {
            var rootT:Transform = owner.getComponent(Transform) as Transform;
            if (rootT == null) return;

            var birdEl:Element = findChild(rootT, "Bird");
            if (birdEl != null)
            {
                _bird = birdEl.getComponent(Bird) as Bird;
                _birdT = birdEl.getComponent(Transform) as Transform;
            }

            var pipesEl:Element = findChild(rootT, "Pipes");
            if (pipesEl != null) spawnPipes(pipesEl);

            _scoreText = textOf(findChild(rootT, "Score"));
            _titleText = textOf(findChild(rootT, "Title"));
            _hintText = textOf(findChild(rootT, "Hint"));
            _titleEl = findChild(rootT, "Title");
            _hintEl = findChild(rootT, "Hint");

            if (_bird != null)
            {
                _bird.gravity = gravity;
                _bird.jumpVelocity = jumpVelocity;
                _bird.maxFallSpeed = maxFallSpeed;
            }
            if (_birdT != null) _birdT.setPosition(birdX, _birdT.y);

            resetGame();
        }

        override protected function onEnterFrame(dt:Number):void
        {
            var pressed:Boolean = InputManager.current != null &&
                (InputManager.current.isKeyPressed(InputKey.Space) ||
                 InputManager.current.isMousePressed(InputManager.MouseButton.LEFT));

            if (_state == StateReady)
            {
                if (pressed && _bird != null)
                {
                    _state = StatePlaying;
                    _bird.hovering = false;
                    _bird.flap();
                    Sfx.playWing();
                    setUiVisible(false);
                }
            }
            else if (_state == StatePlaying)
            {
                movePipes(dt);
                updateScore();
                if (checkCollision())
                {
                    die();
                }
                else if (pressed && _bird != null)
                {
                    _bird.flap();
                    Sfx.playWing();
                }
            }
            else // StateOver
            {
                _overDelay += dt;
                if (_overDelay >= DeathDelay && pressed) resetGame();
            }
        }

        // ── 子树查找 ──

        /** 在 t 的子树（含 t 自身）按名字查找元素。 */
        private static function findChild(t:Transform, name:String):Element
        {
            if (t == null || t.owner != null && t.owner.name == name) return t.owner;
            for (var i:int = 0; i < t.childCount; i++)
            {
                var hit:Element = findChild(t.getChildAt(i), name);
                if (hit != null) return hit;
            }
            return null;
        }

        /** 在 t 的直接子级中按名字查找 Transform（预制体内子节点的定位用）。 */
        private static function childOf(t:Transform, name:String):Transform
        {
            if (t == null) return null;
            for (var i:int = 0; i < t.childCount; i++)
            {
                var c:Transform = t.getChildAt(i);
                if (c != null && c.owner != null && c.owner.name == name) return c;
            }
            return null;
        }

        private static function textOf(e:Element):Text
        {
            return e == null ? null : e.getComponent(Text) as Text;
        }

        // ── 管道生成（预制体） ──

        /**
         * 运行期按 pipePrefabGuid spawn PipeCount 组管道，挂在 container 下。
         *
         * spawnPrefab 是异步安全的：引擎启动时 resource.index 往往还没到（场景先加载、
         * 索引后推送），请求会排队，索引到位后自动实例化。这里只在开局 spawn 一次，
         * 之后全靠池化重定位，不再读盘。
         *
         * 每一组各自在回调里入池并摆好位置——回调可能晚于 resetGame 到达，
         * 不能指望 resetGame 替它们定位。任一组失败只跳过该组并 trace，
         * 缺资产时游戏仍能跑起来（只是没有管道），便于定位问题。
         */
        private function spawnPipes(container:Element):void
        {
            _pipes.length = 0;

            if (SceneSerializer.current == null)
            {
                trace("[flappy] 序列化器未就绪，跳过管道生成");
                return;
            }
            if (pipePrefabGuid == null || pipePrefabGuid.length == 0)
            {
                trace("[flappy] 未指定管道预制体：把 Pipe.prefab 拖到 Inspector 的 Pipe Prefab 字段");
                return;
            }
            for (var i:int = 0; i < PipeCount; i++)
                spawnPipeAt(container.id, i);
        }

        /**
         * 生成第 slot 组管道并入池。slot 作为参数传入而不是直接捕获循环变量：
         * AS3 的 var 是函数作用域，循环里的闭包会共享同一个变量，异步回调读到的会是末值。
         */
        private function spawnPipeAt(containerId:int, slot:int):void
        {
            var serializer:SceneSerializer = SceneSerializer.current;
            serializer.spawnPrefab(pipePrefabGuid, containerId, function(rootId:int):void
            {
                if (rootId < 0)
                {
                    trace("[flappy] 管道预制体实例化失败（第 " + slot + " 组），guid=" + pipePrefabGuid);
                    return;
                }
                var pipEl:Element = serializer.findElement(rootId);
                var rootT:Transform = pipEl != null ? pipEl.getComponent(Transform) as Transform : null;
                var topT:Transform = childOf(rootT, "PipeTop");
                var botT:Transform = childOf(rootT, "PipeBottom");
                if (rootT == null || topT == null || botT == null)
                {
                    trace("[flappy] 管道预制体缺少 Pipe/PipeTop/PipeBottom 结构（第 " + slot + " 组）");
                    return;
                }
                var entry:Object = {
                    rootT: rootT,
                    topT: topT,
                    capTopT: childOf(rootT, "PipeCapTop"),
                    botT: botT,
                    capBotT: childOf(rootT, "PipeCapBottom"),
                    x: 0, gapY: 0, scored: false
                };
                _pipes.push(entry);
                positionPipe(entry, SpawnX + slot * pipeSpacing);
            });
        }

        // ── 游戏逻辑 ──

        /** 重开一局：重置鸟、管道队列与 UI，回到 READY。 */
        private function resetGame():void
        {
            _state = StateReady;
            _score = 0;
            _overDelay = 0;
            if (_scoreText != null) _scoreText.setFieldValue("text", "0");

            if (_bird != null)
            {
                _bird.alive = true;
                _bird.hovering = true;
                _bird.velocity = 0;
            }
            if (_birdT != null)
            {
                // Transform 字段是普通变量，改后须标脏/走 setter，渲染矩阵才会刷新。
                _birdT.setPosition(birdX, 0);
                _birdT.setRotation(0);
            }

            for (var i:int = 0; i < _pipes.length; i++)
                positionPipe(_pipes[i], SpawnX + i * pipeSpacing);

            if (_titleText != null) _titleText.setFieldValue("text", "FLAPPY DREAM");
            if (_hintText != null) _hintText.setFieldValue("text", "点击 / 空格 起飞");
            setUiVisible(true);
        }

        /**
         * 把一组管道重定位到 x，并随机新的缺口中心。
         * 实例根承载摆放（x + 缺口中心 y）；四个子节点相对根的偏移由 pipeGap 现算——
         * 预制体里烘的是默认缺口，改 pipeGap 不必改资产。
         */
        private function positionPipe(p:Object, x:Number):void
        {
            p.x = x;
            p.gapY = GapMinY + Math.random() * (GapMaxY - GapMinY);
            p.scored = false;

            var halfGap:Number = pipeGap * 0.5;
            p.rootT.setPosition(x, p.gapY);
            p.topT.setPosition(0, -(halfGap + PipeBodyHeight * 0.5));
            p.botT.setPosition(0, halfGap + PipeBodyHeight * 0.5);
            if (p.capTopT != null)
                p.capTopT.setPosition(0, -(halfGap + CapHeight * 0.5));
            if (p.capBotT != null)
                p.capBotT.setPosition(0, halfGap + CapHeight * 0.5);
        }

        /** 滚动管道：移动实例根即可带走整组；出屏（最左）的组回收到队尾（最右 + 间距）。 */
        private function movePipes(dt:Number):void
        {
            // 队尾（最右）组的 x；回收组排到它后面。
            var maxX:Number = -Number.MAX_VALUE;
            for each (var p:Object in _pipes)
                if (p.x > maxX) maxX = p.x;

            for each (var q:Object in _pipes)
            {
                q.x -= pipeSpeed * dt;
                if (q.x < -SpawnX - 1)
                {
                    positionPipe(q, maxX + pipeSpacing);
                    maxX += pipeSpacing; // 同帧多组回收时依次排到队尾，不重叠
                }
                else
                {
                    q.rootT.setPosition(q.x, q.gapY);
                }
            }
        }

        /** 鸟越过管道（管右缘过鸟身）记 1 分。 */
        private function updateScore():void
        {
            for each (var p:Object in _pipes)
            {
                if (p.scored) continue;
                if (p.x + PipeWidth * 0.5 < birdX - 0.2)
                {
                    p.scored = true;
                    _score++;
                    if (_scoreText != null) _scoreText.setFieldValue("text", String(_score));
                    Sfx.playPoint();
                }
            }
        }

        /** AABB：鸟 vs 管道/地面/顶棚。 */
        private function checkCollision():Boolean
        {
            if (_bird == null || _birdT == null) return false;
            var hw:Number = _bird.halfW;
            var hh:Number = _bird.halfH;

            if (_birdT.y + hh > GroundY) return true;
            if (_birdT.y - hh < -5.4) return true;

            for each (var p:Object in _pipes)
            {
                if (Math.abs(p.x - birdX) < hw + PipeWidth * 0.5)
                {
                    var gapTop:Number = p.gapY - pipeGap * 0.5 + 0.02;
                    var gapBot:Number = p.gapY + pipeGap * 0.5 - 0.02;
                    if (_birdT.y - hh < gapTop || _birdT.y + hh > gapBot) return true;
                }
            }
            return false;
        }

        private function die():void
        {
            _state = StateOver;
            _overDelay = 0;
            if (_bird != null) _bird.alive = false;
            Sfx.playHit();
            if (_titleText != null) _titleText.setFieldValue("text", "GAME OVER");
            if (_hintText != null) _hintText.setFieldValue("text",
                "得分 " + _score + " · 按空格重来");
            setUiVisible(true);
        }

        /** READY/OVER 显示标题与提示，PLAYING 隐藏。 */
        private function setUiVisible(v:Boolean):void
        {
            if (_titleEl != null) _titleEl.enabled = v;
            if (_hintEl != null) _hintEl.enabled = v;
        }

        // ── Inspector 反射（可调参数随场景序列化） ──

        CONFIG::STUDIO
        override public function getInspectableFields():Array
        {
            return [
                new FieldInfo("gravity", "重力", "number", gravity),
                new FieldInfo("jumpVelocity", "跳跃速度", "number", jumpVelocity),
                new FieldInfo("maxFallSpeed", "最大落速", "number", maxFallSpeed),
                new FieldInfo("birdX", "鸟的X", "number", birdX),
                new FieldInfo("pipeSpeed", "管道速度", "number", pipeSpeed),
                new FieldInfo("pipeGap", "管道缺口", "number", pipeGap),
                new FieldInfo("pipeSpacing", "管道间距", "number", pipeSpacing),
                new FieldInfo("pipePrefabGuid", "管道预制体", "resource", pipePrefabGuid),
            ];
        }

        override public function setFieldValue(fieldName:String, value:*):void
        {
            if (value == null) return;
            switch (fieldName)
            {
                case "gravity": gravity = Number(value); break;
                case "jumpVelocity": jumpVelocity = Number(value); break;
                case "maxFallSpeed": maxFallSpeed = Number(value); break;
                case "birdX": birdX = Number(value); break;
                case "pipeSpeed": pipeSpeed = Number(value); break;
                case "pipeGap": pipeGap = Number(value); break;
                case "pipeSpacing": pipeSpacing = Number(value); break;
                case "pipePrefabGuid": pipePrefabGuid = String(value); break;
            }
            if (_bird != null)
            {
                _bird.gravity = gravity;
                _bird.jumpVelocity = jumpVelocity;
                _bird.maxFallSpeed = maxFallSpeed;
            }
            if (_birdT != null && fieldName == "birdX") _birdT.setPosition(birdX, _birdT.y);
        }
    }
}
