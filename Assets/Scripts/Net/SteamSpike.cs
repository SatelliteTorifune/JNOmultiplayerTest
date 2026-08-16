using System;
using System.Reflection;
using Steamworks;
using UnityEngine;

namespace Assets.Scripts.Net
{
	/// <summary>
	/// Steam API 可行性 spike（临时，验证后删除）：
	/// 验证 JNO mod 运行时能否访问 Steam 身份 + Steamworks.NET 直接调用。
	///
	/// 两步：
	/// 1. 反射访问 `Assets.Packages.SocialPlatforms.SocialExt`（游戏已加载的 Packages.dll）——已验证 IsSteam=True；
	/// 2. Steamworks.NET 直接调用（com.rlabrecque.steamworks.net.dll 已放入 ModTools/Assemblies）：
	///    SteamUser.GetSteamID() / SteamFriends.GetPersonaName() / SteamNetworkingSockets 可用性。
	///
	/// 通过条件：拿到非 0 SteamId 且 SteamNetworkingSockets 可初始化。
	/// </summary>
	public class SteamSpike : MonoBehaviour
	{
		private void Start()
		{
			TryReflectSocialExt();
			TryDirectSteamworks();
		}

		/// <summary>直接调用 Steamworks.NET：拿 SteamId / 名字 / NetworkingSockets。</summary>
		private static void TryDirectSteamworks()
		{
			try
			{
				ulong steamId = SteamUser.GetSteamID().m_SteamID;
				string name = SteamFriends.GetPersonaName();
				Mod.LogLobby("SteamSpike [Steamworks.NET]: SteamId=" + steamId + ", name='" + name + "'");
				Mod.LogLobby("SteamSpike [Steamworks.NET]: IsSteamRunning=" + SteamAPI.IsSteamRunning());

				// 验证 SteamNetworkingSockets 可用（初始化 Identity 服务）
				var identity = new SteamNetworkingIdentity();
				identity.SetSteamID(SteamUser.GetSteamID());
				Mod.LogLobby("SteamSpike [Steamworks.NET]: SteamNetworkingSockets type=" +
					typeof(SteamNetworkingSockets).FullName + ", identitySteamId=" + identity.GetSteamID().m_SteamID);
			}
			catch (Exception e)
			{
				Mod.LogError("SteamSpike [Steamworks.NET] exception: " + e);
			}
		}

		private static void TryReflectSocialExt()
		{
			try
			{
				// 遍历已加载程序集找 SocialExt（避免硬编码程序集名 "Packages" 不匹配）
				Type socialExtType = null;
				foreach (Assembly asm in AppDomain.CurrentDomain.GetAssemblies())
				{
					Type t = asm.GetType("Assets.Packages.SocialPlatforms.SocialExt", false);
					if (t != null)
					{
						socialExtType = t;
						Mod.LogLobby("SteamSpike: found SocialExt in assembly '" + asm.GetName().Name + "'");
						break;
					}
				}

				if (socialExtType == null)
				{
					Mod.LogLobby("SteamSpike: SocialExt NOT FOUND in any loaded assembly");
					return;
				}

				// 静态属性 IsSteam
				PropertyInfo isSteamProp = socialExtType.GetProperty("IsSteam", BindingFlags.Public | BindingFlags.Static);
				bool isSteam = isSteamProp != null && (bool)isSteamProp.GetValue(null, null);
				Mod.LogLobby("SteamSpike: IsSteam=" + isSteam);

				if (!isSteam)
				{
					Mod.LogLobby("SteamSpike: not running under Steam (game launched outside Steam?)");
					return;
				}

				// SocialExt.Active -> SteamPlatform（Assets.Packages.SocialPlatforms.Steam.SteamPlatform）
				PropertyInfo activeProp = socialExtType.GetProperty("Active", BindingFlags.Public | BindingFlags.Static);
				object active = activeProp != null ? activeProp.GetValue(null, null) : null;
				Mod.LogLobby("SteamSpike: Active=" + (active != null ? active.GetType().FullName : "null"));

				// SteamPlatform.SteamManager -> ISteamManager（可能含 SteamId / 名称）
				if (active != null)
				{
					PropertyInfo steamMgrProp = active.GetType().GetProperty("SteamManager");
					object steamMgr = steamMgrProp != null ? steamMgrProp.GetValue(active, null) : null;
					Mod.LogLobby("SteamSpike: SteamManager=" + (steamMgr != null ? steamMgr.GetType().FullName : "null"));

					// 尝试从 SteamManager 反射读 SteamId / 玩家名
					if (steamMgr != null)
					{
						TryDumpMembers(steamMgr.GetType(), "SteamManager");
					}
					// 也 dump SteamPlatform 的公开成员，找 SteamId / PersonaName
					TryDumpMembers(active.GetType(), "SteamPlatform");
				}

				// SocialExt.Steam（ISteamManager）—— WebUtility 里用 SocialExt.Steam.IsOverlayEnabled()
				PropertyInfo steamProp = socialExtType.GetProperty("Steam", BindingFlags.Public | BindingFlags.Static);
				object steam = steamProp != null ? steamProp.GetValue(null, null) : null;
				Mod.LogLobby("SteamSpike: SocialExt.Steam=" + (steam != null ? steam.GetType().FullName : "null"));
				if (steam != null)
				{
					TryDumpMembers(steam.GetType(), "SocialExt.Steam");
				}
			}
			catch (Exception e)
			{
				Mod.LogError("SteamSpike exception: " + e);
			}
		}

		/// <summary>打印类型的公开属性/方法名，定位 SteamId/PersonaName 相关成员。</summary>
		private static void TryDumpMembers(Type t, string label)
		{
			try
			{
				foreach (PropertyInfo p in t.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static))
				{
					Mod.LogLobby("SteamSpike [" + label + "] prop: " + p.PropertyType.Name + " " + p.Name);
				}
				foreach (MethodInfo m in t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
				{
					Mod.LogLobby("SteamSpike [" + label + "] method: " + m.Name);
				}
			}
			catch (Exception e)
			{
				Mod.LogLobby("SteamSpike dump '" + label + "' failed: " + e.Message);
			}
		}
	}
}
