using System;
using System.Collections;
using System.Collections.Generic;
using Assets.Scripts.Flight;
using Jundroo.ModTools;
using ModApi;
using ModApi.Common;
using ModApi.Craft;
using ModApi.Craft.Parts;
using ModApi.Flight.GameView;
using UnityEngine;

namespace Assets.Scripts.Net
{
	/// <summary>
	/// 异步 prefab 预加载器（SP2 式"异步预加载 + 真实百分比加载框"）：
	/// 远程玩家飞船生成前，先把飞船所有部件的 prefab 用 Resources.LoadAsync 逐帧读进缓存，
	/// 再让游戏自身的原子 SpawnCraft 照常跑 → 主 prefab 命中缓存，只剩纯 Instantiate，
	/// 消除"同步加载全部部件 → 秒级白屏"。
	/// 关键（路径一致性命门）：与 CraftBuilder.CreatePartGameObject（jnoCode CraftBuilder.cs:314-333）的路径处理完全一致——
	///   主游戏部件去掉 ".prefab" 后缀走 Game.Instance.ResourceLoader（Resources.LoadAsync 与 Resources.Load 共享缓存）；
	///   mod 部件（PartType.Mod != null 且 PrefabPath 以 "Assets/" 开头）走 mod.ResourceLoader.LoadAsset（同步，数量少，分帧散开）。
	/// 不拦截/不改写/不跳过任何装配逻辑。
	/// </summary>
	public static class MpCraftPreloader
	{
		/// <summary>单个 prefab 加载任务。</summary>
		private struct PrefabJob
		{
			/// <summary>实际加载路径（主部件已去 ".prefab"；mod 部件保留原始路径）。</summary>
			public string Path;
			/// <summary>null = 主游戏部件；非 null = mod 部件（用其 ResourceLoader.LoadAsset）。</summary>
			public ILoadedMod Mod;
			public bool IsMod { get { return Mod != null; } }
		}

		/// <summary>
		/// 收集飞船所有部件 prefab 加载任务（按实际加载路径去重）。
		/// 分类与 CraftBuilder.CreatePartGameObject（jnoCode CraftBuilder.cs:314-333）一致。
		/// </summary>
		private static void CollectJobs(CraftData craftData, List<PrefabJob> jobs)
		{
			jobs.Clear();
			if (craftData == null || craftData.Assembly == null) return;
			HashSet<string> seen = new HashSet<string>();
			IReadOnlyList<PartData> parts = craftData.Assembly.Parts;
			for (int i = 0; i < parts.Count; i++)
			{
				PartData part = parts[i];
				if (part == null || part.PartType == null) continue;
				string raw = part.PartType.PrefabPath;
				if (string.IsNullOrEmpty(raw)) continue;
				ILoadedMod mod = part.PartType.Mod;
				bool isMod = mod != null && raw.StartsWith("Assets/", StringComparison.Ordinal);
				// 与 CraftBuilder.cs:332 一致：主游戏部件去掉 ".prefab" 后缀
				string loadPath = isMod ? raw : raw.Replace(".prefab", string.Empty);
				string key = (isMod ? "M:" : "G:") + loadPath;
				if (!seen.Add(key)) continue;
				jobs.Add(new PrefabJob { Path = loadPath, Mod = mod });
			}
		}

		/// <summary>
		/// 协程预加载飞船所有部件 prefab：
		/// - 主游戏部件：Resources.LoadAsync 逐帧等 Request.isDone（与 Resources.Load 共享缓存），完成一个报一次真实进度；
		/// - mod 部件：mod.ResourceLoader.LoadAsset 同步加载（数量少，分帧散开）；
		/// - 失败容错：某路径加载失败记日志、继续，不阻塞整体；
		/// - 可取消：isCancelled() 返回 true 时提前停止（玩家离开/场景切换时调用方置真）。
		/// 进度 = 已完成唯一 prefab 数 / 总数（0..1），完成时最后报一次 1f。
		/// </summary>
		public static IEnumerator PreloadCraftPrefabs(CraftData craftData, Action<float> onProgress, Func<bool> isCancelled)
		{
			List<PrefabJob> jobs = new List<PrefabJob>();
			CollectJobs(craftData, jobs);
			int total = jobs.Count;
			if (total == 0)
			{
				if (onProgress != null) onProgress(1f);
				yield break;
			}

			int done = 0;
			for (int i = 0; i < total; i++)
			{
				if (isCancelled != null && isCancelled()) yield break;
				PrefabJob job = jobs[i];

				if (job.IsMod)
				{
					// mod 部件：同步加载（数量少），分帧散开避免集中卡顿
					try
					{
						GameObject asset = job.Mod.ResourceLoader.LoadAsset<GameObject>(job.Path);
						if (asset == null) Mod.LogError("MP preloader: mod prefab not found: '" + job.Path + "'");
					}
					catch (Exception e)
					{
						Mod.LogError("MP preloader: mod prefab load error '" + job.Path + "': " + e.Message);
					}
					done++;
					if (onProgress != null) onProgress((float)done / total);
					yield return null;
				}
				else
				{
					// 主游戏部件：异步逐帧等 isDone（读进缓存，之后 SpawnCraft 的 Resources.Load 命中缓存）
					ResourceRequestWrapper<GameObject> wrapper = null;
					try
					{
						wrapper = Game.Instance.ResourceLoader.LoadAsync<GameObject>(job.Path, false);
					}
					catch (Exception e)
					{
						Mod.LogError("MP preloader: async load start error '" + job.Path + "': " + e.Message);
					}
					if (wrapper == null || wrapper.Request == null)
					{
						// 启动失败：记日志、继续（不阻塞整体）
						Mod.LogError("MP preloader: async load failed to start '" + job.Path + "'");
						done++;
						if (onProgress != null) onProgress((float)done / total);
						continue;
					}
					while (!wrapper.Request.isDone)
					{
						if (isCancelled != null && isCancelled()) yield break;
						yield return null;
					}
					if (wrapper.Request.asset == null)
					{
						Mod.LogError("MP preloader: main prefab not found: '" + job.Path + "'");
					}
					done++;
					if (onProgress != null) onProgress((float)done / total);
				}
			}
			if (onProgress != null) onProgress(1f);
		}
	}

