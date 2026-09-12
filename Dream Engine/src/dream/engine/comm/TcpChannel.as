package dream.engine.comm
{
	import flash.events.Event;
	import flash.events.EventDispatcher;
	import flash.events.IOErrorEvent;
	import flash.events.ProgressEvent;
	import flash.net.Socket;
	import flash.utils.ByteArray;

	CONFIG::STUDIO
	{
		/**
		 * 基于 Socket 的消息通道实现（客户端）：4 字节大端长度前缀 + UTF-8 JSON 帧。
		 * 通过 connect() 连接到 Studio 监听端口；SOCKET_DATA 事件驱动逐帧解析。
		 */
		public final class TcpChannel extends EventDispatcher implements IChannel
		{
			private var socket:Socket;
			private var pendingLength:uint = 0;

			public function TcpChannel(socket:Socket = null)
			{
				this.socket = socket != null ? socket : new Socket();
				this.socket.addEventListener(Event.CONNECT, onConnect);
				this.socket.addEventListener(ProgressEvent.SOCKET_DATA, onData);
				this.socket.addEventListener(Event.CLOSE, onClose);
				this.socket.addEventListener(IOErrorEvent.IO_ERROR, onError);
			}

			public function connect(host:String, port:int):void
			{
				socket.connect(host, port);
			}

			public function get connected():Boolean { return socket.connected; }

			public function send(message:Message):void
			{
				if (!socket.connected) return;
				var json:String = JSON.stringify({ type: message.type, payload: message.payload });
				var body:ByteArray = new ByteArray();
				body.writeUTFBytes(json);

				var header:ByteArray = new ByteArray();
				header.writeUnsignedInt(body.length); // ByteArray 默认大端

				socket.writeBytes(header);
				socket.writeBytes(body);
				socket.flush();
			}

			public function close():void
			{
				if (socket.connected) socket.close();
			}

			private function onConnect(event:Event):void
			{
				dispatchEvent(event.clone());
			}

			private function onData(event:ProgressEvent):void
			{
				// 循环消费所有完整帧；不足一帧时保留 pendingLength 等待下次数据。
				while (socket.bytesAvailable > 0)
				{
					if (pendingLength == 0)
					{
						if (socket.bytesAvailable < 4) return;
						pendingLength = socket.readUnsignedInt(); // 大端
					}
					if (socket.bytesAvailable < pendingLength) return;
					var body:ByteArray = new ByteArray();
					socket.readBytes(body, 0, pendingLength);
					pendingLength = 0;

					var json:String = body.readUTFBytes(body.length);
					var obj:Object = JSON.parse(json);
					dispatchEvent(new ChannelEvent(ChannelEvent.MESSAGE, new Message(obj.type, obj.payload)));
				}
			}

			private function onClose(event:Event):void
			{
				dispatchEvent(new ChannelEvent(ChannelEvent.CLOSED));
			}

			private function onError(event:IOErrorEvent):void
			{
				dispatchEvent(new ChannelEvent(ChannelEvent.CLOSED));
			}
		}
	}
}
