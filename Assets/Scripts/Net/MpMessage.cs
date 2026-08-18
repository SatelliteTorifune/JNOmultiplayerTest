using System;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Collections.Generic;
using UnityEngine;

namespace Assets.Scripts.Net
{
	/// <summary>
	/// 联机消息类型定义。
	/// SP2 方案：PlayerJoin 只广播"谁加入了 + 飞船 hash"，不带飞船 XML；
	/// 客户端收到后若本地缓存无该 hash，则发 CraftXmlRequest 按需拉取 XML。
	/// </summary>
	public enum MpMessageType : byte
	{
		Hello = 1,       // 加入者 -> 房主：请求加入（含玩家名）
		Welcome = 2,     // 房主 -> 加入者：欢迎（分配 PlayerId / 初始 NodeId）
		PlayerJoin = 3,  // 房主 -> 所有：通知玩家加入/飞船更新（含 playerId/nodeId/name + craft XML hash，不含 XML）
		PlayerLeave = 4, // 房主 -> 所有：通知玩家离开
		State = 5,       // 状态包：NodeId + 时间戳 + recdata
		Pause = 6,       // 暂停 / 恢复广播
		CraftData = 7,   // 客户端 -> 房主：把自己的飞船 XML 上报给房主
		Ping = 8,        // RTT 探测
		Pong = 9,
		CraftDataAck = 10, // 房主 -> 客户端：确认已收到其飞船（nodeId），客户端据此停止重发
		PlayerJoinAck = 11, // 客户端 -> 房主：确认已收到指定玩家（playerId）的飞船信息，房主据此停止重发 PlayerJoin
		CraftXmlRequest = 12,  // 客户端 -> 房主：按需请求指定玩家（playerId）的飞船 XML（SP2 方案）
		CraftXmlResponse = 13, // 房主 -> 客户端：返回指定玩家的飞船 XML（大包，走可靠通道）
		TickRate = 14,         // 房主 -> 所有：当前状态包发送频率（Hz），客户端据此调整发包节奏与插值
		Kick = 15,             // 房主 -> 指定客户端：你被房主踢出（随后断开连接）
	}

	/// <summary>
	/// 消息封装与序列化。所有消息均为二进制紧凑格式：
	/// [msgType:1][payload...]
	/// 状态包：NodeId(int) + FlightState.Time(double) + recdata
	/// </summary>
	public static class MpMessages
	{
		// ---------------- 基础封装 ----------------

		private static byte[] Pack(MpMessageType type, Action<BinaryWriter> writePayload)
		{
			using (MemoryStream ms = new MemoryStream())
			using (BinaryWriter w = new BinaryWriter(ms))
			{
				w.Write((byte)type);
				writePayload(w);
				return ms.ToArray();
			}
		}

		public static MpMessageType PeekType(byte[] buffer)
		{
			if (buffer == null || buffer.Length < 1) return 0;
			return (MpMessageType)buffer[0];
		}

		// ---------------- craft XML 压缩 ----------------
		// 借鉴 SP2（Utility.CompressCraftXml）：飞船 XML 体积大（数百 KB），
		// 跨公网/UDP 传输前必须先压缩，可缩小 5~10 倍、大幅减少分片数量。

		/// <summary>压缩飞船 XML（UTF-8 + GZip）。空串也返回非空字节，保证可逆。</summary>
		public static byte[] CompressXml(string xml)
		{
			byte[] raw = Encoding.UTF8.GetBytes(xml ?? string.Empty);
			using (MemoryStream ms = new MemoryStream())
			{
				using (GZipStream gz = new GZipStream(ms, CompressionMode.Compress, true))
				{
					gz.Write(raw, 0, raw.Length);
				}
				return ms.ToArray();
			}
		}

		/// <summary>解压飞船 XML。损坏/空输入返回空字符串（由调用方容错）。</summary>
		public static string DecompressXml(byte[] compressed)
		{
			if (compressed == null || compressed.Length == 0) return string.Empty;
			try
			{
				using (MemoryStream ms = new MemoryStream(compressed))
				using (GZipStream gz = new GZipStream(ms, CompressionMode.Decompress))
				using (MemoryStream outMs = new MemoryStream())
				{
					gz.CopyTo(outMs);
					return Encoding.UTF8.GetString(outMs.ToArray());
				}
			}
			catch { return string.Empty; }
		}

