package dream.engine.render
{
    import flash.geom.Rectangle;
    import flash.utils.Dictionary;

    /**
     * 精灵表资源（.dmsheet）：一张源纹理上切出的命名精灵定义集合。
     *
     * 定位（对标 Unity 的 Sprite 子资源）：精灵是组件引用的**主单位**，
     * 每个精灵拥有自己的 GUID，可被单独引用与拖拽；它自带「源纹理 + 像素矩形」，
     * 与图集打包**完全无关**——切分完即可用，不需要先打包。
     *
     * 图集（.dmatlas）是可选覆盖层：打包后由 Studio 推送「精灵 GUID → 图集纹理 + 矩形」
     * 映射，ResourceManager 命中映射时透明改用图集纹理，未命中则回落到本表的源纹理。
     *
     * JSON 约定（与 Studio 写出格式一致）：
     *   {
     *     "name": "robot_sheet_2x2",
     *     "textureGuid": "<源纹理 GUID>",
     *     "sprites": [ { "guid":"<精灵 GUID>", "name":"robot_sheet_2x2_0_0",
     *                    "x":0, "y":0, "w":256, "h":256 }, ... ]
     *   }
     */
    public final class SpriteSheet
    {
        /** 精灵表名称（通常为源纹理文件名）。 */
        public var name:String = "";

        /** 源纹理的资源 GUID；各精灵的矩形都以该纹理的像素坐标系表达。 */
        public var textureGuid:String;

        // 精灵 GUID → SpriteDef
        private var _byGuid:Dictionary = new Dictionary();

        public function SpriteSheet() {}

        /** 精灵数量。 */
        public function get spriteCount():int
        {
            var n:int = 0;
            for each (var d:SpriteDef in _byGuid) n++;
            return n;
        }

        /** 按精灵 GUID 取定义；不存在返回 null。 */
        public function getSpriteByGuid(guid:String):SpriteDef
        {
            if (guid == null) return null;
            return _byGuid[guid] as SpriteDef;
        }

        /** 登记一个精灵。GUID 为空则忽略。 */
        internal function addSprite(guid:String, spriteName:String, rect:Rectangle):void
        {
            if (guid == null || guid.length == 0 || rect == null) return;
            var d:SpriteDef = new SpriteDef();
            d.guid = guid;
            d.name = (spriteName != null) ? spriteName : "";
            d.rect = rect;
            _byGuid[guid] = d;
        }

        /** 从 JSON 对象构建。data 为 null 时返回空表（非 null）。 */
        public static function fromJson(data:Object):SpriteSheet
        {
            var sheet:SpriteSheet = new SpriteSheet();
            if (data == null) return sheet;
            if (data.name != null) sheet.name = String(data.name);
            if (data.textureGuid != null) sheet.textureGuid = String(data.textureGuid);

            var list:Array = data.sprites as Array;
            if (list != null)
            {
                for each (var s:Object in list)
                {
                    if (s == null || s.guid == null) continue;
                    sheet.addSprite(String(s.guid), String(s.name),
                        new Rectangle(Number(s.x), Number(s.y), Number(s.w), Number(s.h)));
                }
            }
            return sheet;
        }
    }
}
