package dream.engine.comm.handlers
{
	CONFIG::STUDIO
	{
		import dream.engine.comm.IChannel;
		import dream.engine.comm.IMessageHandler;
		import dream.engine.comm.Message;
		import dream.engine.comm.MessageTypes;
		import dream.engine.render.ResourceManager;

		/**
		 * 图集映射处理器：接收 Studio 推送的「精灵 GUID → 图集纹理 + 图集内矩形」覆盖表。
		 *
		 * 图集是可选优化层：命中覆盖的精灵改用图集纹理渲染，未命中则回落到
		 * 精灵自身的源纹理——所以是否打包不影响场景引用，打包前后场景数据完全一致。
		 * 重新打包整表替换；传空 entries 即清除全部覆盖（回到未打包状态）。
		 *
		 * 消息格式（payload）：
		 *   { entries: [ { spriteGuid:"...", textureGuid:"...", x:0, y:0, w:256, h:256 }, ... ] }
		 */
		public final class SpriteAtlasMapHandler implements IMessageHandler
		{
			public function get messageType():String
			{
				return MessageTypes.SpriteAtlasMap;
			}

			public function handle(message:Message, channel:IChannel):void
			{
				var rm:ResourceManager = ResourceManager.current;
				if (rm == null) return;
				var payload:Object = message.payload;
				if (payload == null) return;
				rm.setAtlasEntries(payload.entries as Array);
			}
		}
	}
}