		// ---------------- Hello / Welcome ----------------

		public static byte[] EncodeHello(string playerName)
		{
			return Pack(MpMessageType.Hello, w => w.Write(playerName ?? "Player"));
		}

		public static bool TryDecodeHello(byte[] buffer, out string playerName)
		{
			playerName = "Player";
			try
			{
				using (MemoryStream ms = new MemoryStream(buffer))
				using (BinaryReader r = new BinaryReader(ms))
				{
					byte type = r.ReadByte();
					if (type != (byte)MpMessageType.Hello) return false;
					playerName = r.ReadString();
					return true;
				}
			}
			catch { return false; }
		}

		public static byte[] EncodeWelcome(int playerId, int nodeId, long serverTick)
		{
			return Pack(MpMessageType.Welcome, w =>
			{
				w.Write(playerId);
				w.Write(nodeId);
				w.Write(serverTick);
			});
		}

		public static bool TryDecodeWelcome(byte[] buffer, out int playerId, out int nodeId, out long serverTick)
		{
			playerId = -1; nodeId = -1; serverTick = 0;
			try
			{
				using (MemoryStream ms = new MemoryStream(buffer))
				using (BinaryReader r = new BinaryReader(ms))
				{
					if (r.ReadByte() != (byte)MpMessageType.Welcome) return false;
					playerId = r.ReadInt32();
					nodeId = r.ReadInt32();
					serverTick = r.ReadInt64();
					return true;
				}
			}
			catch { return false; }
		}

		// ---------------- PlayerJoin / PlayerLeave ----------------
		// SP2 方案：PlayerJoin 只广播"谁加入了 + 飞船 hash"，不带飞船 XML。
		// 客户端收到后若本地缓存无该 hash，则发 CraftXmlRequest 向房主按需拉取 XML（见下），
		// 避免新玩家加入时把所有人的大 XML 全量广播（SP2 的成熟做法）。

		/// <summary>飞船 XML 的稳定 hash（用于按需下载缓存去重）。</summary>
		public static string ComputeXmlHash(string craftXml)
		{
			using (System.Security.Cryptography.MD5 md5 = System.Security.Cryptography.MD5.Create())
			{
				byte[] raw = Encoding.UTF8.GetBytes(craftXml ?? string.Empty);
				byte[] hash = md5.ComputeHash(raw);
				return BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
			}
		}

		public static byte[] EncodePlayerJoin(int playerId, int nodeId, string playerName, string craftXmlHash)
		{
			return Pack(MpMessageType.PlayerJoin, w =>
			{
				w.Write(playerId);
				w.Write(nodeId);
				w.Write(playerName ?? string.Empty);
				w.Write(craftXmlHash ?? string.Empty);
			});
		}

		public static bool TryDecodePlayerJoin(byte[] buffer, out int playerId, out int nodeId, out string playerName, out string craftXmlHash)
		{
			playerId = -1; nodeId = -1; playerName = null; craftXmlHash = null;
			try
			{
				using (MemoryStream ms = new MemoryStream(buffer))
				using (BinaryReader r = new BinaryReader(ms))
				{
					if (r.ReadByte() != (byte)MpMessageType.PlayerJoin) return false;
					playerId = r.ReadInt32();
					nodeId = r.ReadInt32();
					playerName = r.ReadString();
					craftXmlHash = r.ReadString();
					return true;
				}
			}
			catch { return false; }
		}

		public static byte[] EncodePlayerLeave(int playerId)
		{
			return Pack(MpMessageType.PlayerLeave, w => w.Write(playerId));
		}

		public static bool TryDecodePlayerLeave(byte[] buffer, out int playerId)
		{
			playerId = -1;
			try
			{
				using (MemoryStream ms = new MemoryStream(buffer))
				using (BinaryReader r = new BinaryReader(ms))
				{
					if (r.ReadByte() != (byte)MpMessageType.PlayerLeave) return false;
					playerId = r.ReadInt32();
					return true;
				}
			}
			catch { return false; }
		}

		// ---------------- State（核心状态包） ----------------