	/// <summary>
	/// SP2 风格加载进度框：远程玩家位置上方一个"旋转的白色薄板方框" + TextMesh 百分比（真实进度）。
	/// 始终面向相机（billboard：拷贝相机旋转）+ 薄板绕视线轴（自身 Z）旋转；加载完成/取消时调用 DestroyIndicator 销毁。
	/// </summary>
	public class MpCraftLoadingIndicator : MonoBehaviour
	{
		private const float SpinSpeedDeg = 150f;

		private TextMesh _text;
		private Transform _spinner;
		private Camera _camera;
		private string _label;

		/// <summary>在指定世界坐标创建加载进度框。</summary>
		public static MpCraftLoadingIndicator Create(Vector3 worldPosition)
		{
			GameObject go = new GameObject("MpCraftLoadingIndicator");
			go.transform.position = worldPosition;
			MpCraftLoadingIndicator ind = go.AddComponent<MpCraftLoadingIndicator>();
			ind.Build();
			return ind;
		}

		private void Build()
		{
			// 旋转的白色薄板方框：宽 X/Y、薄 Z（法线朝 Z）→ 朝向相机后正面是 2.2x2.2 的大面，
			// 绕自身 Z（视线轴）旋转呈"旋转方框"（SP2 LoadingAircraftStatusScript 同款）。
			GameObject cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
			cube.name = "MpLoadingSpinner";
			cube.transform.SetParent(transform, false);
			cube.transform.localPosition = Vector3.zero;
			cube.transform.localScale = new Vector3(2.2f, 2.2f, 0.06f);
			Collider col = cube.GetComponent<Collider>();
			if (col != null) Destroy(col);
			Renderer cubeRenderer = cube.GetComponent<Renderer>();
			if (cubeRenderer != null && cubeRenderer.sharedMaterial != null)
			{
				Material mat = new Material(cubeRenderer.sharedMaterial);
				mat.color = Color.white;
				cubeRenderer.sharedMaterial = mat;
			}
			_spinner = cube.transform;

			// 子物体 TextMesh 显示 "正在加载飞船\nN%"（真实进度）
			GameObject textGo = new GameObject("MpLoadingText");
			textGo.transform.SetParent(transform, false);
			textGo.transform.localPosition = new Vector3(0f, 1.9f, 0f);
			_text = textGo.AddComponent<TextMesh>();
			_text.characterSize = 0.1f;
			_text.fontSize = 80;
			_text.anchor = TextAnchor.MiddleCenter;
			_text.alignment = TextAlignment.Center;
			_text.color = Color.white;
			try { _label = Locale.GetString("MultiPlayer.MultiPlayerUI.LoadingCraft"); }
			catch { _label = "Loading craft"; }
			if (string.IsNullOrEmpty(_label)) _label = "Loading craft";
			_text.text = _label + "\n0%";
		}

		/// <summary>更新百分比（0..1）显示。</summary>
		public void SetProgress(float progress)
		{
			if (_text == null) return;
			int pct = Mathf.RoundToInt(Mathf.Clamp01(progress) * 100f);
			_text.text = _label + "\n" + pct + "%";
		}

		private void Update()
		{
			// 绕自身 Z（视线轴）旋转，呈"旋转方框"（SP2 LoadingAircraftStatusScript 风格）
			if (_spinner != null) _spinner.Rotate(0f, 0f, SpinSpeedDeg * Time.deltaTime, Space.Self);
			// billboard：始终面向相机（拷贝相机旋转，薄板法线朝视线）
			if (_camera == null) _camera = ResolveCamera();
			if (_camera != null)
			{
				transform.rotation = _camera.transform.rotation;
			}
		}

		/// <summary>销毁进度框。</summary>
		public void DestroyIndicator()
		{
			if (this != null && gameObject != null) Destroy(gameObject);
		}

		private static Camera ResolveCamera()
		{
			try
			{
				IGameView gv = null;
				if (FlightSceneScript.Instance != null && FlightSceneScript.Instance.ViewManager != null)
				{
					gv = FlightSceneScript.Instance.ViewManager.GameView;
				}
				if (gv != null && gv.GameCamera != null)
				{
					Camera c = gv.GameCamera.NearCamera;
					if (c != null) return c;
					c = gv.GameCamera.FarCamera;
					if (c != null) return c;
				}
			}
			catch { }
			return Camera.main;
		}
	}
}
