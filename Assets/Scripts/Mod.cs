using System;
using System.Collections.Generic;
using System.Linq;

using ModApi;
using ModApi.Craft;
using ModApi.Craft.Parts;
using ModApi.Flight;
using ModApi.Mods;
using ModApi.Scenes.Parameters;
using ModApi.Ui;

using Assets.Packages.DevConsole;
using Assets.Scripts.Flight;
using Assets.Scripts.Flight.Sim;
using Assets.Scripts.State;


using Jundroo.ModTools;
using UnityEngine;

using HarmonyLib;

namespace Assets.Scripts
{
	public partial class Mod : GameMod
	{
		private Mod()
		{
			
		}
		public static Mod Instance { get; } = GameModBase.GetModInstance<Mod>();
		public GameObject ReplayController = null;
		float MaxUpdateIntervalMs = 10f;
		bool replayOnSceneLoaded = false;

		public bool goToStartPosOnStart = true;

		public int CraftNodeID = -2147483647;
		List<recdata> RecordData;
		List<List<bodyrecdata>> BodyRecordData;
		protected override void OnModInitialized()
		{
			try
			{
				base.OnModInitialized();
				new Harmony("replaytools").PatchAll();

				DevConsoleApi.RegisterCommand("ReplayTools.SetRecordIntervalMs", delegate (float i)
				{
					UnityEngine.Debug.Log("Record Update Interval changed from " + (MaxUpdateIntervalMs).ToString() + " ms to " + i.ToString() + " ms");
					MaxUpdateIntervalMs = i;
				}, "Interval");

				DevConsoleApi.RegisterCommand("ReplayTools.ReplicateCraftControls", delegate (bool i)
				{
					UnityEngine.Debug.Log("Replicate Craft Controls changed from " + ModSettings.Instance.ReplayCraftControls.Value.ToString() + " to " + i.ToString());
					ModSettings.Instance.ReplayCraftControls.Value = i;
				}, "Replicate");

				DevConsoleApi.RegisterCommand("ReplayTools.GoToStartPosOnReplayStart", delegate (bool i)
				{
					UnityEngine.Debug.Log($"Craft {(i ? "will" : "no longer")} return to start position on replay start");
					goToStartPosOnStart = i;
				}, "ReturnOnStart");

				DevConsoleApi.RegisterCommand("ReplayTools.UseRigidBodyTransform", delegate (bool i)
				{
					UnityEngine.Debug.Log($"Craft {(i ? "will" : "no longer")} record rigid body transform {(i ? "and" : "or")} use them in replay");
					ModSettings.Instance.RecordBodiesTransform.Value = i;
				}, "DeactivateOnStart");

				DevConsoleApi.RegisterCommand("ReplayTools.dpc", delegate (bool i)
				{
					UnityEngine.Debug.Log($"Craft {(i ? "will" : "no longer")} deactivate physic simulation on replay start");
					ModSettings.Instance.ReplayDeactivatePhysics.Value = i;
				}, "DeactivateOnStart");

				DevConsoleApi.RegisterCommand("Record", delegate ()
				{
					StartRecord();
				});

				ReplayUI.Initialize();
				//开始replay
				//replayOnSceneLoaded bool值,决定是否开始replay
				Game.Instance.SceneManager.SceneLoaded += (sender, e) => {
					if (replayOnSceneLoaded && Game.Instance.SceneManager.InFlightScene)
					{
						StartReplay();
					}
				};

			}
			catch (Exception e)
			{
				UnityEngine.Debug.Log("Init failed: " + e.ToString());
			}
		}

