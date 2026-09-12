package dream.engine.render
{
    import dream.engine.animation.AnimationClip;
    import dream.engine.animation.AnimatorController;
    import dream.engine.audio.AudioClip;

    import flash.display.Bitmap;
    import flash.display.BitmapData;
    import flash.display.Loader;
    import flash.display.LoaderInfo;
    import flash.events.Event;
    import flash.filesystem.File;
    import flash.filesystem.FileMode;
    import flash.filesystem.FileStream;
    import flash.geom.Rectangle;
    import flash.utils.ByteArray;
    import flash.utils.Dictionary;

    /**
     * 资源管理器：异步加载外部图片文件为 Texture2D，按路径缓存。
     *
     * 用法：
     *   ResourceManager.current.requestTexture(path, function(tex:Texture2D):void { ... });
     *   ResourceManager.current.requestTextureByGuid(guid, function(tex:Texture2D):void { ... });
     *
     * 加载流程：
     *   1. 检查缓存——命中则同步回调
     *   2. 检查 pending 队列——已在加载则加入回调队列
     *   3. 读取文件字节 → Loader.loadBytes 异步解码 → BitmapData → Texture2D
     *
     * 缓存策略：按绝对路径缓存，不自动释放。调用 releaseTexture 手动释放单条，
     * disposeAll 释放全部（场景切换时使用）。
     *
     * GUID 解析：Studio 通过 resource.index 消息推送 GUID→路径映射；
     * SpriteRenderer 等组件使用 GUID 引用资源，避免重命名/移动破坏引用。
     */
    public final class ResourceManager
    {
        /** 当前实例（服务定位器），由 DreamEngine 初始化时设置。 */
        public static var current:ResourceManager;

        // path → Texture2D（已加载缓存）
        private var _cache:Dictionary = new Dictionary();
        // path → Vector.<Function>（加载中回调队列）
        private var _pending:Dictionary = new Dictionary();
        // guid → path（由 Studio 推送的 GUID→路径索引）
        private var _guidToPath:Dictionary = new Dictionary();
        // guid → Vector.<Function>（GUID 尚未解析时的等待队列，索引注册后唤醒）
        private var _pendingGuid:Dictionary = new Dictionary();
        // guid → AnimationClip（已解析的 .dmclip 缓存）
        private var _clipCache:Dictionary = new Dictionary();
        // guid → Vector.<Function>（.dmclip 等待 GUID 索引注册）
        private var _pendingClipGuid:Dictionary = new Dictionary();
        // guid → AnimatorController（已解析的 .dmanimator 缓存）
        private var _controllerCache:Dictionary = new Dictionary();
        // guid → Vector.<Function>（.dmanimator 等待 GUID 索引注册）
        private var _pendingControllerGuid:Dictionary = new Dictionary();
        // guid → AudioClip（已解析的音频缓存）
        private var _audioCache:Dictionary = new Dictionary();
        // guid → Vector.<Function>（音频等待 GUID 索引注册）
        private var _pendingAudioGuid:Dictionary = new Dictionary();
        // path → SpriteSheet（已解析的 .dmsheet 缓存；精灵定义按源纹理聚合）
        private var _sheetByPath:Dictionary = new Dictionary();
        // 精灵 GUID → 精灵名（索引中带精灵名的子资源条目，供 Inspector 显示）
        private var _guidToSpriteName:Dictionary = new Dictionary();
        // 精灵 GUID → { textureGuid, rect }（图集覆盖映射，由 Studio 打包后推送）
        private var _atlasBySprite:Dictionary = new Dictionary();
        // 精灵 GUID → Vector.<Function>（图集覆盖变化时需要重新解析的订阅者，见 watchSprite）
        private var _spriteWatchers:Dictionary = new Dictionary();
        // guid → Vector.<Function>（精灵等待 GUID 索引注册）
        private var _pendingSpriteGuid:Dictionary = new Dictionary();
        // guid → Vector.<Function>（预制体等待 GUID 索引注册）
        private var _pendingPrefabGuid:Dictionary = new Dictionary();

        public function ResourceManager()
        {
            current = this;
        }

        /**
         * 请求加载纹理。若已缓存则同步回调，否则异步加载后回调。
         * callback 签名：function(texture:Texture2D):void
         * 加载失败时 callback 收到 null。
         */
        public function requestTexture(path:String, callback:Function):void
        {
            if (path == null || path.length == 0) { callback(null); return; }

            // 缓存命中：同步回调
            var cached:Texture2D = _cache[path];
            if (cached != null) { callback(cached); return; }

            // 已在加载：加入回调队列
            var pendingList:Vector.<Function> = _pending[path];
            if (pendingList != null)
            {
                pendingList.push(callback);
                return;
            }

            // 首次请求：创建队列并启动异步加载
            pendingList = new Vector.<Function>();
            pendingList.push(callback);
            _pending[path] = pendingList;

            loadAsync(path);
        }

        /**
         * 注册 GUID → 路径映射。由 ResourceIndexHandler 在收到 resource.index 时调用。
         * 注册后唤醒此前因索引未就绪而等待的请求（场景加载早于索引推送的时序兜底）。
         *
         * spriteName 非空表示这是一条**精灵子资源**条目：GUID 指向精灵，
         * path 指向承载它的 .dmsheet 文件（而非可直接加载的图片）。
         */
        public function registerGuid(guid:String, path:String, spriteName:String = null):void
        {
            if (guid == null || guid.length == 0 || path == null || path.length == 0) return;
            _guidToPath[guid] = path;
            if (spriteName != null && spriteName.length > 0) _guidToSpriteName[guid] = spriteName;

            var waiting:Vector.<Function> = _pendingGuid[guid];
            if (waiting != null)
            {
                delete _pendingGuid[guid];
                for each (var cb:Function in waiting)
                    requestTexture(path, cb);
            }

            // 唤醒等待 .dmclip 索引的队列。
            var waitingClips:Vector.<Function> = _pendingClipGuid[guid];
            if (waitingClips != null)
            {
                delete _pendingClipGuid[guid];
                for each (var cb2:Function in waitingClips)
                    loadClipAsync(guid, path, cb2);
            }

            // 唤醒等待 .dmanimator 索引的队列。
            var waitingControllers:Vector.<Function> = _pendingControllerGuid[guid];
            if (waitingControllers != null)
            {
                delete _pendingControllerGuid[guid];
                for each (var cb3:Function in waitingControllers)
                    loadControllerAsync(guid, path, cb3);
            }

            // 唤醒等待音频索引的队列。
            var waitingAudio:Vector.<Function> = _pendingAudioGuid[guid];
            if (waitingAudio != null)
            {
                delete _pendingAudioGuid[guid];
                for each (var cb4:Function in waitingAudio)
                    loadAudioAsync(guid, path, cb4);
            }

            // 唤醒等待精灵索引的队列。
            var waitingSprites:Vector.<Function> = _pendingSpriteGuid[guid];
            if (waitingSprites != null)
            {
                delete _pendingSpriteGuid[guid];
                for each (var cb5:Function in waitingSprites)
                    resolveSprite(guid, path, cb5);
            }

            // 唤醒等待预制体索引的队列。
            var waitingPrefabs:Vector.<Function> = _pendingPrefabGuid[guid];
            if (waitingPrefabs != null)
            {
                delete _pendingPrefabGuid[guid];
                for each (var cb6:Function in waitingPrefabs)
                    loadPrefabAsync(path, cb6);
            }
        }

        /**
         * 请求指定 GUID 对应的预制体内容（.prefab 解析后的 JSON 对象）。
         * GUID 未解析到路径时排队等待，registerGuid 注册后自动读取后回调。
         *
         * 为什么需要排队：编辑器启动时引擎先按 --scene 加载场景并进入运行态，
         * 之后才连上 Studio 收到 resource.index——游戏代码在组件 start() 里
         * 请求 spawn 时 GUID 往往还没注册。排队让调用方不必关心这个时序。
         *
         * 与其它资源不同，这里**不缓存**：预制体源在编辑期会变（热重载后整份换掉），
         * 缓存只会留下一份会过期的副本；spawn 频率很低，读盘成本可忽略。
         * callback 签名：function(data:Object):void，失败回调 null。
         */
        public function requestPrefabByGuid(guid:String, callback:Function):void
        {
            if (guid == null || guid.length == 0) { callback(null); return; }
            var path:String = _guidToPath[guid] as String;
            if (path == null || path.length == 0)
            {
                // 索引未就绪：排队等待。
                var waiting:Vector.<Function> = _pendingPrefabGuid[guid];
                if (waiting == null)
                {
                    waiting = new Vector.<Function>();
                    _pendingPrefabGuid[guid] = waiting;
                }
                waiting.push(callback);
                return;
            }
            loadPrefabAsync(path, callback);
        }

        /** 读取 .prefab 文件 → JSON 解析 → 回调（不缓存）。失败仅 trace 并回调 null。 */
        private function loadPrefabAsync(path:String, callback:Function):void
        {
            try
            {
                var file:File = new File(path);
                if (!file.exists)
                {
                    trace("[ResourceManager] .prefab 不存在: " + path);
                    callback(null);
                    return;
                }
                var fs:FileStream = new FileStream();
                fs.open(file, FileMode.READ);
                var json:String = fs.readUTFBytes(fs.bytesAvailable);
                fs.close();
                callback(JSON.parse(json));
            }
            catch (e:Error)
            {
                trace("[ResourceManager] .prefab 解析失败: " + path + " - " + e);
                callback(null);
            }
        }

        /**
         * 请求加载指定 GUID 对应的纹理。
         * 若 GUID 未解析到路径（资源索引尚未推送），加入等待队列，
         * 待 registerGuid 注册后自动加载——避免"场景在索引到达前反序列化"丢纹理。
         * GUID 为空时直接回调 null。
         */
        public function requestTextureByGuid(guid:String, callback:Function):void
        {
            if (guid == null || guid.length == 0) { callback(null); return; }
            var path:String = _guidToPath[guid] as String;
            if (path == null || path.length == 0)
            {
                // 索引未就绪：排队等待。
                var waiting:Vector.<Function> = _pendingGuid[guid];
                if (waiting == null)
                {
                    waiting = new Vector.<Function>();
                    _pendingGuid[guid] = waiting;
                }
                waiting.push(callback);
                return;
            }
            requestTexture(path, callback);
        }

        /**
         * 请求加载指定 GUID 对应的动画片段（.dmclip）。
         * GUID 未解析到路径时排队等待（与 requestTextureByGuid 同机制）；
         * 已缓存则同步回调。callback 签名：function(clip:AnimationClip):void，失败回调 null。
         */
        public function requestClipByGuid(guid:String, callback:Function):void
        {
            if (guid == null || guid.length == 0) { callback(null); return; }
            var cached:AnimationClip = _clipCache[guid];
            if (cached != null) { callback(cached); return; }
            var path:String = _guidToPath[guid] as String;
            if (path == null || path.length == 0)
            {
                // 索引未就绪：排队等待。
                var waiting:Vector.<Function> = _pendingClipGuid[guid];
                if (waiting == null)
                {
                    waiting = new Vector.<Function>();
                    _pendingClipGuid[guid] = waiting;
                }
                waiting.push(callback);
                return;
            }
            loadClipAsync(guid, path, callback);
        }

        /** 读取 .dmclip 文件 → JSON 解析 → AnimationClip，缓存后回调。失败回调 null。 */
        private function loadClipAsync(guid:String, path:String, callback:Function):void
        {
            try
            {
                var file:File = new File(path);
                if (!file.exists)
                {
                    trace("[ResourceManager] .dmclip 不存在: " + path);
                    callback(null);
                    return;
                }
                var fs:FileStream = new FileStream();
                fs.open(file, FileMode.READ);
                var json:String = fs.readUTFBytes(fs.bytesAvailable);
                fs.close();
                var data:Object = JSON.parse(json);
                var clip:AnimationClip = AnimationClip.fromJson(data);
                _clipCache[guid] = clip;
                callback(clip);
            }
            catch (e:Error)
            {
                trace("[ResourceManager] .dmclip 解析失败: " + path + " - " + e);
                callback(null);
            }
        }

        /**
         * 请求加载指定 GUID 对应的动画状态机控制器（.dmanimator）。
         * GUID 未解析到路径时排队等待（与 requestClipByGuid 同机制）；
         * 已缓存则同步回调。callback 签名：function(controller:AnimatorController):void，
         * 失败回调 null。
         */
        public function requestControllerByGuid(guid:String, callback:Function):void
        {
            if (guid == null || guid.length == 0) { callback(null); return; }
            var cached:AnimatorController = _controllerCache[guid];
            if (cached != null) { callback(cached); return; }
            var path:String = _guidToPath[guid] as String;
            if (path == null || path.length == 0)
            {
                // 索引未就绪：排队等待。
                var waiting:Vector.<Function> = _pendingControllerGuid[guid];
                if (waiting == null)
                {
                    waiting = new Vector.<Function>();
                    _pendingControllerGuid[guid] = waiting;
                }
                waiting.push(callback);
                return;
            }
            loadControllerAsync(guid, path, callback);
        }

        /** 读取 .dmanimator 文件 → JSON 解析 → AnimatorController，缓存后回调。失败回调 null。 */
        private function loadControllerAsync(guid:String, path:String, callback:Function):void
        {
            try
            {
                var file:File = new File(path);
                if (!file.exists)
                {
                    trace("[ResourceManager] .dmanimator 不存在: " + path);
                    callback(null);
                    return;
                }
                var fs:FileStream = new FileStream();
                fs.open(file, FileMode.READ);
                var json:String = fs.readUTFBytes(fs.bytesAvailable);
                fs.close();
                var data:Object = JSON.parse(json);
                var controller:AnimatorController = AnimatorController.fromJson(data);
                _controllerCache[guid] = controller;
                callback(controller);
            }
            catch (e:Error)
            {
                trace("[ResourceManager] .dmanimator 解析失败: " + path + " - " + e);
                callback(null);
            }
        }

        /**
         * 请求加载指定 GUID 对应的音频片段（.mp3/.wav）。
         * GUID 未解析到路径时排队等待（与 requestClipByGuid 同机制）；
         * 已缓存则同步回调。callback 签名：function(clip:AudioClip):void，失败回调 null。
         */
        public function requestAudioByGuid(guid:String, callback:Function):void
        {
            if (guid == null || guid.length == 0) { callback(null); return; }
            var cached:AudioClip = _audioCache[guid];
            if (cached != null) { callback(cached); return; }
            var path:String = _guidToPath[guid] as String;
            if (path == null || path.length == 0)
            {
                // 索引未就绪：排队等待。
                var waiting:Vector.<Function> = _pendingAudioGuid[guid];
                if (waiting == null)
                {
                    waiting = new Vector.<Function>();
                    _pendingAudioGuid[guid] = waiting;
                }
                waiting.push(callback);
                return;
            }
            loadAudioAsync(guid, path, callback);
        }

        /** 读取音频文件 → 解码 → AudioClip，缓存后回调。失败回调 null。 */
        private function loadAudioAsync(guid:String, path:String, callback:Function):void
        {
            try
            {
                var file:File = new File(path);
                if (!file.exists)
                {
                    trace("[ResourceManager] 音频不存在: " + path);
                    callback(null);
                    return;
                }
                var fs:FileStream = new FileStream();
                fs.open(file, FileMode.READ);
                var bytes:ByteArray = new ByteArray();
                fs.readBytes(bytes);
                fs.close();

                var lower:String = path.toLowerCase();
                var clip:AudioClip = AudioClip.fromBytes(bytes, lower.indexOf(".mp3") >= 0);
                if (clip == null)
                {
                    trace("[ResourceManager] 音频解码失败: " + path);
                    callback(null);
                    return;
                }
                _audioCache[guid] = clip;
                callback(clip);
            }
            catch (e:Error)
            {
                trace("[ResourceManager] 音频加载失败: " + path + " - " + e);
                callback(null);
            }
        }

        /**
         * 请求按精灵 GUID 解析精灵：回调收到「纹理 + 纹理内像素矩形」。
         * callback 签名：function(texture:Texture2D, rect:Rectangle):void，失败回调 (null, null)。
         *
         * 解析顺序（图集是可选覆盖层，不打包也能用）：
         *   1. 命中图集映射 → 用图集纹理 + 图集内矩形
         *   2. 未命中 → 读该精灵所属的 .dmsheet，用源纹理 + 精灵自身矩形
         */
        public function requestSpriteByGuid(guid:String, callback:Function):void
        {
            if (guid == null || guid.length == 0) { callback(null, null); return; }
            var path:String = _guidToPath[guid] as String;
            if (path == null || path.length == 0)
            {
                // 索引未就绪：排队等待（registerGuid 后唤醒）。
                var waiting:Vector.<Function> = _pendingSpriteGuid[guid];
                if (waiting == null)
                {
                    waiting = new Vector.<Function>();
                    _pendingSpriteGuid[guid] = waiting;
                }
                waiting.push(callback);
                return;
            }
            resolveSprite(guid, path, callback);
        }

        /**
         * 替换图集覆盖映射。entries 每项：{ spriteGuid, textureGuid, x, y, w, h }。
         * 由 Studio 在合图缓存变化后推送（编辑器启动、切分精灵、图集定义变更等）；
         * 传空数组即清除全部覆盖（回到未打包状态）。
         * 覆盖变化后已订阅的精灵会被重新解析，实现编辑期热切换。
         */
        public function setAtlasEntries(entries:Array):void
        {
            _atlasBySprite = new Dictionary();
            if (entries != null)
            {
                for each (var e:Object in entries)
                {
                    if (e == null || e.spriteGuid == null || e.textureGuid == null) continue;
                    _atlasBySprite[String(e.spriteGuid)] = {
                        textureGuid: String(e.textureGuid),
                        rect: new Rectangle(Number(e.x), Number(e.y), Number(e.w), Number(e.h))
                    };
                }
            }
            refreshWatchedSprites();
        }

        /**
         * 订阅某精灵的解析结果：图集覆盖变化时用同样的回调重新解析一次。
         * 组件在设置 spriteGuid 后调用，销毁或清除引用时用 unwatchSprite 解除，
         * 否则 ResourceManager 会一直持有已销毁组件的引用。
         * 重复订阅同一 (guid, callback) 会被去重。
         */
        public function watchSprite(guid:String, callback:Function):void
        {
            if (guid == null || guid.length == 0 || callback == null) return;
            unwatchSprite(guid, callback);
            var list:Vector.<Function> = _spriteWatchers[guid] as Vector.<Function>;
            if (list == null)
            {
                list = new Vector.<Function>();
                _spriteWatchers[guid] = list;
            }
            list.push(callback);
        }

        /** 解除订阅。非订阅者调用无副作用。 */
        public function unwatchSprite(guid:String, callback:Function):void
        {
            if (guid == null || callback == null) return;
            var list:Vector.<Function> = _spriteWatchers[guid] as Vector.<Function>;
            if (list == null) return;
            for (var i:int = list.length - 1; i >= 0; i--)
                if (list[i] == callback) list.splice(i, 1);
            if (list.length == 0) delete _spriteWatchers[guid];
        }

        /** 覆盖表变化后重新解析所有被订阅的精灵并回调。 */
        private function refreshWatchedSprites():void
        {
            if (_spriteWatchers == null) return;

            // 先取 GUID 快照：回调可能改动订阅表（组件重建/销毁）。
            var guids:Array = [];
            for (var g:* in _spriteWatchers) guids.push(g);

            for each (var guid:String in guids)
            {
                var list:Vector.<Function> = _spriteWatchers[guid] as Vector.<Function>;
                var path:String = _guidToPath[guid] as String;
                if (list == null || path == null || path.length == 0) continue;

                // 再取回调快照：同一次刷新期间解析结果一致，逐一同一个回调即可。
                var callbacks:Vector.<Function> = list.slice();
                for each (var cb:Function in callbacks)
                    resolveSprite(guid, path, cb);
            }
        }

        /** 该 GUID 是否为精灵子资源（索引中带精灵名的条目）。 */
        public function isSpriteGuid(guid:String):Boolean
        {
            return guid != null && _guidToSpriteName[guid] != null;
        }

        /** 精灵名（Inspector 显示用）；非精灵返回 null。 */
        public function getSpriteName(guid:String):String
        {
            if (guid == null) return null;
            return _guidToSpriteName[guid] as String;
        }

        /** 解析精灵：图集覆盖优先，未命中回落精灵所属表的源纹理。 */
        private function resolveSprite(guid:String, path:String, callback:Function):void
        {
            var hit:Object = _atlasBySprite[guid];
            if (hit != null)
            {
                var atlasTexGuid:String = hit.textureGuid as String;
                var atlasRect:Rectangle = hit.rect as Rectangle;
                requestTextureByGuid(atlasTexGuid, function(tex:Texture2D):void
                {
                    callback(tex, atlasRect);
                });
                return;
            }

            var cached:SpriteSheet = _sheetByPath[path] as SpriteSheet;
            if (cached != null) { applySheet(cached, guid, callback); return; }

            // 非精灵表（散图）：整张纹理本身即一个"退化精灵"，无切分矩形。
            if (path.toLowerCase().indexOf(".dmsheet") < 0)
            {
                requestTextureByGuid(guid, function(tex:Texture2D):void { callback(tex, null); });
                return;
            }

            loadSheetByPath(path, function(sheet:SpriteSheet):void
            {
                if (sheet == null) { callback(null, null); return; }
                applySheet(sheet, guid, callback);
            });
        }

        /** 用精灵所属表解析出源纹理与矩形。 */
        private function applySheet(sheet:SpriteSheet, guid:String, callback:Function):void
        {
            var def:SpriteDef = sheet.getSpriteByGuid(guid);
            if (def == null)
            {
                trace("[ResourceManager] 精灵表中找不到该精灵: " + guid);
                callback(null, null);
                return;
            }
            var texGuid:String = sheet.textureGuid;
            var rect:Rectangle = def.rect.clone();
            requestTextureByGuid(texGuid, function(tex:Texture2D):void
            {
                callback(tex, rect);
            });
        }

        /** 读取 .dmsheet 文件 → JSON 解析 → SpriteSheet（按路径缓存）。失败回调 null。 */
        private function loadSheetByPath(path:String, callback:Function):void
        {
            try
            {
                var file:File = new File(path);
                if (!file.exists)
                {
                    trace("[ResourceManager] .dmsheet 不存在: " + path);
                    callback(null);
                    return;
                }
                var fs:FileStream = new FileStream();
                fs.open(file, FileMode.READ);
                var json:String = fs.readUTFBytes(fs.bytesAvailable);
                fs.close();
                var sheet:SpriteSheet = SpriteSheet.fromJson(JSON.parse(json));
                _sheetByPath[path] = sheet;
                callback(sheet);
            }
            catch (e:Error)
            {
                trace("[ResourceManager] .dmsheet 解析失败: " + path + " - " + e);
                callback(null);
            }
        }

        /** 异步加载图片文件：读字节 → Loader 解码 → Texture2D。 */
        private function loadAsync(path:String):void
        {
            try
            {
                var file:File = new File(path);
                if (!file.exists)
                {
                    trace("[ResourceManager] 文件不存在: " + path);
                    dispatchCallbacks(path, null);
                    return;
                }

                var fs:FileStream = new FileStream();
                fs.open(file, FileMode.READ);
                var bytes:ByteArray = new ByteArray();
                fs.readBytes(bytes);
                fs.close();

                var loader:Loader = new Loader();
                loader.contentLoaderInfo.addEventListener(Event.COMPLETE, function(e:Event):void
                {
                    try
                    {
                        var info:LoaderInfo = e.target as LoaderInfo;
                        var bmp:Bitmap = info.content as Bitmap;
                        if (bmp == null)
                        {
                            trace("[ResourceManager] 加载内容不是 Bitmap: " + path);
                            dispatchCallbacks(path, null);
                            return;
                        }
                        var bmd:BitmapData = bmp.bitmapData;
                        var tex:Texture2D = Texture2D.fromBitmapData(bmd);
                        bmd.dispose();
                        _cache[path] = tex;
                        dispatchCallbacks(path, tex);
                    }
                    catch (err:Error)
                    {
                        trace("[ResourceManager] 纹理创建失败: " + path + " - " + err);
                        dispatchCallbacks(path, null);
                    }
                });
                loader.loadBytes(bytes);
            }
            catch (err:Error)
            {
                trace("[ResourceManager] 加载失败: " + path + " - " + err);
                dispatchCallbacks(path, null);
            }
        }

        /** 分发回调队列。 */
        private function dispatchCallbacks(path:String, tex:Texture2D):void
        {
            var list:Vector.<Function> = _pending[path];
            delete _pending[path];
            if (list == null) return;
            for each (var cb:Function in list)
                cb(tex);
        }

        /** 释放指定路径的缓存纹理。 */
        public function releaseTexture(path:String):void
        {
            var tex:Texture2D = _cache[path];
            if (tex != null)
            {
                tex.dispose();
                delete _cache[path];
            }
        }

        /** 释放所有缓存纹理。 */
        public function disposeAll():void
        {
            for (var path:* in _cache)
            {
                (_cache[path] as Texture2D).dispose();
                delete _cache[path];
            }
        }
    }
}