		/// <summary>
		/// 状态包格式：PlayerId(int) + NodeId(int) + FlightState.Time(double) + recdata。
		/// PlayerId 由房主分配，全局唯一，用于寻址；NodeId 为发送方本机飞船节点号（各机之间可重复）。
		/// </summary>
		public static byte[] EncodeState(int playerId, int nodeId, double time, Mod.RemoteDataPack data)
		{
			return Pack(MpMessageType.State, w =>
			{
				w.Write(playerId);
				w.Write(nodeId);
				w.Write(time);
				WriteRecdata(w, data);
			});
		}

		public static bool TryDecodeState(byte[] buffer, out int playerId, out int nodeId, out double time, out Mod.RemoteDataPack data)
		{
			playerId = -1; nodeId = -1; time = 0; data = new Mod.RemoteDataPack();
			try
			{
				using (MemoryStream ms = new MemoryStream(buffer))
				using (BinaryReader r = new BinaryReader(ms))
				{
					if (r.ReadByte() != (byte)MpMessageType.State) return false;
					playerId = r.ReadInt32();
					nodeId = r.ReadInt32();
					time = r.ReadDouble();
					data = ReadRecdata(r);
					return true;
				}
			}
			catch { return false; }
		}

		// ---------------- CraftData（本机飞船信息上报） ----------------

		public static byte[] EncodeCraftData(int nodeId, string craftXml)
		{
			return Pack(MpMessageType.CraftData, w =>
			{
				w.Write(nodeId);
				byte[] xmlBytes = CompressXml(craftXml ?? string.Empty);
				w.Write(xmlBytes.Length);
				w.Write(xmlBytes);
			});
		}

		public static bool TryDecodeCraftData(byte[] buffer, out int nodeId, out string craftXml)
		{
			nodeId = -1; craftXml = null;
			try
			{
				using (MemoryStream ms = new MemoryStream(buffer))
				using (BinaryReader r = new BinaryReader(ms))
				{
					if (r.ReadByte() != (byte)MpMessageType.CraftData) return false;
					nodeId = r.ReadInt32();
					int len = r.ReadInt32();
					craftXml = DecompressXml(r.ReadBytes(len));
					return true;
				}
			}
			catch { return false; }
		}

		// ---------------- CraftDataAck（房主确认收到加入者飞船） ----------------

		public static byte[] EncodeCraftDataAck(int nodeId)
		{
			return Pack(MpMessageType.CraftDataAck, w => w.Write(nodeId));
		}

		public static bool TryDecodeCraftDataAck(byte[] buffer, out int nodeId)
		{
			nodeId = -1;
			try
			{
				using (MemoryStream ms = new MemoryStream(buffer))
				using (BinaryReader r = new BinaryReader(ms))
				{
					if (r.ReadByte() != (byte)MpMessageType.CraftDataAck) return false;
					nodeId = r.ReadInt32();
					return true;
				}
			}
			catch { return false; }
		}

		// ---------------- PlayerJoinAck（加入者确认收到指定玩家飞船 XML） ----------------

		public static byte[] EncodePlayerJoinAck(int playerId)
		{
			return Pack(MpMessageType.PlayerJoinAck, w => w.Write(playerId));
		}

		public static bool TryDecodePlayerJoinAck(byte[] buffer, out int playerId)
		{
			playerId = -1;
			try
			{
				using (MemoryStream ms = new MemoryStream(buffer))
				using (BinaryReader r = new BinaryReader(ms))
				{
					if (r.ReadByte() != (byte)MpMessageType.PlayerJoinAck) return false;
					playerId = r.ReadInt32();
					return true;
				}
			}
			catch { return false; }
		}

		// ---------------- CraftXmlRequest / CraftXmlResponse（SP2 按需下载） ----------------

		/// <summary>客户端 -> 房主：请求指定玩家（playerId）的飞船 XML（带其 hash 供房主校验）。</summary>
		public static byte[] EncodeCraftXmlRequest(int playerId, string craftXmlHash)
		{
			return Pack(MpMessageType.CraftXmlRequest, w =>
			{
				w.Write(playerId);
				w.Write(craftXmlHash ?? string.Empty);
			});
		}

		public static bool TryDecodeCraftXmlRequest(byte[] buffer, out int playerId, out string craftXmlHash)
		{
			playerId = -1; craftXmlHash = null;
			try
			{
				using (MemoryStream ms = new MemoryStream(buffer))
				using (BinaryReader r = new BinaryReader(ms))
				{
					if (r.ReadByte() != (byte)MpMessageType.CraftXmlRequest) return false;
					playerId = r.ReadInt32();
					craftXmlHash = r.ReadString();
					return true;
				}
			}
			catch { return false; }
		}