		public void StartRecord()
		{

			if (!string.IsNullOrWhiteSpace(Game.Instance.GameState.GetTagQuicksave()))
			{

				if (FlightSceneScript.Instance != null)
				{
					if (Instance.ReplayController == null)
					{
						//FlightSceneScript.Instance.QuickSave();
						Instance.ReplayController = new GameObject();
						Instance.ReplayController.AddComponent<RecordSystem>();
						UnityEngine.Debug.Log("Recording...");
						FlightSceneScript.Instance.FlightSceneUI.ShowMessage("Quick Save Complete\nRecording...", false,
							5f);
						Instance.ReplayController.SetActive(true);
						DevConsoleApi.UnregisterCommand("Replay");

					}
					else if (Instance.ReplayController.GetComponent<RecordSystem>())
					{
						StopRecord();
					}
					else
					{
						StopReplay();
					}

				}
			}
			else
			{
				FlightSceneScript.Instance.FlightSceneUI.ShowMessage(
					"Unable to start record because Quick Save is disabled", false, 5f);
			}
				
		}

		public void StopRecord()
		{
			if (Instance.ReplayController != null)
			{
				FlightSceneScript.Instance.FlightSceneUI.ShowMessage($"Record Complete, Frames: {Instance.RecordData.Count()}, Time: {Instance.RecordData.Count() * MaxUpdateIntervalMs / 1000} second(s)", false, 5f);
				UnityEngine.Debug.Log($"Record Complete, Frames: {Instance.RecordData.Count()}, Time: {Instance.RecordData.Count() * MaxUpdateIntervalMs / 1000} second(s)");
				Instance.ReplayController.SetActive(false);
				GameObject.Destroy(Instance.ReplayController);
				Instance.ReplayController = null;

				DevConsoleApi.RegisterCommand("Replay", delegate ()
				{
					StartReplay();
				});
			}
		}

		public void StartReplay()
		{
			replayOnSceneLoaded = false;
			if (!string.IsNullOrWhiteSpace(Game.Instance.GameState.GetTagQuicksave()))
			{
				if (FlightSceneScript.Instance != null)
				{
					if (Instance.ReplayController == null)
					{
						UnityEngine.Debug.Log("Replaying...");
						//搜索玩家craft的craftID的craftNodeId
						//你妈的我在写什么东西
						if (Instance.CraftNodeID != -2147483647 && !new List<CraftNode>(FlightSceneScript.Instance.FlightState.CraftNodes).Exists(x => x.NodeId == Instance.CraftNodeID))
						{
							UnityEngine.Debug.Log("Unable to find craft with NodeID: " + Instance.CraftNodeID.ToString() + ", using Player Craft instead");
							Instance.CraftNodeID = -2147483647;
						}
						Instance.ReplayController = new GameObject();
						Instance.ReplayController.AddComponent<ReplaySystem>();
						Instance.ReplayController.SetActive(true);
					}
					else if (Instance.ReplayController.GetComponent<ReplaySystem>())
					{
						StopReplay();
					}
					else
					{
						StopRecord();
					}
				}
			}
			else FlightSceneScript.Instance.FlightSceneUI.ShowMessage("Unable to start replay because Quick Load is disabled", false, 5f);
		}
		public void StopReplay()
		{
			if (Instance.ReplayController != null)
			{
				FlightSceneScript.Instance.FlightSceneUI.ShowMessage("Replay Stopped", false, 5f);
				UnityEngine.Debug.Log("Replay Stopped");
				Instance.ReplayController.SetActive(false);
				GameObject.Destroy(Instance.ReplayController);
				Instance.ReplayController = null;
			}
		}
		public void ReplayQuickLoad()
		{
			if (Instance.ReplayController != null)
			{
				if (Instance.ReplayController.GetComponent<ReplaySystem>())
				{
					StopReplay();
					return;
				}
				else
				{
					StopRecord();
					return;
				}
			}
			
			if (Instance.RecordData == null || Instance.RecordData.Count < 3)
			{
				UnityEngine.Debug.Log("No record data");
				FlightSceneScript.Instance.FlightSceneUI.ShowMessage("No record data", false, 5f);
				return;
			}
			GameState gameState = Game.Instance.GameState;
			string quicksaveTag = gameState.GetTagQuicksave();
			if (string.IsNullOrWhiteSpace(quicksaveTag))
			{
				FlightSceneScript.Instance.FlightSceneUI.ShowMessage("Quick Load is currently disabled", false, 5f);
				return;
			}
			//这里是根据quick save读取的你看看
			if (Game.Instance.GameStateManager.CheckGameStateTagExists(gameState.Id, quicksaveTag))
			{
				FlightSceneScript.Instance.TimeManager.RequestPauseChange(true, false);
				MessageDialogScript messageDialogScript = Game.Instance.UserInterface.CreateMessageDialog(MessageDialogType.OkayCancel, null, true);
				messageDialogScript.MessageText = "Confirm that you wish to start replay from your last quick save.";
				messageDialogScript.OkayButtonText = "REPLAY";
				messageDialogScript.UseDangerButtonStyle = true;
				messageDialogScript.OkayClicked += delegate (MessageDialogScript d)
				{
					Game.Instance.GameStateManager.RestoreGameStateTag(gameState.Id, quicksaveTag, gameState.GetTagActive());
					replayOnSceneLoaded = true;
					FlightSceneScript.Instance.ReloadFlightScene(false, FlightSceneLoadParameters.ResumeCraft(null, null), FlightSceneExitReason.QuickLoad);
				};
				messageDialogScript.CancelClicked += delegate (MessageDialogScript d)
				{
					FlightSceneScript.Instance.TimeManager.RequestPauseChange(false, false);
					d.Close();
				};
				return;
			}
			FlightSceneScript.Instance.FlightSceneUI.ShowMessage("No quick save available", false, 5f);
		}

