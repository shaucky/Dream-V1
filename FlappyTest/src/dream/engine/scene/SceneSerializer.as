package dream.engine.scene
{
    import dream.engine.ecs.ComponentFactory;
    import dream.engine.ecs.DreamComponent;
    import dream.engine.ecs.Element;
    import dream.engine.ecs.World;
    import dream.engine.render.DisplayComponent;
    import dream.engine.render.Drawable;
    import dream.engine.render.RenderEngine;
    import dream.engine.render.ResourceManager;
    import dream.engine.transform.Transform;

    CONFIG::STUDIO
    {
        import dream.engine.ecs.FieldInfo;
        import flash.utils.Dictionary;
        import flash.utils.getQualifiedClassName;
    }

    /**
     * 场景序列化器：将 World 序列化为 JSON 对象 / 从 JSON 对象重建 World。
     *
     * 门控：反序列化与组件重建不设 CONFIG 门控——发布构建同样要按 .space 加载启动场景；
     * 序列化（serialize）与子树克隆（cloneSubtree）依赖 Inspector 反射字段
     * （FieldInfo / getInspectableFields / Element.getComponents），仅编辑器需要，保持门控。
     *
     * 存档格式（.space，JSON）：
     * {
     *   "elements": [
     *     {
     *       "id": 7,               // 元素 ID，原样恢复，保证撤销/重做前后 ID 稳定
     *       "name": "Element",
     *       "parentId": -1,        // elements 数组下标（不是元素 ID）
     *       "enabled": true,
     *       "components": [
     *         { "type": "Transform", "fields": { "position": {"x":0,"y":0}, ... } },
     *         { "type": "DisplayComponent", "fields": { "color":4825504 } },
     *         { "type": "Rotator", "fields": { "speed":1.0 } }
     *       ]
     *     }
     *   ]
     * }
     *
     * 序列化：遍历元素，用 getInspectableFields() 读取字段值。
     * 反序列化：
     *   1. 清空当前 World
     *   2. 按 elements 数组重建元素（沿用存档里的 id，缺失才分配新 ID）
     *   3. 每个元素按 components 数组重建组件：
     *      - Transform：直接 new Transform()
     *      - DisplayComponent：用 RenderEngine.createQuad 创建 Drawable 再构造
     *      - 其他：用 ComponentFactory.create 创建
     *   4. 用 setFieldValue() 恢复字段值
     *   5. 恢复父子层级（按 parentId 索引）
     *
     * prefab 实例元数据（仅编辑器，见 instantiate / serialize / syncPrefabSources）：
     *   实例在存档里是「展开副本 + 溯源」——元素仍是普通元素（发布构建照常渲染），
     *   额外两处元数据记住它来自哪个 prefab：
     *     顶层  prefabInstances: [ { "instanceId": 1, "guid": "<prefab 资产 GUID>" } ]
     *     元素  prefab: { "instanceId": 1, "sourceId": 3,
     *                     "overrides": ["Transform.position", "DisplayComponent", "!AudioSource"] }
     *           sourceId = prefab 文件里 elements 的下标
     *   没有 prefab 元素的普通场景不产生这两个字段，存档格式与旧版完全一致。
     *
     * override（引用 + override 模型，仅编辑器）：
     *   实例是源 prefab 的展开副本。在实例上改过的内容记进 overrides，
     *   源改动传播（syncInstances）时这些键被跳过——即"实例的本地修改优先"。
     *   override 键的形式：
     *     "Component.field"  字段值被改过（如 "Transform.position"）
     *     "Component"        组件是用户在实例上自行添加的（同步时不删、不改）
     *     "!Component"       组件被用户从实例上移除（同步时不补回）
     *     "name" / "enabled" 元素级名称/启用被改过
     */
    public final class SceneSerializer
    {
        /**
         * 当前世界使用的序列化器。SceneSerializer 与 Element/World 不同，没有天然的
         * "current" 语义，这里按 ResourceManager.current / InputManager.current 的既有惯例
         * 暴露一个服务定位器：一个 World 配一个序列化器（编辑器是 DreamEngine._serializer，
         * 发布构建是启动时惰性创建的 _runtimeSerializer）。
         *
         * 两类调用方：
         *   - 编辑器专用模块（HierarchyBridge / InspectorShared / 文档 overlay）读 prefab 溯源信息，
         *     逐个传引用会污染一大片签名；
         *   - 游戏代码在运行期 spawn 预制体（见 spawnPrefab），发布构建同样需要，
         *     故本字段不设门控。
         */
        public static var current:SceneSerializer;

        private var _world:World;
        private var _renderEngine:RenderEngine;

        // 实例溯源：Element → {instanceId, sourceId}。放在序列化器上而不是 Element 上——
        // 发布构建完全不感知 prefab，Element 也不必背编辑器专用字段。
        CONFIG::STUDIO
        private var _prefabMeta:Dictionary = new Dictionary();

        // 场景内出现过的实例：{instanceId, guid}，写回存档时落到顶层 prefabInstances。
        CONFIG::STUDIO
        private var _prefabInstances:Array = [];

        // 下一个实例 ID；从存档读入时抬到已有最大值之上，避免重开后撞号。
        CONFIG::STUDIO
        private var _nextPrefabInstanceId:int = 1;

        public function SceneSerializer(world:World, renderEngine:RenderEngine)
        {
            _world = world;
            _renderEngine = renderEngine;
            current = this;
        }

        /** 将当前 World 序列化为可 JSON.stringify 的对象。 */
        CONFIG::STUDIO
        public function serialize():Object
        {
            var elements:Array = [];
            var idMap:Dictionary = new Dictionary();
            // 先分配 ID 并建立映射
            var idx:int = 0;
            for each (var e:Element in _world.elements)
            {
                if (!e.alive) continue;
                idMap[e] = idx;
                idx++;
            }

            for each (var e2:Element in _world.elements)
            {
                if (!e2.alive) continue;
                var parentT:Transform = e2.getComponent(Transform) as Transform;
                var parentId:int = -1;
                if (parentT != null && parentT.parent != null && parentT.parent.owner != null)
                    parentId = idMap[parentT.parent.owner] as int;

                var doc:Object = {
                    // 元素 ID 一并存档：撤销/重做靠 deserialize 整体重建世界，ID 重排会让
                    // Studio 侧所有按 ID 记录的状态（Hierarchy 展开/选中、Inspector 目标）失配。
                    id: e2.id,
                    name: e2.name,
                    parentId: parentId,
                    enabled: e2.enabled,
                    components: serializeComponents(e2)
                };

                // 实例溯源：只有属于某个 prefab 实例的元素才写这个字段。
                var meta:Object = _prefabMeta[e2];
                if (meta != null)
                {
                    var pdoc:Object = { instanceId: meta.instanceId, sourceId: meta.sourceId };
                    // override 集：仅非空时写，没改过任何东西的实例存档保持精简。
                    var ov:Dictionary = meta.overrides as Dictionary;
                    if (ov != null)
                    {
                        var keys:Array = [];
                        for (var k:* in ov)
                            if (ov[k]) keys.push(k);
                        if (keys.length > 0) pdoc.overrides = keys;
                    }
                    doc.prefab = pdoc;
                }

                elements.push(doc);
            }

            var result:Object = { elements: elements };
            // 实例登记表：没有实例时不写，普通场景存档保持旧格式。
            if (_prefabInstances.length > 0) result.prefabInstances = _prefabInstances;
            return result;
        }

        /** 序列化单个元素的组件数组（serialize 与 serializeSubtree 共用）。 */
        CONFIG::STUDIO
        private function serializeComponents(e:Element):Array
        {
            var comps:Array = [];
            for each (var c:DreamComponent in e.getComponents())
            {
                var fields:Object = {};
                for each (var f:FieldInfo in c.getInspectableFields())
                {
                    // action 字段是编辑器命令（如 "Fit to Texture"），不是状态：
                    // 若一并序列化，反序列化时会误触发命令（如 setNativeSize 把矩形
                    // 改成纹理原生尺寸），故跳过。
                    if (f.type == "action") continue;
                    fields[f.name] = f.value;
                }
                comps.push({ type: shortClassName(c), fields: fields });
            }
            return comps;
        }

        /**
         * 序列化以 rootId 为根的子树，供「把选中子树导出为 prefab」使用。
         * 输出与本类其它输出同形（{elements:[...]}，parentId 为子树内下标、根为 -1），
         * 因此写出的 .prefab 与 .space 完全同构，可被 deserialize / instantiate 直接读回。
         * 不写实例溯源：第一版不支持 prefab 里再嵌 prefab。根不存在时返回 null。
         */
        CONFIG::STUDIO
        public function serializeSubtree(rootId:int):Object
        {
            var root:Element = findElement(rootId);
            if (root == null) return null;

            var order:Array = [];
            var parentOf:Dictionary = new Dictionary();
            collectSubtree(root, null, order, parentOf);

            var indexOf:Dictionary = new Dictionary();
            for (var i:int = 0; i < order.length; i++) indexOf[order[i]] = i;

            var elements:Array = [];
            for each (var e:Element in order)
            {
                var parentId:int = -1;
                var parent:Element = parentOf[e] as Element;
                if (parent != null) parentId = indexOf[parent] as int;
                elements.push({
                    name: e.name,
                    parentId: parentId,
                    enabled: e.enabled,
                    components: serializeComponents(e)
                });
            }
            return { elements: elements };
        }

        /**
         * 从数据对象实例化一棵元素子树（.prefab 文件内容，或存档里的 subtree），挂到 parentId 下。
         * parentId=-1 表示挂到场景根元素下（与 hierarchy create 约定一致）。
         * 实例必须挂在已有元素下：解析不到父级（场景无根 / id 失效）时不做任何创建并返回 -1，
         * 避免元素游离成新的根级节点（场景有且仅有一个根）。
         * 返回新子树顶层元素 ID。
         *
         * 不设 CONFIG 门控：编辑器实例化与发布构建的运行期 spawn 走同一条代码路径。
         * 实例溯源只在编辑器记录（见类注释），并随 scene.save 写回存档。
         */
        public function instantiate(data:Object, parentId:int = -1, guid:String = null):int
        {
            if (data == null || data.elements == null) return -1;
            var elements:Array = data.elements as Array;
            if (elements == null || elements.length == 0) return -1;

            // 先解析目标父级：解析不到直接放弃（连实例 ID 都不占用、不创建半棵树）。
            var target:Element = findElement(parentId >= 0 ? parentId : _world.rootElementId);
            var targetT:Transform = target != null ? (target.getComponent(Transform) as Transform) : null;
            if (targetT == null) return -1;

            var instanceId:int = 0;
            CONFIG::STUDIO
            {
                instanceId = _nextPrefabInstanceId++;
            }

            var created:Vector.<Element> = new Vector.<Element>();
            var parentIds:Vector.<int> = new Vector.<int>();

            // 顶层元素（parentId < 0 的第一个）在创建前就要知道：实例根坐标要预置为 override。
            var topIndex:int = 0;
            for (var t:int = 0; t < elements.length; t++)
            {
                var topEd:Object = elements[t];
                if (topEd != null && (topEd.parentId == null || int(topEd.parentId) < 0)) { topIndex = t; break; }
            }

            for (var i:int = 0; i < elements.length; i++)
            {
                var ed:Object = elements[i];
                if (ed == null) return -1;
                var e:Element = createElementFromData(ed);
                created.push(e);
                parentIds.push(ed.parentId != null ? int(ed.parentId) : -1);

                CONFIG::STUDIO
                {
                    if (instanceId > 0)
                    {
                        var ov:Dictionary = new Dictionary();
                        // 顶层元素的摆放位置属于"实例自己的位置"：源 prefab 若改了根坐标，
                        // 不应把场景里各实例搬到同一个地方。预置为 override（Unity 同理）。
                        if (i == topIndex) ov["Transform.position"] = true;
                        _prefabMeta[e] = { instanceId: instanceId, sourceId: i, overrides: ov };
                    }
                }
            }

            // 子树内部层级（parentId 为子树内下标）。
            for (var j:int = 0; j < created.length; j++)
            {
                var pid:int = parentIds[j];
                if (pid < 0 || pid >= created.length) continue;
                var ct:Transform = created[j].getComponent(Transform) as Transform;
                var pt:Transform = created[pid].getComponent(Transform) as Transform;
                if (ct != null && pt != null) ct.setParent(pt, false);
            }

            // 顶层元素挂到目标父级下。
            var top:Element = created[topIndex];
            var topT:Transform = top.getComponent(Transform) as Transform;
            if (topT != null) topT.setParent(targetT, false);

            CONFIG::STUDIO
            {
                if (instanceId > 0)
                    _prefabInstances.push({ instanceId: instanceId, guid: guid != null ? guid : "" });
            }

            _world.hierarchyDirty = true;
            return top.id;
        }

        /**
         * 运行期 spawn 预制体：GUID →（ResourceManager 的 GUID→路径索引）绝对路径 →
         * 读 .prefab → 交给 instantiate（格式与 .space 同构，含实例溯源）。
         *
         * 异步安全，因此调用方不必关心时序：GUID 尚未随 resource.index 到达时
         * （编辑器启动即属此列——引擎先按 --scene 加载场景并进入运行态，之后才连上
         * Studio 收到索引）本次请求排队，索引到位后自动实例化。组件在自己的 start()
         * 里直接调用即可。
         *
         * 结果经 callback 返回一次：新实例顶层元素 ID，失败为 -1
         * （GUID 为空/未注册、文件缺失、JSON 解析失败、父级不可用）。
         * 不缓存内容：源在编辑期会变（热重载后整份换掉），缓存只会留下会过期的副本。
         */
        public function spawnPrefab(guid:String, parentId:int, callback:Function):void
        {
            var rm:ResourceManager = ResourceManager.current;
            if (rm == null)
            {
                trace("[prefab] spawn 失败：ResourceManager 未初始化");
                callback(-1);
                return;
            }
            var report:Function = callback;
            rm.requestPrefabByGuid(guid, function(data:Object):void
            {
                if (data == null)
                {
                    trace("[prefab] spawn 失败：取不到预制体内容 guid=" + guid);
                    report(-1);
                    return;
                }
                report(instantiate(data, parentId, guid));
            });
        }

        // ── prefab override 记录与同步（仅编辑器） ──

        /**
         * 记录实例元素上的一个 override 键：调用方在"用户改了实例的某处"时调用。
         * 键的形式见类注释（"Component.field" / "Component" / "!Component" / "name" / "enabled"）。
         * 非实例元素（普通场景元素）直接忽略——override 只对 prefab 实例有意义。
         */
        CONFIG::STUDIO
        public function recordOverride(elementId:int, key:String):void
        {
            if (key == null || key.length == 0) return;
            var e:Element = findElement(elementId);
            if (e == null) return;
            var meta:Object = _prefabMeta[e];
            if (meta == null) return;
            var ov:Dictionary = meta.overrides as Dictionary;
            if (ov == null)
            {
                ov = new Dictionary();
                meta.overrides = ov;
            }
            ov[key] = true;
        }

        /**
         * 查询元素所属的 prefab 实例信息，供 Hierarchy / Inspector 显示与还原使用。
         * 元素不属于任何实例时返回 null，否则：
         *   { instanceId:int, guid:String, isRoot:Boolean, overrides:Dictionary }
         * isRoot 表示它是实例的顶层元素（向上没有同属一个实例的祖先）；
         * guid 由 instanceId 反查实例登记表得到（文件被删/未登记时为空串）。
         */
        CONFIG::STUDIO
        public function describePrefab(e:Element):Object
        {
            if (e == null) return null;
            var meta:Object = _prefabMeta[e];
            if (meta == null) return null;
            var instanceId:int = int(meta.instanceId);
            var ov:Dictionary = meta.overrides as Dictionary;
            return {
                instanceId: instanceId,
                guid: guidOfInstance(instanceId),
                isRoot: isInstanceRoot(e, instanceId),
                overrides: ov != null ? ov : new Dictionary()
            };
        }

        /**
         * 清空该元素所属实例上的全部 override（整实例还原），随后由 Studio 重推源完成同步。
         * 实例顶层元素的摆放位置是"实例自己的位置"，与 syncInstances 的兜底一致：保留不清。
         * 返回 false 表示元素不存在或不属于任何 prefab 实例。
         */
        CONFIG::STUDIO
        public function clearInstanceOverrides(elementId:int):Boolean
        {
            var e:Element = findElement(elementId);
            if (e == null) return false;
            var meta:Object = _prefabMeta[e];
            if (meta == null) return false;

            var instanceId:int = int(meta.instanceId);
            var hit:Boolean = false;
            for each (var el:Element in _world.elements)
            {
                if (!el.alive) continue;
                var m:Object = _prefabMeta[el];
                if (m == null || int(m.instanceId) != instanceId) continue;
                var ov:Dictionary = m.overrides as Dictionary;
                if (ov == null) continue;

                if (isInstanceRoot(el, instanceId))
                {
                    var keepPlacement:Boolean = ov["Transform.position"] == true;
                    clearDictionary(ov);
                    if (keepPlacement) ov["Transform.position"] = true;
                }
                else
                {
                    clearDictionary(ov);
                }
                hit = true;
            }
            return hit;
        }

        /** 按 instanceId 反查实例登记表里的 prefab GUID；未登记返回空串。 */
        CONFIG::STUDIO
        private function guidOfInstance(instanceId:int):String
        {
            for each (var inst:Object in _prefabInstances)
            {
                if (inst != null && int(inst.instanceId) == instanceId)
                    return inst.guid != null ? String(inst.guid) : "";
            }
            return "";
        }

        /** 实例顶层元素 = 沿 Transform 父链向上，没有同属该实例的祖先。 */
        CONFIG::STUDIO
        private function isInstanceRoot(e:Element, instanceId:int):Boolean
        {
            var t:Transform = e.getComponent(Transform) as Transform;
            t = t != null ? t.parent : null;
            while (t != null)
            {
                var owner:Element = t.owner as Element;
                if (owner == null) break;
                var meta:Object = _prefabMeta[owner];
                if (meta != null && int(meta.instanceId) == instanceId) return false;
                t = t.parent;
            }
            return true;
        }

        /** 清空字典（先收集键再删，避免遍历期间改结构）。 */
        CONFIG::STUDIO
        private function clearDictionary(d:Dictionary):void
        {
            var keys:Array = [];
            for (var k:* in d) keys.push(k);
            for each (var key:* in keys) delete d[key];
        }

        /**
         * 接收 Studio 推送的 prefab 源内容（guid → .prefab 数据），立刻同步到当前场景的实例上。
         * 返回实际同步到的实例数。
         *
         * 不做缓存：推送时机由 Studio 保证——它总在场景加载完成之后推（打开场景、引擎连上时），
         * 所以"收到即应用"落在的正是目标场景。缓存一份副本只会引入会过期的状态。
         */
        CONFIG::STUDIO
        public function syncPrefabSources(entries:Array):int
        {
            if (entries == null || entries.length == 0) return 0;
            var synced:int = 0;
            for each (var en:Object in entries)
            {
                if (en == null || en.guid == null || en.data == null) continue;
                var guid:String = String(en.guid);
                if (guid.length == 0) continue;
                synced += syncInstances(guid, en.data);
            }
            if (synced > 0) _world.hierarchyDirty = true;
            return synced;
        }

        /**
         * 把预制体源的内容同步到场景中该 guid 的所有实例上。
         *
         * 语义（引用 + override 的简化版，刻意不做结构增删）：
         *   - 只更新实例里已存在的元素：名称、启用、组件字段值；被 override 的键跳过。
         *   - 源里新增的组件会补挂到实例元素上；被用户移除过（"!C"）的组件不补回。
         *   - 不新增/删除元素，也不改实例内部层级——用户在实例下自行加的子元素因此永远安全；
         *     反过来，从源里删掉元素不会让实例跟着删（需手动处理）。
         *
         * 返回被同步的实例数。data 为 .prefab 文件内容（与 .space 同构）。
         */
        CONFIG::STUDIO
        private function syncInstances(guid:String, data:Object):int
        {
            if (guid == null || guid.length == 0) return 0;
            if (data == null || data.elements == null) return 0;
            var elements:Array = data.elements as Array;
            if (elements == null || elements.length == 0) return 0;

            // 实例顶层元素（源里第一个 parentId < 0 的）在源里的下标：它的摆放位置属于实例自身。
            var topSourceId:int = -1;
            for (var t:int = 0; t < elements.length; t++)
            {
                var ed:Object = elements[t];
                if (ed != null && (ed.parentId == null || int(ed.parentId) < 0)) { topSourceId = t; break; }
            }

            var synced:int = 0;
            for each (var inst:Object in _prefabInstances)
            {
                if (inst == null || inst.guid == null || String(inst.guid) != guid) continue;
                syncInstance(int(inst.instanceId), elements, topSourceId);
                synced++;
            }
            return synced;
        }

        /** 同步单个实例：按 sourceId 找到实例元素与源元素，逐个套用。 */
        CONFIG::STUDIO
        private function syncInstance(instanceId:int, source:Array, topSourceId:int):void
        {
            // 这里只读 _world.elements，不改结构（同步不增删元素）。
            for each (var e:Element in _world.elements)
            {
                if (!e.alive) continue;
                var meta:Object = _prefabMeta[e];
                if (meta == null || int(meta.instanceId) != instanceId) continue;
                var sourceId:int = int(meta.sourceId);
                if (sourceId < 0 || sourceId >= source.length) continue;
                applySourceToInstance(e, source[sourceId], meta.overrides as Dictionary,
                    sourceId == topSourceId);
            }
        }

        /**
         * 把源元素数据套用到实例元素：名称 / 启用 / 各组件字段，逐键跳过已 override 的。
         * 源里存在但实例上没有的组件会被创建并挂上（除非用户移除过）。
         * isTop：实例顶层元素——它的摆放位置属于实例自身，不跟着源走。
         */
        CONFIG::STUDIO
        private function applySourceToInstance(e:Element, src:Object, ov:Dictionary,
                                               isTop:Boolean):void
        {
            if (src == null) return;
            if (ov == null) ov = new Dictionary();

            if (!ov["name"] && src.name != null && e.name != String(src.name))
                e.name = String(src.name);
            if (!ov["enabled"] && src.enabled != null && e.enabled != Boolean(src.enabled))
                e.enabled = Boolean(src.enabled);

            var comps:Array = src.components as Array;
            if (comps == null) return;

            for each (var cd:Object in comps)
            {
                if (cd == null || cd.type == null) continue;
                var typeName:String = String(cd.type);
                if (ov["!" + typeName]) continue; // 用户移除过：不补回

                var c:DreamComponent = findComponentByName(e, typeName);
                if (c == null)
                {
                    c = createComponent(typeName);
                    if (c == null) continue;
                    e.addComponent(c);
                }

                var fields:Object = cd.fields;
                if (fields == null) continue;
                for (var fname:String in fields)
                {
                    var key:String = typeName + "." + fname;
                    if (ov[key]) continue;
                    // 实例根的摆放位置属于实例自己：即使存档没记这条 override（旧版存档），也不跟源走。
                    if (isTop && key == "Transform.position") continue;
                    c.setFieldValue(fname, fields[fname]);
                }
            }
        }

        /** 按组件短类名查找元素上的组件；找不到返回 null。 */
        CONFIG::STUDIO
        private function findComponentByName(e:Element, shortName:String):DreamComponent
        {
            for each (var c:DreamComponent in e.getComponents())
            {
                if (shortClassName(c) == shortName) return c;
            }
            return null;
        }

        /**
         * 从数据对象重建 World。先清空所有现有元素，再按数据创建。
         * 返回 true 表示加载成功。
         *
         * 不设 CONFIG 门控：编辑器加载场景与发布构建加载启动场景走同一条路径，
         * 保证两端对 .space 的解析行为完全一致（含用户自定义组件的反射恢复）。
         */
        public function deserialize(data:Object):Boolean
        {
            if (data == null || data.elements == null) return false;
            var elements:Array = data.elements as Array;
            if (elements == null) return false;

            // 清空当前 World
            var existing:Vector.<Element> = _world.elements.slice();
            for each (var old:Element in existing)
                _world.destroyElement(old);

            CONFIG::STUDIO
            {
                // 上一份存档的实例溯源不能带进新场景：整体重置，随后按数据重建。
                _prefabMeta = new Dictionary();
                _prefabInstances = [];
                _nextPrefabInstanceId = 1;
            }

            // 按 parentId 依赖顺序创建：先全部创建元素+组件，再恢复层级
            var created:Vector.<Element> = new Vector.<Element>();
            var parentIds:Vector.<int> = new Vector.<int>();

            for each (var ed:Object in elements)
            {
                var e:Element = createElementFromData(ed);
                // 恢复存档里的元素 ID；旧存档没这个键则保留刚分配的新 ID。
                // 撤销/重做就是"反序列化旧快照"，ID 稳定才谈得上保住 Studio 侧的展开状态与选中。
                if (ed.id != null)
                {
                    var savedId:int = int(ed.id);
                    if (savedId > 0)
                    {
                        e.id = savedId;
                        Element.reserveId(savedId);
                    }
                }
                created.push(e);
                parentIds.push(ed.parentId != null ? int(ed.parentId) : -1);

                CONFIG::STUDIO
                {
                    var pmeta:Object = ed.prefab;
                    var pInstance:int = pmeta != null && pmeta.instanceId != null ? int(pmeta.instanceId) : 0;
                    if (pInstance > 0)
                    {
                        var pov:Dictionary = new Dictionary();
                        var ovList:Array = pmeta.overrides as Array;
                        if (ovList != null)
                        {
                            for each (var ovKey:String in ovList)
                                if (ovKey != null && ovKey.length > 0) pov[ovKey] = true;
                        }
                        _prefabMeta[e] = {
                            instanceId: pInstance,
                            sourceId: pmeta.sourceId != null ? int(pmeta.sourceId) : 0,
                            overrides: pov
                        };
                        if (pInstance >= _nextPrefabInstanceId) _nextPrefabInstanceId = pInstance + 1;
                    }
                }
            }

            CONFIG::STUDIO
            {
                // 实例登记表（instanceId → prefab GUID），供后续 override 同步。
                var instances:Array = data.prefabInstances as Array;
                if (instances != null)
                {
                    for each (var inst:Object in instances)
                    {
                        var instId:int = inst != null && inst.instanceId != null ? int(inst.instanceId) : 0;
                        if (instId <= 0) continue;
                        _prefabInstances.push({
                            instanceId: instId,
                            guid: inst.guid != null ? String(inst.guid) : ""
                        });
                        if (instId >= _nextPrefabInstanceId) _nextPrefabInstanceId = instId + 1;
                    }
                }
            }

            // 恢复父子层级
            for (var i:int = 0; i < created.length; i++)
            {
                var pid:int = parentIds[i];
                if (pid < 0 || pid >= created.length) continue;
                var childT:Transform = created[i].getComponent(Transform) as Transform;
                var parentT:Transform = created[pid].getComponent(Transform) as Transform;
                if (childT != null && parentT != null)
                    childT.setParent(parentT, false);
            }

            // 重新认定根元素：deserialize 整体替换了 World，而元素 id 单调递增、不复用，
            // 旧的 rootElementId 从此指向不存在的元素。不更新会让 parentId=-1 的创建/实例化
            // 找不到落点（元素游离成新的根级节点），根保护与"根不可复制"等判断也会失效。
            // 与 DreamEngine.ensureRoot 同一规则：取第一个无父元素为根（退路：第一个元素）。
            var rootId:int = -1;
            for (var r:int = 0; r < created.length; r++)
            {
                if (parentIds[r] < 0) { rootId = created[r].id; break; }
            }
            if (rootId < 0 && created.length > 0) rootId = created[0].id;
            _world.rootElementId = rootId;

            _world.hierarchyDirty = true;
            return true;
        }

        /**
         * 按元素数据（{name, enabled, components}）创建元素及其组件与字段值。
         * deserialize 与 instantiate 共用，保证两条路径对同一份数据的解释完全一致。
         * 不设 CONFIG 门控：发布构建加载存档也走这里。
         */
        private function createElementFromData(ed:Object):Element
        {
            var e:Element = _world.createElement();
            e.name = ed.name != null ? String(ed.name) : "Element";
            if (ed.enabled != null) e.enabled = Boolean(ed.enabled);

            var comps:Array = ed.components as Array;
            if (comps != null)
            {
                for each (var cd:Object in comps)
                {
                    var comp:DreamComponent = createComponent(String(cd.type));
                    if (comp == null) continue;
                    e.addComponent(comp);
                    // 恢复字段值
                    var fields:Object = cd.fields;
                    if (fields != null)
                    {
                        for (var fname:String in fields)
                            comp.setFieldValue(fname, fields[fname]);
                    }
                }
            }
            return e;
        }

        /** 按 ID 查找存活元素；找不到返回 null。
         *  对外公开：游戏代码 spawn 出预制体实例后按返回的 ID 拿回该实例（见 spawnPrefab）。 */
        public function findElement(id:int):Element
        {
            if (id < 0) return null;
            for each (var e:Element in _world.elements)
            {
                if (e.alive && e.id == id) return e;
            }
            return null;
        }

        /** 按类型名创建组件实例。Transform 和 DisplayComponent 特殊处理。 */
        private function createComponent(typeName:String):DreamComponent
        {
            switch (typeName)
            {
                case "Transform":
                    return new Transform();
                case "DisplayComponent":
                    // 先创建默认 Quad，setFieldValue 会设置实际 width/height/color
                    var quad:Drawable = _renderEngine.createQuad(1, 1, 0xFFFFFF);
                    return new DisplayComponent(quad);
                default:
                    return ComponentFactory.create(typeName);
            }
        }

        /**
         * 克隆元素子树：source 及其全部后代 → 一组全新元素（新 ID、全部组件字段），
         * 保持内部父子关系，顶层挂到 targetParent（缺省 null 时保持无父，由调用方决定）。
         * 顶层副本的本地坐标 = 源顶层本地坐标 + (offsetX, offsetY)。
         * 新顶层插入到源元素之后（同级顺序），并置 hierarchyDirty。
         * 返回新顶层元素；参数非法返回 null。
         *
         * 用于编辑器"复制/粘贴元素"命令：复用组件反射字段（getInspectableFields/
         * setFieldValue）与 createComponent 的组件创建规则，保证副本与反序列化一致。
         */
        CONFIG::STUDIO
        public function cloneSubtree(source:Element, targetParent:Element,
                                     offsetX:Number, offsetY:Number):Element
        {
            if (source == null || !source.alive) return null;

            // 1. 收集子树（DFS）：order 父先子后；parentOf 记录每个元素的旧父。
            var order:Array = [];
            var parentOf:Dictionary = new Dictionary();
            collectSubtree(source, null, order, parentOf);

            // 2. 逐元素创建副本（新 ID）+ 复制组件字段。
            var newOf:Dictionary = new Dictionary();
            for each (var old:Element in order)
            {
                var ne:Element = _world.createElement();
                ne.name = old.name;
                ne.enabled = old.enabled;
                copyComponents(old, ne);
                newOf[old] = ne;
            }

            // 3. 恢复子树内部父子层级（映射到新元素）。
            for each (var o2:Element in order)
            {
                var oldParent:Element = parentOf[o2];
                if (oldParent == null) continue;
                var newParent:Element = newOf[oldParent] as Element;
                if (newParent == null) continue;
                var ct:Transform = (newOf[o2] as Element).getComponent(Transform) as Transform;
                var pt:Transform = newParent.getComponent(Transform) as Transform;
                if (ct != null && pt != null) ct.setParent(pt, false);
            }

            // 4. 顶层副本：位置偏移 + 挂到目标父级。
            var topNew:Element = newOf[source] as Element;
            if (topNew == null) return null;
            var topT:Transform = topNew.getComponent(Transform) as Transform;
            if (topT != null)
            {
                topT.x += offsetX;
                topT.y += offsetY;
                topT.setLocalDirty();
            }
            if (targetParent != null)
            {
                var tpT:Transform = targetParent.getComponent(Transform) as Transform;
                if (tpT != null && topT != null) topT.setParent(tpT, false);
            }

            // 5. 同级顺序：新顶层紧跟源元素之后。
            _world.moveElementTo(topNew, source, false);
            _world.hierarchyDirty = true;
            return topNew;
        }

        /** DFS 收集 e 及其后代到 order，parentOf[e] = e 的父（顶层不记录）。 */
        CONFIG::STUDIO
        private function collectSubtree(e:Element, parent:Element,
                                       order:Array, parentOf:Dictionary):void
        {
            order.push(e);
            if (parent != null) parentOf[e] = parent;
            var t:Transform = e.getComponent(Transform) as Transform;
            if (t == null) return;
            for (var i:int = 0; i < t.childCount; i++)
            {
                var childEl:Element = t.getChildAt(i).owner as Element;
                if (childEl != null && childEl.alive) collectSubtree(childEl, e, order, parentOf);
            }
        }

        /** 复制组件：按源组件类型重建实例并恢复可检查字段。 */
        CONFIG::STUDIO
        private function copyComponents(src:Element, dst:Element):void
        {
            for each (var c:DreamComponent in src.getComponents())
            {
                var comp:DreamComponent = createComponent(shortClassName(c));
                if (comp == null) continue;
                dst.addComponent(comp);
                for each (var f:FieldInfo in c.getInspectableFields())
                {
                    // action 字段为编辑器命令，不是状态：复制时不重放（见 serialize 注释）。
                    if (f.type == "action") continue;
                    comp.setFieldValue(f.name, f.value);
                }
            }
        }

        /** 获取类的短名（去包名）。
         *  AS3 getQualifiedClassName 返回形如 "dream.engine.ecs.components::Transform"
         *  （包与类用 "::" 分隔），取最后一个 "::" 或 "." 之后的部分。 */
        CONFIG::STUDIO
        private static function shortClassName(obj:Object):String
        {
            var full:String = getQualifiedClassName(obj);
            var colonIdx:int = full.lastIndexOf("::");
            var dotIdx:int = full.lastIndexOf(".");
            if (colonIdx >= 0)
                return full.substr(colonIdx + 2); // 跳过 "::" 两字符
            if (dotIdx >= 0)
                return full.substr(dotIdx + 1);
            return full;
        }
    }
}
