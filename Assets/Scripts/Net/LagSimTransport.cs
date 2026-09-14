using System;
using System.Collections.Generic;

namespace Assets.Scripts.Net
{
	/// <summary>
	/// 延迟/抖动/丢包/重复 模拟传输层(装饰器,包住任意 IMpTransport)。
	///
	/// 用途:没有 Steam 好友时,用 TCP + 本地虚拟机联机无法暴露真实公网的延迟/抖动/丢包。
	/// 本类在"接收路径"(inner.OnDataReceived → 延迟队列 → 到点再触发上层 OnDataReceived)注入
	/// 可配置的网络条件,让接收端看到的包到达时间分布与公网一致 —— 从而暴露插值/平滑代码在高延迟、
	/// 抖动、丢包下的问题(缓冲欠载冻结-跳变、body 每包跳、渲染滞后等)。
	///
	/// 用法:
	///   1. 开房前设好静态配置:NetSimDelay 150 / NetSimJitter 30 / NetSimLoss 2 / NetSimDuplicate 0;
	///   2. TcpHostLobby / TcpJoinLobby(或 UI 按钮)创建传输时会自动包装(见 LagSimTransport.MaybeWrap);
	///   3. 会话进行中可直接改静态配置(NetSimDelay 200 等),已激活的实例逐包实时生效;
	///   4. NetSimReset 归零;NetSim 查看当前配置与投递统计。
	///
	/// 注意:对 MpNetworkManager 完全透明(它只看到 IMpTransport);收发路径中 SendTo/Broadcast 直通,
	/// 只在"接收"侧打延迟 —— 对端到达时间分布与真实网络一致,足够复现插值层问题。
	/// </summary>
	public class LagSimTransport : IMpTransport
	{
		// ---------------- 静态配置(UI Toggle/输入框 + 控制台命令修改;开房命令据此决定是否包装) ----------------
		/// <summary>延迟模拟总开关(UI Toggle / NetSimOn / NetSimOff)。关闭=直通(不延迟、不丢包),
		/// 保证其它场景(如普通 TCP 联机)延迟尽量小。数值设置不会自动打开此开关。</summary>
		public static bool ToggleOn;
		/// <summary>基础单向延迟(ms)。</summary>
		public static int DelayMs;
		/// <summary>均匀抖动 ±JitterMs(ms)。</summary>
		public static int JitterMs;
		/// <summary>丢包率(0..100)。</summary>
		public static float LossPercent;
		/// <summary>重复包率(0..100,模拟 UDP 乱序重复)。</summary>
		public static float DuplicatePercent;

		/// <summary>是否实际生效(总开关开 + 有任一数值)。</summary>
		public static bool Enabled => ToggleOn && (DelayMs > 0 || JitterMs > 0 || LossPercent > 0 || DuplicatePercent > 0);

		/// <summary>设置总开关(不修改数值)。</summary>
		public static void SetToggle(bool on) => ToggleOn = on;

		/// <summary>设置延迟(ms,只设数值,不开总开关)。</summary>
		public static void SetDelay(int ms) => DelayMs = Math.Max(0, ms);

		/// <summary>设置抖动(ms,只设数值,不开总开关)。</summary>
		public static void SetJitter(int ms) => JitterMs = Math.Max(0, ms);

		/// <summary>设置丢包率(0..100,只设数值,不开总开关)。</summary>
		public static void SetLoss(float pct) => LossPercent = Math.Max(0f, Math.Min(100f, pct));

		/// <summary>设置重复包率(0..100,只设数值,不开总开关)。</summary>
		public static void SetDuplicate(float pct) => DuplicatePercent = Math.Max(0f, Math.Min(100f, pct));

		/// <summary>
		/// 按当前静态配置决定是否包装:未启用(总开关关或无数值)时原样返回 inner(保证 LobbyManager 的
		/// "Transport is SteamTransport" 判断等类型检查仍成立);启用时包一层 LagSimTransport。
		/// </summary>
		public static IMpTransport MaybeWrap(IMpTransport inner)
		{
			if (inner == null || !Enabled) return inner;
			// Steam 暂不包装:LobbyManager.HostLobby 依赖 "Transport is SteamTransport" 做 SteamId 预检,
			// 包装会破坏该判断。NetSim 的主要场景是 TCP debug(无此限制);需要 Steam+延迟模拟时再扩展。
			if (inner is SteamTransport) return inner;
			Mod.LogLobby("LagSimTransport.MaybeWrap: wrapping " + inner.GetType().Name +
				" (delay=" + DelayMs + "ms, jitter=" + JitterMs + "ms, loss=" + LossPercent +
				"%, dup=" + DuplicatePercent + "%)");
			return new LagSimTransport(inner);
		}

		/// <summary>重置静态配置(关总开关 + 数值全归零)。</summary>
		public static void ResetConfig()
		{
			ToggleOn = false;
			DelayMs = 0;
			JitterMs = 0;
			LossPercent = 0;
			DuplicatePercent = 0;
		}