		public struct recdata 
		{
			public Vector3d Position;
			public Vector3d Velocity;
			public Quaterniond Heading;

			public float Pitch;
			public float Yaw;
			public float Roll;

			/*public float EvaMoveFwdAft;
			public float EvaMoveFwdBft;

			public float EvaMoveUpDown;
			
			public float EvaJump*/
			
			public float Throttle;
			public float Brake;

			public float Slider1;
			public float Slider2;
			public float Slider3;
			public float Slider4;

			public float TranslateForward;
			public float TranslateRight;
			public float TranslateUp;

			public List<bool> ActivationGroupStates;

			public int Stage;

			public recdata(Vector3d position, Vector3d velocity, Quaterniond heading)
			{
				Position = position;
				Velocity = velocity;
				Heading = heading;

				Pitch = 0;
				Yaw = 0;
				Roll = 0;

				Throttle = 0;
				Brake = 0;

				Slider1 = 0;
				Slider2 = 0;
				Slider3 = 0;
				Slider4 = 0;

				TranslateForward = 0;
				TranslateRight = 0;
				TranslateUp = 0;

				ActivationGroupStates = new List<bool>();
				Stage = 0;
			}

			public recdata(
				Vector3d position,
				Vector3d velocity,
				Quaterniond heading,

				float pitch,
				float yaw,
				float roll,

				float throttle,
				float brake,

				float slider1,
				float slider2,
				float slider3,
				float slider4,

				float translateForward,
				float translateRight,
				float translateUp,

				List<bool> activationGroupStates,
				int stage
				)
			{
				Position = position;
				Velocity = velocity;
				Heading = heading;

				Pitch = pitch;
				Yaw = yaw;
				Roll = roll;

				Throttle = throttle;
				Brake = brake;

				Slider1 = slider1;
				Slider2 = slider2;
				Slider3 = slider3;
				Slider4 = slider4;

				TranslateForward = translateForward;
				TranslateRight = translateRight;
				TranslateUp = translateUp;

				ActivationGroupStates = activationGroupStates;
				Stage = stage;
			}

		}

		public struct bodyrecdata
		{
			public int Id;
			public Vector3 Position;
			public Quaternion Heading;

			public bodyrecdata(int id, Vector3 position, Quaternion heading)
			{
				Id = id;
				Position = position;
				Heading = heading;
			}

		}

