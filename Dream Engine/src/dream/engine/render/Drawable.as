package dream.engine.render
{
    import flash.display.BitmapData;
    import flash.geom.Matrix;
    import flash.geom.Point;
    import flash.geom.Rectangle;

    import starling.display.DisplayObject;
    import starling.display.Image;
    import starling.display.Mesh;
    import starling.display.Quad;
    import starling.text.TextField;

    /**
     * 引擎可绘制对象：对 Starling DisplayObject 的内联封装。
     * 用户只面对此类，不直接接触 starling.* 类型。
     *
     * 内部持有 Starling 实例，转发常用属性与变换。
     * RenderSystem 每帧通过 transformationMatrix 同步 Transform.worldMatrix。
     *
     * 命名说明：此类故意不与 starling.display.DisplayObject 同名——
     * AS3 编译器在同名类内部无法用全限定名引用 Starling 类型，会导致 _impl
     * 只能声明为 *（动态分发，性能差）。改名后可正常 import 并用强类型。
     *
     * 未来若替换 Starling，只需改本类内部实现，对外 API 不变。
     */
    public class Drawable
    {
        // Starling 实例，包内可见——RenderEngine 创建时注入，RenderSystem 不直接访问。
        internal var _impl:DisplayObject;

        // 源位图引用：像素 alpha 精确拾取用（RenderEngine.createImage 注入）。
        internal var _sourceBitmapData:BitmapData;

        // 该显示对象所用纹理在源位图中的像素区域（子纹理时非零）。
        internal var _sourceRect:Rectangle;

        // 像素 alpha 命中阈值（0-255）：低于该值的像素视为透明，不参与拾取。
        private static const AlphaHitThreshold:uint = 8;

        // 顶点复用对象（三角形精确判定用，避免每帧分配）。
        private static const sPointA:Point = new Point();
        private static const sPointB:Point = new Point();
        private static const sPointC:Point = new Point();
        private static const sPointD:Point = new Point();
        private static const sLocalPoint:Point = new Point();

        /**
         * 由 RenderEngine 内部创建。参数为底层显示对象——弱类型避免公共签名
         * 暴露 Starling 类型（引擎用户通过 RenderEngine 工厂获取，不应直接 new）。
         */
        public function Drawable(impl:*)
        {
            _impl = impl as DisplayObject;
        }

        // ── 变换（RenderSystem 主用） ──

        /** 实际显示矩阵（世界矩阵 × 本地 pivot 平移）。RenderSystem 每帧同步。 */
        public function get transformationMatrix():Matrix { return _impl.transformationMatrix; }
        public function set transformationMatrix(v:Matrix):void { _impl.transformationMatrix = v; }

        public function get x():Number { return _impl.x; }
        public function set x(v:Number):void { _impl.x = v; }
        public function get y():Number { return _impl.y; }
        public function set y(v:Number):void { _impl.y = v; }
        public function get rotation():Number { return _impl.rotation; }
        public function set rotation(v:Number):void { _impl.rotation = v; }
        public function get scaleX():Number { return _impl.scaleX; }
        public function set scaleX(v:Number):void { _impl.scaleX = v; }
        public function get scaleY():Number { return _impl.scaleY; }
        public function set scaleY(v:Number):void { _impl.scaleY = v; }

        // ── 可见性与外观 ──

        public function get visible():Boolean { return _impl.visible; }
        public function set visible(v:Boolean):void { _impl.visible = v; }

        /** 屏幕交互开关：false 时不参与拾取/触摸（编辑器覆盖层用）。 */
        public function get touchable():Boolean { return _impl.touchable; }
        public function set touchable(v:Boolean):void { _impl.touchable = v; }

        public function get alpha():Number { return _impl.alpha; }
        public function set alpha(v:Number):void { _impl.alpha = v; }

        /** 混合模式（Starling 字符串常量，如 auto/normal/add/...）。 */
        public function get blendMode():String { return _impl.blendMode; }
        public function set blendMode(v:String):void { _impl.blendMode = v; }

        /** 纯色矩形颜色（0xRRGGBB）。仅 Quad 支持读写，非 Quad 读返回 0xFFFFFF、写忽略。 */
        public function get color():uint
        {
            var q:Quad = _impl as Quad;
            return q != null ? q.color : 0xFFFFFF;
        }
        public function set color(v:uint):void
        {
            var q:Quad = _impl as Quad;
            if (q != null) q.color = v;
        }

        /** 九宫格（Starling scale9Grid，本地像素矩形）。仅带纹理的 Image 有效；null 关闭。 */
        public function set scale9Grid(v:Rectangle):void
        {
            var img:Image = _impl as Image;
            if (img != null) img.scale9Grid = v;
        }

        // ── 文本（Text 组件用，仅 Starling TextField 可绘制对象有效） ──
        // Starling 2.8：文本外观属性位于 TextField.format（starling.text.TextFormat）。

        public function set text(v:String):void
        {
            var tf:TextField = _impl as TextField;
            if (tf != null) tf.text = v;
        }

        public function set fontSize(v:Number):void
        {
            var tf:TextField = _impl as TextField;
            if (tf != null) tf.format.size = v;
        }

        /** 文本颜色（TextFormat.color）。注意与 color（Quad tint）区分。 */
        public function set textColor(v:uint):void
        {
            var tf:TextField = _impl as TextField;
            if (tf != null) tf.format.color = v;
        }

        public function set textAlign(v:String):void
        {
            var tf:TextField = _impl as TextField;
            if (tf != null) tf.format.horizontalAlign = v;
        }

        /** 垂直对齐：top | center | bottom。 */
        public function set verticalAlign(v:String):void
        {
            var tf:TextField = _impl as TextField;
            if (tf != null) tf.format.verticalAlign = v;
        }

        public function set bold(v:Boolean):void
        {
            var tf:TextField = _impl as TextField;
            if (tf != null) tf.format.bold = v;
        }

        public function set fontName(v:String):void
        {
            var tf:TextField = _impl as TextField;
            if (tf != null) tf.format.font = v;
        }

        // ── 尺寸与边界 ──

        public function get width():Number { return _impl.width; }
        public function set width(v:Number):void { _impl.width = v; }
        public function get height():Number { return _impl.height; }
        public function set height(v:Number):void { _impl.height = v; }

        /** 本地边界（未旋转/缩放/位移的几何包围盒），用于拾取/布局。 */
        public function get bounds():Rectangle { return _impl.bounds; }

        /**
         * 调整本地几何尺寸（不改 Transform 缩放）。仅 Quad/Image 有效，非 Quad 忽略。
         * 用于编辑器复制元素时还原 Quad 的创建尺寸（尺寸无可检查字段，无法走 setFieldValue）。
         */
        public function resizeLocal(width:Number, height:Number):void
        {
            var q:Quad = _impl as Quad;
            if (q != null && width > 0 && height > 0) q.readjustSize(width, height);
        }

        /**
         * 对象自身坐标系下的包围盒（不含任何变换）。
         * 注意：Starling 的 bounds 是"父坐标系"下的包围盒（含本对象变换），
         * pivot 自动居中需要的是未变换的本地几何尺寸，故取 getBounds(this)。
         */
        public function get localBounds():Rectangle { return _impl.getBounds(_impl); }

        // ── 精确命中（Scene 拾取） ──

        /**
         * 世界坐标精确命中（含完整父链变换与祖先遮罩）：
         * 点 → 对象本地坐标 → 三角形几何判定 + 像素 alpha 判定。
         *
         * 变换经 Starling globalToLocal 解析整条父链（容器嵌套、画布 CanvasScaler 缩放
         * 均包含在内）；对象未挂入舞台时退化为局部矩阵（兼容游离对象）。
         *
         * 祖先遮罩：任一祖先带 mask 且点落在遮罩外 → 不命中（与 Starling hitTest 一致，
         * 支持 maskInverted）。UI 分组容器（Mask/ScrollView）靠此实现裁剪区拾取。
         */
        public function hitTestWorld(wx:Number, wy:Number):Boolean
        {
            if (_impl == null) return false;

            sLocalPoint.setTo(wx, wy);
            var lp:Point;
            if (_impl.stage != null)
            {
                lp = _impl.globalToLocal(sLocalPoint);
            }
            else
            {
                var m:Matrix = _impl.transformationMatrix;
                if (m == null) return false;
                var inv:Matrix = m.clone();
                inv.invert();
                lp = inv.transformPoint(sLocalPoint);
            }
            if (!hitTestLocal(lp.x, lp.y)) return false;

            // 祖先遮罩：把舞台点换算到各祖先本地空间，交给 Starling hitTestMask 判定。
            var anc:DisplayObject = _impl.parent;
            while (anc != null)
            {
                if (anc.mask != null && anc.stage != null)
                {
                    sLocalPoint.setTo(wx, wy);
                    if (!anc.hitTestMask(anc.globalToLocal(sLocalPoint))) return false;
                }
                anc = anc.parent;
            }
            return true;
        }

        /**
         * 舞台（物理像素）坐标 → 本对象局部坐标，解析完整父链变换
         * （容器嵌套、画布屏幕/世界变换、相机缩放平移均包含在内）。
         * 对象未挂入舞台时退化为局部矩阵逆变换。
         *
         * 用途：把指针的舞台坐标换算到 UI 画布局部单位（CanvasSystem 分发指针事件/拖拽增量）、
         * 或把舞台点换算到某容器局部空间（编辑器侧）。
         */
        public function globalToLocal(wx:Number, wy:Number):Point
        {
            if (_impl == null) return new Point(wx, wy);
            sLocalPoint.setTo(wx, wy);
            if (_impl.stage != null) return _impl.globalToLocal(sLocalPoint);
            var m:Matrix = _impl.transformationMatrix;
            if (m == null) return new Point(wx, wy);
            var inv:Matrix = m.clone();
            inv.invert();
            return inv.transformPoint(sLocalPoint);
        }

        /** 本地坐标精确命中：先几何（Mesh 三角形判定/容器递归），再像素 alpha。 */
        public function hitTestLocal(localX:Number, localY:Number):Boolean
        {
            if (!hitTestGeometry(localX, localY)) return false;
            // 像素 alpha：透明（低透明度）像素区域不参与命中。
            if (_sourceBitmapData != null && !alphaHitAt(localX, localY)) return false;
            return true;
        }

        /**
         * 几何判定：
         *  - Mesh 族（Quad/Image）：默认 4 顶点 2 三角形，做三角形包含判定——
         *    旋转后包围盒角落区域不命中；带 tileGrid/scale9Grid 的多顶点网格
         *    退化为 AABB（与现状一致，减少误判可后续扩展）。
         *  - 容器类（Sprite 等）：委托 Starling hitTest 递归子对象。
         *  与 Starling hitTest 一致，先检查 visible/touchable。
         */
        private function hitTestGeometry(localX:Number, localY:Number):Boolean
        {
            if (!_impl.visible || !_impl.touchable) return false;

            var mesh:Mesh = _impl as Mesh;
            if (mesh != null && mesh.numVertices == 4)
            {
                // Quad 顶点布局（本地坐标）：
                //   0=(0,0)  1=(w,0)
                //   2=(0,h)  3=(w,h)
                // 索引 addQuad(0,1,2,3) → 三角形 (0,1,2) 与 (1,3,2)。
                mesh.getVertexPosition(0, sPointA);
                mesh.getVertexPosition(1, sPointB);
                mesh.getVertexPosition(2, sPointC);
                mesh.getVertexPosition(3, sPointD);
                return pointInTriangle(localX, localY, sPointA, sPointB, sPointC) ||
                       pointInTriangle(localX, localY, sPointB, sPointD, sPointC);
            }

            sLocalPoint.setTo(localX, localY);
            return _impl.hitTest(sLocalPoint) != null;
        }

        /** 叉积法点-三角形包含判定（顺/逆时针均可）。 */
        private static function pointInTriangle(x:Number, y:Number,
                                                a:Point, b:Point, c:Point):Boolean
        {
            var d1:Number = (x - b.x) * (a.y - b.y) - (a.x - b.x) * (y - b.y);
            var d2:Number = (x - c.x) * (b.y - c.y) - (b.x - c.x) * (y - c.y);
            var d3:Number = (x - a.x) * (c.y - a.y) - (c.x - a.x) * (y - a.y);
            var hasNeg:Boolean = (d1 < 0) || (d2 < 0) || (d3 < 0);
            var hasPos:Boolean = (d1 > 0) || (d2 > 0) || (d3 > 0);
            return !(hasNeg && hasPos);
        }

        /**
         * 像素 alpha 判定：命中点的本地坐标已由调用方完成几何判定，
         * 这里仅采样纹理像素 alpha（无采样源/已释放时视为不透明，不阻断命中）。
         */
        public function alphaHit(localX:Number, localY:Number):Boolean
        {
            if (_sourceBitmapData == null) return true;
            return alphaHitAt(localX, localY);
        }

        /** 采样命中点对应纹理像素的 alpha；低于阈值视为透明（不命中）。 */
        private function alphaHitAt(localX:Number, localY:Number):Boolean
        {
            var b:Rectangle = localBounds;
            if (b == null || b.width <= 0 || b.height <= 0) return true;
            try
            {
                var tw:int = _sourceBitmapData.width;
                var th:int = _sourceBitmapData.height;
                // 本地几何 (b) → 归一化 UV → 纹理所占区域的像素坐标。
                var sx:int = 0, sy:int = 0, sw:Number = tw, sh:Number = th;
                if (_sourceRect != null)
                {
                    sx = _sourceRect.x; sy = _sourceRect.y;
                    sw = _sourceRect.width; sh = _sourceRect.height;
                }
                var px:int = sx + Math.floor((localX - b.x) / b.width * sw);
                var py:int = sy + Math.floor((localY - b.y) / b.height * sh);
                px = Math.min(tw - 1, Math.max(0, px));
                py = Math.min(th - 1, Math.max(0, py));
                var argb:uint = _sourceBitmapData.getPixel32(px, py);
                return ((argb >>> 24) & 0xFF) >= AlphaHitThreshold;
            }
            catch (e:Error)
            {
                return true; // BitmapData 已被外部 dispose：放弃 alpha 判定，不阻断命中。
            }
        }

        // ── 父子（层级由 ECS Transform 管理，显示树挂载仅用于让 Starling 渲染。
        //        RenderEngine 的 root 是顶层容器。）──

        /** 父可绘制对象，无则 null。 */
        public function get parent():Drawable
        {
            var p:DisplayObject = _impl.parent;
            return p != null ? new Drawable(p) : null;
        }

        /** 从父级移除。Renderer.onDestroy 调用。 */
        public function removeFromParent():void { _impl.removeFromParent(); }

        // ── 遮罩（Stencil mask）──

        /**
         * 设置/清除遮罩对象：仅渲染该对象覆盖的区域，区域外不绘制。
         * 传入 null 清除。遮罩对象须处于同一显示列表范围内（本引擎 UI 分组容器
         * 使用同尺寸、alpha=0 的矩形作为遮罩图形，既可见性无影响又能写入模板缓冲）。
         */
        public function set mask(v:Drawable):void
        {
            _impl.mask = (v != null) ? v._impl : null;
        }

        /** 当前遮罩对象；无则 null。 */
        public function get mask():Drawable
        {
            var m:DisplayObject = _impl.mask;
            return (m != null) ? new Drawable(m) : null;
        }

        /** 反转遮罩：true 时仅渲染遮罩区域之外的部分。 */
        public function get maskInverted():Boolean { return _impl.maskInverted; }
        public function set maskInverted(v:Boolean):void { _impl.maskInverted = v; }

        /** 判断两个 Drawable 是否包装同一个 Starling 显示对象（拾取映射用）。 */
        public function sameAs(other:Drawable):Boolean
        {
            return other != null && other._impl === _impl;
        }
    }
}