		/// <summary>房主 -> 客户端：返回指定玩家的飞船 XML（压缩，大包自动分片）。</summary>
		public static byte[] EncodeCraftXmlResponse(int playerId, string craftXmlHash, string craftXml)
		{
			return Pack(MpMessageType.CraftXmlResponse, w =>
			{
				w.Write(playerId);
				w.Write(craftXmlHash ?? string.Empty);
				byte[] xmlBytes = CompressXml(craftXml ?? string.Empty);
				w.Write(xmlBytes.Length);
				w.Write(xmlBytes);
			});
		}

		public static bool TryDecodeCraftXmlResponse(byte[] buffer, out int playerId, out string craftXmlHash, out string craftXml)
		{
			playerId = -1; craftXmlHash = null; craftXml = null;
			try
			{
				using (MemoryStream ms = new MemoryStream(buffer))
				using (BinaryReader r = new BinaryReader(ms))
				{
					if (r.ReadByte() != (byte)MpMessageType.CraftXmlResponse) return false;
					playerId = r.ReadInt32();
					craftXmlHash = r.ReadString();
					int len = r.ReadInt32();
					craftXml = DecompressXml(r.ReadBytes(len));
					return true;
				}
			}
			catch { return false; }
		}

		// ---------------- Ping ----------------

		public static byte[] EncodePing(long tick)
		{
			return Pack(MpMessageType.Ping, w => w.Write(tick));
		}

		public static byte[] EncodePong(long tick)
		{
			return Pack(MpMessageType.Pong, w => w.Write(tick));
		}

		public static bool TryDecodePing(byte[] buffer, out long tick)
		{
			tick = 0;
			try
			{
				using (MemoryStream ms = new MemoryStream(buffer))
				using (BinaryReader r = new BinaryReader(ms))
				{
					if (r.ReadByte() != (byte)MpMessageType.Ping) return false;
					tick = r.ReadInt64();
					return true;
				}
			}
			catch { return false; }
		}

		public static bool TryDecodePong(byte[] buffer, out long tick)
		{
			tick = 0;
			try
			{
				using (MemoryStream ms = new MemoryStream(buffer))
				using (BinaryReader r = new BinaryReader(ms))
				{
					if (r.ReadByte() != (byte)MpMessageType.Pong) return false;
					tick = r.ReadInt64();
					return true;
				}
			}
			catch { return false; }
		}

		// ---------------- Kick（房主 -> 指定客户端：你被踢出） ----------------

		public static byte[] EncodeKick()
		{
			return Pack(MpMessageType.Kick, _ => { });
		}

		public static bool TryDecodeKick(byte[] buffer)
		{
			try
			{
				using (MemoryStream ms = new MemoryStream(buffer))
				using (BinaryReader r = new BinaryReader(ms))
				{
					return r.ReadByte() == (byte)MpMessageType.Kick;
				}
			}
			catch { return false; }
		}

		// ---------------- TickRate（房主 -> 客户端：状态包发送频率） ----------------

		public static byte[] EncodeTickRate(int hz)
		{
			return Pack(MpMessageType.TickRate, w => w.Write(hz));
		}

		public static bool TryDecodeTickRate(byte[] buffer, out int hz)
		{
			hz = 20;
			try
			{
				using (MemoryStream ms = new MemoryStream(buffer))
				using (BinaryReader r = new BinaryReader(ms))
				{
					if (r.ReadByte() != (byte)MpMessageType.TickRate) return false;
					hz = r.ReadInt32();
					return true;
				}
			}
			catch { return false; }
		}

		// ---------------- recdata 序列化 ----------------