		//[DefaultExecutionOrder(1000)]
		public class RecordSystem : MonoBehaviour
		{
			CraftNode craft;
			List<BodyData> bodies;
			void Awake()
			{
				try
				{
					craft = (CraftNode)FlightSceneScript.Instance.CraftNode;
					bodies = new List<BodyData>(craft.CraftScript.Data.Assembly.Bodies);
					Instance.CraftNodeID = craft.NodeId;
					UnityEngine.Debug.Log("Record Started");
					Instance.RecordData = new List<recdata>();
					Instance.BodyRecordData = new List<List<bodyrecdata>>();
				}
				catch (Exception e)
				{
					UnityEngine.Debug.Log("Record Activation Failed: " + e.ToString());
				}
			}
			
			float updateTimer = Instance.MaxUpdateIntervalMs;
			public void Record()
			{
				try
				{
					DataProcess DP = new DataProcess();
					Debug.LogError("DP已实例化");
					if (!FlightSceneScript.Instance.TimeManager.Paused)
					{
						if (FlightSceneScript.Instance.TimeManager.CurrentMode.TimeMultiplier > 1)
						{
							FlightSceneScript.Instance.TimeManager.SetNormalSpeedMode();
						}
						updateTimer += Time.deltaTime * 1000;
						if (updateTimer >= Instance.MaxUpdateIntervalMs)
						{
							foreach (var node in FlightSceneScript.Instance.FlightState.CraftNodes) node.AllowPlayerControl = false;

							ICommandPod cp = craft.CraftScript.ActiveCommandPod;
							List<bool> activationGroupStates = new List<bool>();
							for (int i = 1; i < 11; i++)
							{
								activationGroupStates.Add(cp.GetActivationGroupState(i));
							}
							
							Instance.RecordData.Add(
							new recdata(

								craft.Parent.PlanetVectorToSurfaceVector(craft.Position),
								craft.Parent.PlanetVectorToSurfaceVector(craft.Velocity),
								craft.Heading,


								cp.Controls.Pitch,
								cp.Controls.Yaw,
								cp.Controls.Roll,
								cp.Controls.Throttle,
								cp.Controls.Brake,
								cp.Controls.Slider1,
								cp.Controls.Slider2,
								cp.Controls.Slider3,
								cp.Controls.Slider4,
								cp.Controls.TranslateForward,
								cp.Controls.TranslateRight,
								cp.Controls.TranslateUp,
								activationGroupStates,
								cp.CurrentStage
							));


							var newData = new recdata(
   							craft.Parent.PlanetVectorToSurfaceVector(craft.Position),
   							craft.Parent.PlanetVectorToSurfaceVector(craft.Velocity),
   							craft.Heading,

   							cp.Controls.Pitch,
   							cp.Controls.Yaw,
   							cp.Controls.Roll,
   							cp.Controls.Throttle,
   							cp.Controls.Brake,
   							cp.Controls.Slider1,
   							cp.Controls.Slider2,
   							cp.Controls.Slider3,
   							cp.Controls.Slider4,
   							cp.Controls.TranslateForward,
   							cp.Controls.TranslateRight,
   							cp.Controls.TranslateUp,
   							activationGroupStates,
   							cp.CurrentStage
					);

							Instance.RecordData.Add(newData);
							DP.DPupdate(newData);
							
							DataProcess.LogRecordData(newData);
							
							
							if(ModSettings.Instance.RecordBodiesTransform)
							{
								List<bodyrecdata> d = new List<bodyrecdata>();
								foreach (var body in bodies)
								{
									d.Add(
									new bodyrecdata(
										body.Id,
										body.BodyScript.Transform.position,
										body.BodyScript.Transform.rotation
									));
								}
								Instance.BodyRecordData.Add(d);
							}

							updateTimer = 0;
						}
					}
				}

				catch (Exception e)
				{
					UnityEngine.Debug.Log("Update Failed: " + e.ToString());
				}
			}
		}

		//[DefaultExecutionOrder(1000)]
		/// <summary>
		/// 我希望莲子说的是对的，应该是这里吧，梅莉啊，为什么呢我亲爱的，
		/// </summary>
		public class ReplaySystem : MonoBehaviour
		{

