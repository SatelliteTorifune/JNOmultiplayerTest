using System;
using System.IO;
using System.Collections.Generic;
using UnityEngine;

namespace Assets.Scripts.Net
{
	/// <summary>
	/// 联机消息类型定义。
	/// </summary>
	public enum MpMessageType : byte
	{
		Hello = 1,       // 加入者 -> 房主：请求加入（含玩家名）
		Welcome = 2,     // 房主 -> 加入者：欢迎（分配 PlayerId / 初始 NodeId）
		PlayerJoin = 3,  // 房主 -> 所有：通知新玩家加入（含该玩家 craft XML）
		PlayerLeave = 4, // 房主 -> 所有：通知玩家离开
		State = 5,       // 状态包：NodeId + 时间戳 + recdata
		Pause = 6,       // 暂停 / 恢复广播
		CraftData = 7,   // craft XML 交换（加入者把自己的飞船发给房主）
		Ping = 8,        // RTT 探测
		Pong = 9,
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

		public static byte[] EncodePlayerJoin(int playerId, int nodeId, string craftXml)
		{
			return Pack(MpMessageType.PlayerJoin, w =>
			{
				w.Write(playerId);
				w.Write(nodeId);
				w.Write(craftXml ?? string.Empty);
			});
		}

		public static bool TryDecodePlayerJoin(byte[] buffer, out int playerId, out int nodeId, out string craftXml)
		{
			playerId = -1; nodeId = -1; craftXml = null;
			try
			{
				using (MemoryStream ms = new MemoryStream(buffer))
				using (BinaryReader r = new BinaryReader(ms))
				{
					if (r.ReadByte() != (byte)MpMessageType.PlayerJoin) return false;
					playerId = r.ReadInt32();
					nodeId = r.ReadInt32();
					craftXml = r.ReadString();
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
		public static byte[] EncodeState(int playerId, int nodeId, double time, Mod.recdata data)
		{
			return Pack(MpMessageType.State, w =>
			{
				w.Write(playerId);
				w.Write(nodeId);
				w.Write(time);
				WriteRecdata(w, data);
			});
		}

		public static bool TryDecodeState(byte[] buffer, out int playerId, out int nodeId, out double time, out Mod.recdata data)
		{
			playerId = -1; nodeId = -1; time = 0; data = new Mod.recdata();
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
				w.Write(craftXml ?? string.Empty);
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
					craftXml = r.ReadString();
					return true;
				}
			}
			catch { return false; }
		}

		// ---------------- Pause / Ping ----------------

		public static byte[] EncodePause(bool paused)
		{
			return Pack(MpMessageType.Pause, w => w.Write(paused));
		}

		public static bool TryDecodePause(byte[] buffer, out bool paused)
		{
			paused = false;
			try
			{
				using (MemoryStream ms = new MemoryStream(buffer))
				using (BinaryReader r = new BinaryReader(ms))
				{
					if (r.ReadByte() != (byte)MpMessageType.Pause) return false;
					paused = r.ReadBoolean();
					return true;
				}
			}
			catch { return false; }
		}

		public static byte[] EncodePing(long tick)
		{
			return Pack(MpMessageType.Ping, w => w.Write(tick));
		}

		public static byte[] EncodePong(long tick)
		{
			return Pack(MpMessageType.Pong, w => w.Write(tick));
		}

		public static bool TryDecodePingPong(byte[] buffer, out long tick)
		{
			tick = 0;
			try
			{
				using (MemoryStream ms = new MemoryStream(buffer))
				using (BinaryReader r = new BinaryReader(ms))
				{
					byte type = r.ReadByte();
					if (type != (byte)MpMessageType.Ping && type != (byte)MpMessageType.Pong) return false;
					tick = r.ReadInt64();
					return true;
				}
			}
			catch { return false; }
		}

		// ---------------- recdata 序列化 ----------------

		public static void WriteRecdata(BinaryWriter w, Mod.recdata d)
		{
			w.Write(d.Position.x); w.Write(d.Position.y); w.Write(d.Position.z);
			w.Write(d.Velocity.x); w.Write(d.Velocity.y); w.Write(d.Velocity.z);
			w.Write(d.Heading.x); w.Write(d.Heading.y); w.Write(d.Heading.z); w.Write(d.Heading.w);

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
		}

		public static Mod.recdata ReadRecdata(BinaryReader r)
		{
			Mod.recdata d = new Mod.recdata(
				new Vector3d(r.ReadDouble(), r.ReadDouble(), r.ReadDouble()),
				new Vector3d(r.ReadDouble(), r.ReadDouble(), r.ReadDouble()),
				new Quaterniond(r.ReadDouble(), r.ReadDouble(), r.ReadDouble(), r.ReadDouble())
			);
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

			return d;
		}
	}
}
