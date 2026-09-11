using ModApi.Scenes.Events;

using Assets.Scripts.Net;

using Jundroo.ModTools;
using UnityEngine;

using HarmonyLib;
using ModApi.Mods;

namespace Assets.Scripts
{
    public partial class Mod : GameMod
    {
        public static void Log(object message)
        {
            if (!ModSettings.Instance.DebugMode)
            {
                return;	
            }
            UnityEngine.Debug.Log("[Mptest] " + message);
        }

        public static void LogError(object message)
        {
            if (!ModSettings.Instance.DebugMode)
            {
                return;
            }
            UnityEngine.Debug.LogError("[Mptest] " + message);
        }

        /// <summary>
        /// 联机生命周期日志：不受 DebugMode 限制，始终输出到控制台。
        /// 用于确认 Host/Join/Stop 等关键节点确实执行成功。
        /// </summary>
        public static void LogLobby(object message)
        {
            UnityEngine.Debug.Log("[Mptest][Lobby] " + message);
        }

        /// <summary>
        /// 更新检查日志：不受 DebugMode 限制，始终输出到控制台。
        /// 更新检查含网络"总看门狗"超时 / 主动中断等诊断（防卡死机制），需要始终可见，
        /// 便于确认"最多等 15s 必放弃"确实生效。
        /// </summary>
        public static void LogUpdate(object message)
        {
            UnityEngine.Debug.Log("[Mptest][Update] " + message);
        }
    }
}