			CraftNode craft;
			void Awake()
			{
				try
				{
					if (Instance.RecordData == null || Instance.RecordData.Count < 3)
					{
						UnityEngine.Debug.Log("No record data");
						Destroy(Instance.ReplayController);
						Instance.ReplayController = null;
						FlightSceneScript.Instance.FlightSceneUI.ShowMessage("No record data", false, 5f);
						return;
					}
					else
					{
						if (Instance.CraftNodeID == -2147483647)
							craft = (CraftNode)FlightSceneScript.Instance.CraftNode;
						else
							craft = FlightSceneScript.Instance.FlightState.GetCraftNode(Instance.CraftNodeID);

						if (Instance.goToStartPosOnStart && ModSettings.Instance.ReplayMode != ModSettings.replayModes.Precise)
						{
							CraftUtils.SetCraftTransform(craft, Instance.RecordData[0].Position, Instance.RecordData[0].Velocity, Instance.RecordData[0].Heading, craft.Parent);
							CraftUtils.RecalculateFrameState(FlightSceneScript.Instance.ViewManager.GameView.ReferenceFrame, craft);
						}
						
						if (ModSettings.Instance.ReplayDeactivatePhysics)
						{
							CraftUtils.DisableCraftPhysicCalculation(ref craft);
						}

						FlightSceneScript.Instance.FlightSceneUI.ShowMessage("Replaying...", false, 5f);
						UnityEngine.Debug.Log("Replay Started");
					}

				}
				catch (Exception e)
				{
					UnityEngine.Debug.Log("Replay Activation Failed: " + e.ToString());
				}
			}
			void Start()
			{

			}
			void Update()
			{
				//Replay();
			}

