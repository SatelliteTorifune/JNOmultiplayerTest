using System;
using System.Collections.Generic;
using Assets.Scripts.Net;
using UnityEngine;

namespace Assets.Scripts.Net.Session
{
	/// <summary>
	/// 聊天会话模型(plans/text-chat-2026-09-24.md §四 M0/M3):
	/// 环形缓冲(100 条) + 新消息事件(驱动 UI) + 名字解析(Registry) + ChatPolicy 过滤接口 + 会话结束清空。
	/// 过滤三件套(每玩家屏蔽 / BadWord / RTF)2026-09-24 拍板**暂不做**,只在 Policy 留扩展点,默认全放行/恒等。
	/// 消息入列统一走 Add(接收端/本地系统消息);发送走 Send(本端出口,经房主中继通道)。
	/// </summary>
	internal class ChatSession
	{
		/// <summary>系统消息的 PlayerId(join/leave 等本地生成,不经网络;SP2 同款语义)。</summary>
		public const int SystemPlayerId = -1;

		/// <summary>单条消息字符上限(SP2 同款 500)。</summary>
		public const int MaxMessageLength = 500;

		private const int MaxMessages = 100; // 环形缓冲上限,旧消息顶掉最旧一条

		private readonly NetworkManager _multiPlayer;
		private readonly List<ChatMessage> _messages = new List<ChatMessage>();

		internal ChatSession(NetworkManager multiPlayer) { _multiPlayer = multiPlayer; }

		/// <summary>新消息入列后触发(主线程:传输层 DrainIncoming 在 NetworkManager.Update 里泵)。聊天窗/预览条订阅刷新。</summary>
		public event Action<ChatMessage> MessageReceived;

		/// <summary>过滤扩展点(默认全放行/恒等,见 ChatPolicy)。</summary>
		public ChatPolicy Policy { get; } = new ChatPolicy();

		/// <summary>会话消息(最新在末尾;UI 渲染顺序即此列表顺序)。</summary>
		public IReadOnlyList<ChatMessage> Messages => _messages;

		/// <summary>
		/// 发送一条聊天(本端出口):Trim + 空白丢弃 + 500 字截断,走 SendOrBroadcastToNet(房主广播 / 客户端发房主中继)。
		/// 房主本地立即入列(广播不回自身);客户端等房主广播回来再入列(同 SP2 服务器回显语义)。
		/// </summary>
		public void Send(string text)
		{
			text = (text ?? string.Empty).Trim();
			if (text.Length == 0) return;
			if (text.Length > MaxMessageLength) text = text.Substring(0, MaxMessageLength);
			_multiPlayer.SendOrBroadcastToNet(MultiPlayerMessages.EncodeChat(_multiPlayer.PlayerId, text));
			if (_multiPlayer.IsServer) Add(_multiPlayer.PlayerId, text);
		}

		/// <summary>
		/// 入列一条消息(接收端 Router.OnChat / 本地系统消息统一入口):
		/// 名字解析(Registry)+ Policy.AllowDisplay / Sanitize + 500 字截断 + 环形缓冲裁剪 + 事件。
		/// </summary>
		internal void Add(int playerId, string text)
		{
			if (string.IsNullOrEmpty(text)) return;
			if (!Policy.AllowDisplay(playerId)) return;
			text = Policy.Sanitize(text) ?? string.Empty;
			if (text.Length == 0) return;
			if (text.Length > MaxMessageLength) text = text.Substring(0, MaxMessageLength);
			MultiPlayerPeer peer = playerId == SystemPlayerId ? null : _multiPlayer.Registry.GetPlayer(playerId);
			string name = peer != null ? peer.PlayerName : null;
			// 本机玩家不在 Registry 里(房主自身从不登记;客户端在 OnPlayerJoin 里跳过自己)
			// ⇒ 自己发的消息用会话名兜底(来源 ModSettings,Host/Join 时已取),否则显示成 "Player 0"。
			if (string.IsNullOrEmpty(name) && playerId == _multiPlayer.PlayerId) name = _multiPlayer.PlayerName;
			var msg = new ChatMessage
			{
				PlayerId = playerId,
				PlayerName = playerId == SystemPlayerId ? null : name,
				Text = text,
				Timestamp = Time.unscaledTime,
			};
			_messages.Add(msg);
			while (_messages.Count > MaxMessages) _messages.RemoveAt(0);
			MessageReceived?.Invoke(msg);
		}

		/// <summary>本地系统消息(join/leave 等;SP2 同款:两端各自本地生成,不经网络)。</summary>
		internal void AddSystem(string text) { Add(SystemPlayerId, text); }

		/// <summary>会话结束清空(挂进 NetworkManager.Stop 复位清单;新会话从空白开始)。</summary>
		internal void Clear() { _messages.Clear(); }
	}

	/// <summary>
	/// 聊天过滤扩展点(2026-09-24 拍板:脏词/屏蔽暂不做,默认全放行;`Sanitize` 默认恒等 = 允许玩家 RTF)。
	/// 未来实现:每玩家本地屏蔽(AllowDisplay)/ BadWord(AllowRelay)/ URL 处理与关键词过滤(Sanitize)。
	/// </summary>
	public class ChatPolicy
	{
		/// <summary>房主转发前(未来:BadWord / 防刷屏限速)。senderPlayerId 为房主重写后的连接身份。</summary>
		public virtual bool AllowRelay(int senderPlayerId, string text) => true;

		/// <summary>接收端入列前(未来:每玩家本地屏蔽,纯接收端实现,不影响他人)。</summary>
		public virtual bool AllowDisplay(int playerId) => true;

		/// <summary>显示前(未来:URL 处理 / 关键词过滤)。**默认恒等**——【决策:2026-09-24】允许玩家发 RTF(§四 M3):
		/// TMP 显示层会正常解析玩家自带的富文本标签;**玩家名与正文同等对待**(名字里的标签也生效,显示层用
		/// "不套自有配色 + 行尾收口"规避标记冲突与串染,见 MultiPlayerChatWindow.FormatLine)。</summary>
		public virtual string Sanitize(string text) => text;
	}

	/// <summary>一条聊天消息(PlayerId=-1 为系统消息,名字为空;PlayerName 未解析到时 UI 回退 "Player {id}")。</summary>
	public class ChatMessage
	{
		public int PlayerId;
		public string PlayerName;
		public string Text;
		public float Timestamp; // 接收端 Time.unscaledTime
		public bool IsSystem => PlayerId == ChatSession.SystemPlayerId;
	}
}