		public static void WriteRecdata(BinaryWriter w, Mod.RemoteDataPack d)
		{
			w.Write(d.Position.x); w.Write(d.Position.y); w.Write(d.Position.z);
			w.Write(d.Velocity.x); w.Write(d.Velocity.y); w.Write(d.Velocity.z);
			w.Write(d.Heading.x); w.Write(d.Heading.y); w.Write(d.Heading.z); w.Write(d.Heading.w);
			w.Write(d.SrfRel.x); w.Write(d.SrfRel.y); w.Write(d.SrfRel.z); w.Write(d.SrfRel.w);

			w.Write(d.Pitch); w.Write(d.Yaw); w.Write(d.Roll);
			w.Write(d.Throttle); w.Write(d.Brake);
			w.Write(d.Slider1); w.Write(d.Slider2); w.Write(d.Slider3); w.Write(d.Slider4);
			w.Write(d.TranslateForward); w.Write(d.TranslateRight); w.Write(d.TranslateUp);
			w.Write(d.Stage);

			int count = d.ActivationGroupStates == null ? 0 : d.ActivationGroupStates.Count;
			w.Write(count);
			if (count > 0)
			{
				for (int i = 0; i < count; i++) w.Write(d.ActivationGroupStates[i]);
			}

			// body 局部姿态（相对根，欧拉角）：远程端据此复现飞船 body 朝向，避免"分裂/散架"
			int bodyCount = d.BodyRotations == null ? 0 : d.BodyRotations.Count;
			w.Write(bodyCount);
			for (int i = 0; i < bodyCount; i++)
			{
				Vector3 br = d.BodyRotations[i];
				w.Write(br.x); w.Write(br.y); w.Write(br.z);
			}

			// body 局部位置(相对 comRot,body-sync P0):与 BodyRotations 平行同索引;远程端据此复现转轴/关节连接的子装配"整体移动"
			int bpCount = d.BodyPositions == null ? 0 : d.BodyPositions.Count;
			w.Write(bpCount);
			for (int i = 0; i < bpCount; i++)
			{
				Vector3 bp = d.BodyPositions[i];
				w.Write(bp.x); w.Write(bp.y); w.Write(bp.z);
			}

			// 每引擎视觉 throttle(尾焰同步)：与发送端引擎枚举顺序一一对应
			int etCount = d.EngineThrottles == null ? 0 : d.EngineThrottles.Count;
			w.Write(etCount);
			for (int i = 0; i < etCount; i++) w.Write(d.EngineThrottles[i]);

			// 每部件开关状态(方案 B)：与发送端 Data.Assembly.Parts 顺序一一对应
			int paCount = d.PartActivated == null ? 0 : d.PartActivated.Count;
			w.Write(paCount);
			for (int i = 0; i < paCount; i++) w.Write(d.PartActivated[i]);
		}

		public static Mod.RemoteDataPack ReadRecdata(BinaryReader r)
		{
			Mod.RemoteDataPack d = new Mod.RemoteDataPack(
				new Vector3d(r.ReadDouble(), r.ReadDouble(), r.ReadDouble()),
				new Vector3d(r.ReadDouble(), r.ReadDouble(), r.ReadDouble()),
				new Quaterniond(r.ReadDouble(), r.ReadDouble(), r.ReadDouble(), r.ReadDouble())
			);
			d.SrfRel = new Quaterniond(r.ReadDouble(), r.ReadDouble(), r.ReadDouble(), r.ReadDouble());
			d.Pitch = r.ReadSingle();
			d.Yaw = r.ReadSingle();
			d.Roll = r.ReadSingle();
			d.Throttle = r.ReadSingle();
			d.Brake = r.ReadSingle();
			d.Slider1 = r.ReadSingle();
			d.Slider2 = r.ReadSingle();
			d.Slider3 = r.ReadSingle();
			d.Slider4 = r.ReadSingle();
			d.TranslateForward = r.ReadSingle();
			d.TranslateRight = r.ReadSingle();
			d.TranslateUp = r.ReadSingle();
			d.Stage = r.ReadInt32();

			int count = r.ReadInt32();
			d.ActivationGroupStates = new List<bool>();
			for (int i = 0; i < count; i++) d.ActivationGroupStates.Add(r.ReadBoolean());

			int bodyCount = r.ReadInt32();
			for (int i = 0; i < bodyCount; i++)
			{
				d.BodyRotations.Add(new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle()));
			}

			int bpCount = r.ReadInt32();
			for (int i = 0; i < bpCount; i++)
			{
				d.BodyPositions.Add(new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle()));
			}

			int etCount = r.ReadInt32();
			for (int i = 0; i < etCount; i++)
			{
				d.EngineThrottles.Add(r.ReadSingle());
			}

			int paCount = r.ReadInt32();
			for (int i = 0; i < paCount; i++)
			{
				d.PartActivated.Add(r.ReadBoolean());
			}

			return d;
		}
	}
}