			public void Replay()
			{
				try
				{
					if (Instance.RecordData == null || Instance.RecordData.Count < 3)
					{
						return;
					}
					else if (!FlightSceneScript.Instance.TimeManager.Paused)
					{
						ICommandPod cp = craft.CraftScript.ActiveCommandPod;
						if (FlightSceneScript.Instance.TimeManager.CurrentMode.TimeMultiplier > 1)
						{
							FlightSceneScript.Instance.TimeManager.SetNormalSpeedMode();
						}
						if (frame < Instance.RecordData.Count - 1)
						{
							float lerp = updateTimer / Instance.MaxUpdateIntervalMs;
							if (ModSettings.Instance.ReplayMode == ModSettings.replayModes.Precise)
							{
								CraftUtils.InterpolatedTransform(
									craft,
									Instance.RecordData[frame].Position,
									Instance.RecordData[frame + 1].Position,
									Instance.RecordData[frame].Velocity,
									Instance.RecordData[frame + 1].Velocity,
									Instance.RecordData[frame].Heading,
									Instance.RecordData[frame + 1].Heading,
									lerp
								);
							}
							else if (ModSettings.Instance.ReplayMode == ModSettings.replayModes.Normal)
							{
								CraftUtils.InterpolatedTransform(
									craft,
									Instance.RecordData[frame].Velocity,
									Instance.RecordData[frame + 1].Velocity,
									Instance.RecordData[frame].Heading,
									Instance.RecordData[frame + 1].Heading,
									lerp
								);
							}

							if (ModSettings.Instance.ReplayCraftControls)
							{
								cp.Controls.Pitch = Mathf.Lerp(Instance.RecordData[frame].Pitch, Instance.RecordData[frame + 1].Pitch, lerp);
								cp.Controls.Yaw = Mathf.Lerp(Instance.RecordData[frame].Yaw, Instance.RecordData[frame + 1].Yaw, lerp);
								cp.Controls.Roll = Mathf.Lerp(Instance.RecordData[frame].Roll, Instance.RecordData[frame + 1].Roll, lerp);
								cp.Controls.Throttle = Mathf.Lerp(Instance.RecordData[frame].Throttle, Instance.RecordData[frame + 1].Throttle, lerp);
								cp.Controls.Brake = Mathf.Lerp(Instance.RecordData[frame].Brake, Instance.RecordData[frame + 1].Brake, lerp);
								cp.Controls.Slider1 = Mathf.Lerp(Instance.RecordData[frame].Slider1, Instance.RecordData[frame + 1].Slider1, lerp);
								cp.Controls.Slider2 = Mathf.Lerp(Instance.RecordData[frame].Slider2, Instance.RecordData[frame + 1].Slider2, lerp);
								cp.Controls.Slider3 = Mathf.Lerp(Instance.RecordData[frame].Slider3, Instance.RecordData[frame + 1].Slider3, lerp);
								cp.Controls.Slider4 = Mathf.Lerp(Instance.RecordData[frame].Slider4, Instance.RecordData[frame + 1].Slider4, lerp);
								cp.Controls.TranslateForward = Mathf.Lerp(Instance.RecordData[frame].TranslateForward, Instance.RecordData[frame + 1].TranslateForward, lerp);
								cp.Controls.TranslateRight = Mathf.Lerp(Instance.RecordData[frame].TranslateRight, Instance.RecordData[frame + 1].TranslateRight, lerp);
								cp.Controls.TranslateUp = Mathf.Lerp(Instance.RecordData[frame].TranslateUp, Instance.RecordData[frame + 1].TranslateUp, lerp);

								for (int i = 0; i < 10; i++)
								{
									cp.SetActivationGroupState(i + 1, Instance.RecordData[frame].ActivationGroupStates[i]);
								}
							}
							
							if (ModSettings.Instance.RecordBodiesTransform)
							{
								foreach (var body in craft.CraftScript.Data.Assembly.Bodies)
								{
									bodyrecdata d0 = Instance.BodyRecordData[frame].Find(x => x.Id == body.Id);
									bodyrecdata d1 = Instance.BodyRecordData[frame + 1].Find(x => x.Id == body.Id);
									CraftUtils.InterpolatedBodyTransform(
									body,
									d0.Position,
									d1.Position,
									d0.Heading,
									d1.Heading,
									lerp
									);
								}
							}
							
							CraftUtils.RecalculateFrameState(FlightSceneScript.Instance.ViewManager.GameView.ReferenceFrame, craft);
						}
						else
						{
							UnityEngine.Debug.Log("Replay Completed!");
							FlightSceneScript.Instance.FlightSceneUI.ShowMessage("Replay Completed", false, 5f);
							FlightSceneScript.Instance.TimeManager.RequestPauseChange(true, false);
							GameObject.Destroy(Instance.ReplayController);
							Instance.ReplayController = null;
						}

						updateTimer += Time.deltaTime * 1000;
						if (updateTimer >= Instance.MaxUpdateIntervalMs)
						{
							for (int i = cp.CurrentStage; i < Instance.RecordData[frame].Stage; i++)
								cp.ActivateStage();
							
							updateTimer = 0;
							frame++;
						}

					}

				}

				catch (Exception e)
				{
					UnityEngine.Debug.Log("Update Failed: " + e.ToString());
				}
			}
			float updateTimer = 0;
			int frame = 0;
		}


	}
	
	[HarmonyPatch(typeof(CraftNode))]
	[HarmonyPatch("UpdateCraft")]
	public class CraftUpdatePatch
	{
		private static void Postfix(ref CraftNode __instance)
		{
			try
			{
				if (Mod.Instance.ReplayController != null)
				{
					if (Mod.Instance.ReplayController.GetComponent<Mod.ReplaySystem>() && __instance.NodeId == (Mod.Instance.CraftNodeID == -2147483647 ? FlightSceneScript.Instance.CraftNode.NodeId : Mod.Instance.CraftNodeID))
					{
						Mod.Instance.ReplayController.GetComponent<Mod.ReplaySystem>().Replay();
					}

					else if (Mod.Instance.ReplayController.GetComponent<Mod.RecordSystem>() && __instance.NodeId == FlightSceneScript.Instance.CraftNode.NodeId)
					{
						Mod.Instance.ReplayController.GetComponent<Mod.RecordSystem>().Record();
					}
				}
			}
			catch (Exception e)
			{
				UnityEngine.Debug.Log("Patch Update Failed: " + e.ToString());
			}
		}
	}

}