		/// <summary>当前静态配置摘要(NetSim 命令/日志用)。</summary>
		public static string DescribeConfig()
		{
			if (!ToggleOn) return "net-sim OFF";
			if (DelayMs <= 0 && JitterMs <= 0 && LossPercent <= 0 && DuplicatePercent <= 0)
				return "net-sim ON (数值未设,直通)";
			return "net-sim ON: delay=" + DelayMs + "ms jitter=" + JitterMs + "ms loss=" +
				LossPercent + "% dup=" + DuplicatePercent + "%";
		}

		// ---------------- 实例 ----------------

		private readonly IMpTransport _inner;
		private readonly object _lock = new object();
		private readonly List<QueuedPacket> _pending = new List<QueuedPacket>();
		private readonly Random _rng = new Random();

		/// <summary>已到点投递数。</summary>
		public long Delivered;
		/// <summary>被丢弃的包数。</summary>
		public long Dropped;
		/// <summary>队列中尚未到点的包数。</summary>
		public long InFlight => _pending.Count;

		/// <summary>毫秒级时间戳（纯 .NET，与 TcpTransport 同款实现）。</summary>
		private static long NowMs => DateTime.UtcNow.Ticks / TimeSpan.TicksPerMillisecond;

		public event Action<MpPeer, byte[]> OnDataReceived;
		public event Action<MpPeer> OnPeerTimeout;

		public LagSimTransport(IMpTransport inner)
		{
			_inner = inner;
			inner.OnDataReceived += OnInnerData;
			inner.OnPeerTimeout += OnPeerTimeout; // 超时/断开直接转发(不延迟)
		}

		private void OnInnerData(MpPeer peer, byte[] data)
		{
			// 总开关关闭:直通(不延迟、不丢包)——保证其它 TCP 场景延迟尽量小
			if (!ToggleOn)
			{
				try { OnDataReceived?.Invoke(peer, data); }
				catch (Exception e) { Mod.LogError("LagSimTransport pass-through error: " + e.Message); }
				return;
			}
			// 丢包
			if (LossPercent > 0f && _rng.NextDouble() * 100.0 < LossPercent)
			{
				Dropped++;
				return;
			}
			long delay = DelayMs;
			if (JitterMs > 0) delay += _rng.Next(-JitterMs, JitterMs + 1);
			if (delay < 0) delay = 0;
			long due = NowMs + delay;
			lock (_lock) _pending.Add(new QueuedPacket { DueMs = due, Peer = peer, Data = data });
			// 重复包(模拟 UDP 重复/乱序导致的重复投递)
			if (DuplicatePercent > 0f && _rng.NextDouble() * 100.0 < DuplicatePercent)
			{
				lock (_lock) _pending.Add(new QueuedPacket { DueMs = due, Peer = peer, Data = data });
			}
		}

		public void DrainIncoming()
		{
			// ① 先把底层传输的接收队列拉空(触发 inner.OnDataReceived → OnInnerData 进入延迟队列);
			_inner.DrainIncoming();
			// ② 投递已到点的包。
			long now = NowMs;
			List<QueuedPacket> due = null;
			lock (_lock)
			{
				if (_pending.Count > 0)
				{
					due = new List<QueuedPacket>(Math.Min(_pending.Count, 64));
					for (int i = _pending.Count - 1; i >= 0; i--)
					{
						if (_pending[i].DueMs <= now)
						{
							due.Add(_pending[i]);
							_pending.RemoveAt(i);
						}
					}
				}
			}
			if (due == null) return;
			foreach (QueuedPacket p in due)
			{
				Delivered++;
				try { OnDataReceived?.Invoke(p.Peer, p.Data); }
				catch (Exception e) { Mod.LogError("LagSimTransport.OnDataReceived error: " + e.Message); }
			}
		}

		/// <summary>投递统计摘要(NetSim 命令/日志用)。</summary>
		public string DescribeStats()
		{
			return DescribeConfig() + " | delivered=" + Delivered + " dropped=" + Dropped + " inFlight=" + InFlight;
		}

		// ---------------- 生命周期直通 ----------------

		public int LocalPort => _inner.LocalPort;
		public bool IsRunning => _inner.IsRunning;

		public bool Start(int port) => _inner.Start(port);
		public bool StartClient(string host, int port, byte[] helloPacket) => _inner.StartClient(host, port, helloPacket);

		public void Stop()
		{
			lock (_lock) _pending.Clear(); // 丢弃未投递的延迟包(会话已结束)
			_inner.Stop();
		}

		public void SendTo(MpPeer peer, byte[] data) => _inner.SendTo(peer, data);
		public void Broadcast(byte[] data) => _inner.Broadcast(data);
		public void CheckTimeouts(long timeoutMs) => _inner.CheckTimeouts(timeoutMs);
		public IReadOnlyCollection<MpPeer> GetPeers() => _inner.GetPeers();
		public int GetPeersCount() => _inner.GetPeersCount();
		public void DisconnectPeer(MpPeer peer) => _inner.DisconnectPeer(peer);

		public void Dispose()
		{
			_inner.OnDataReceived -= OnInnerData;
			_inner.OnPeerTimeout -= OnPeerTimeout;
			lock (_lock) _pending.Clear();
			_inner.Dispose();
		}

		private struct QueuedPacket
		{
			public long DueMs;
			public MpPeer Peer;
			public byte[] Data;
		}
	}
}
