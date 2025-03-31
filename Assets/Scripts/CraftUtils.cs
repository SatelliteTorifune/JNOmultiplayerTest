using System;
using System.Collections.Generic;
using System.Linq;

using ModApi;
using ModApi.Craft;
using ModApi.Craft.Parts;
using ModApi.Craft.Parts.Modifiers;
using ModApi.Flight.GameView;
using ModApi.Flight.Sim;

using Assets.Scripts.Craft;
using Assets.Scripts.Flight;
using Assets.Scripts.Flight.Sim;
using UnityEngine;

namespace Assets.Scripts
{
	public class CraftUtils
	{
		//给点扰动用的,这一坨别动嗷
		public static void SetStateVectorsAtDefaultTime(Vector3d position, Vector3d velocity, CraftNode craft)
		{
			if (velocity == Vector3d.zero)
			{
				velocity = new Vector3d(0.0001, 0.0001, 0.0001);
			}
			craft.SetStateVectors(position, velocity, FlightSceneScript.Instance.FlightState.Time);
		}

		public static void RecalculateFrameState(IReferenceFrame referenceFrame, CraftNode craft)
		{
			Vector3 positionDelta = referenceFrame.PlanetToFramePosition(craft.Position) - ((CraftScript)craft.CraftScript).FramePosition;
			Vector3 velocityDelta = referenceFrame.PlanetToFrameVelocity(craft.Velocity) - ((CraftScript)craft.CraftScript).FrameVelocity;
			Vector3 frameZeroVelocity = Vector3.zero;
			if (!referenceFrame.IsSurfaceLocked)
			{
				frameZeroVelocity = referenceFrame.PlanetToFrameVector(referenceFrame.Velocity);
			}
			RecalculateFrameState(positionDelta, velocityDelta, frameZeroVelocity, (CraftScript)craft.CraftScript);
			if (craft.CraftScript.IsPhysicsEnabled)
			{
				((CraftScript)craft.CraftScript).RecenterTransformOnCoM(true);
			}
		}

		public static void RecalculateFrameState(Vector3 positionDelta, Vector3 velocityDelta, Vector3 frameZeroVelocity, CraftScript craft)
		{
			List<BodyData> list = null;
			Vector3 position = craft.RootPart.Transform.position;
			List<BodyData> bodies = new List<BodyData>(craft.Data.Assembly.Bodies.Where(b => !b.BodyScript.Disconnected && !b.IsDestroyed && b.BodyScript != null));
			for (int i = 0; i < bodies.Count; i++)
			{
				BodyData bodyData = bodies[i];
				Rigidbody rigidBody = bodyData.BodyScript.RigidBody;
				if ((craft.CraftNode.AltitudeAgl > 500.0 || craft.CraftNode.IsDestroyed) && bodyData.BodyScript.IsDebris && ((rigidBody.transform.position - position).sqrMagnitude > 1000000f || craft.CraftNode.IsDestroyed))
				{
					if (list == null)
					{
						list = new List<BodyData>();
					}
					list.Add(bodyData);
				}
				rigidBody.transform.position += positionDelta;
				if (!rigidBody.isKinematic)
				{
					rigidBody.velocity += velocityDelta;
				}
				((BodyScript)bodyData.BodyScript).OnRecentered();
			}
			if (list != null)
			{
				foreach (BodyData body in list)
				{
					craft.DestroyBody(body);
				}
			}

			//craft.GetType().GetField("_frameVelocity", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).SetValue(craft, null);
			ParticleSystem[] componentsInChildren = craft.gameObject.GetComponentsInChildren<ParticleSystem>();
			for (int i = 0; i < componentsInChildren.Length; i++)
			{
				CraftScript.RepositionParticleSystem(componentsInChildren[i], positionDelta, velocityDelta);
			}
			/*
			IReadOnlyList<PartData> parts = craft.Data.Assembly.Parts;
			for (int j = 0; j < parts.Count; j++)
			{
				List<PartModifierScript> modifiers = parts[j].PartScript.Modifiers;
				for (int k = 0; k < modifiers.Count; k++)
				{
					modifiers[k].RecalculateFrameState(positionDelta, velocityDelta);
				}
			}
			*/
			
		}
		//通过PCL坐标设置位置
		public static bool SetCraftTransform(CraftNode craft, Vector3d position, Vector3d velocity, Quaternion orientation)
		{
			try
			{
				SetStateVectorsAtDefaultTime(position, velocity, craft);
				craft.CraftScript.Transform.rotation = orientation;
				return true;
			}
			catch (Exception e)
			{
				FlightSceneScript.Instance.FlightSceneUI.ShowMessage(e.ToString(), false, 10f);
				return false;
			}
		}
		//通过地面位置与地面速度设置位置
		public static bool SetCraftTransform(CraftNode craft, Vector3d surfacePosition, Vector3d surfaceVelocity, Quaterniond groundedTransform, IPlanetNode planet)
		{
			try
			{
				var orientation = groundedTransform;
				var position = surfacePosition == Vector3d.zero ? craft.Position : planet.SurfaceVectorToPlanetVector(surfacePosition);
				var velocity = planet.SurfaceVectorToPlanetVector(surfaceVelocity);
				SetCraftTransform(craft, position, velocity, new Quaternion((float)orientation.x, (float)orientation.y, (float)orientation.z, (float)orientation.w));
				return true;
			}
			catch (Exception e)
			{
				FlightSceneScript.Instance.FlightSceneUI.ShowMessage(e.ToString(), false, 10f);
				return false;
			}
		}
		//我操,我他妈这么知道Quaternion是什么东西
		public static bool SetCraftTransform(CraftNode craft, Vector3d surfacePosition, Vector3d surfaceVelocity, Quaternion groundedTransform, IPlanetNode planet)
		{
			try
			{
				var rotation = groundedTransform;
				var position = surfacePosition == Vector3d.zero ? craft.Position : planet.SurfaceVectorToPlanetVector(surfacePosition);
				var velocity = planet.SurfaceVectorToPlanetVector(surfaceVelocity);
				SetCraftTransform(craft, position, velocity, rotation);
				return true;
			}
			catch (Exception e)
			{
				FlightSceneScript.Instance.FlightSceneUI.ShowMessage(e.ToString(), false, 10f);
				return false;
			}
		}
		//通过经纬度+高度加地面速度设置craft位置
		public static bool SetCraftTransform(CraftNode craft, double latitude, double longtitude, double asl, Vector3d surfaceVelocity, Quaternion groundedTransform, PlanetNode planet)
		{
			try
			{
				var rotation = new Quaternion((float)planet.Rotation.x, (float)planet.Rotation.y, (float)planet.Rotation.z, (float)planet.Rotation.w) * groundedTransform;
				var position = planet.GetSurfacePosition(latitude, longtitude, AltitudeType.AboveSeaLevel, asl);
				var velocity = planet.SurfaceVectorToPlanetVector(surfaceVelocity);
				SetCraftTransform(craft, position, velocity, rotation);
				return true;
			}
			catch (Exception e)
			{
				FlightSceneScript.Instance.FlightSceneUI.ShowMessage(e.ToString(), false, 10f);
				return false;
			}
		}
		//禁用craft1的物理计算更新
		public static bool DisableCraftPhysicCalculation(ref CraftNode craft)
		{
			try
			{
				List<PartData> parts = new List<PartData>(craft.CraftScript.Data.Assembly.Parts);
				foreach (PartData part in parts)
				{
					part.Damage = -2147483647;
					part.PartDrag.ClearDrag();
					part.PartScript.Colliders.Clear();

					ConfigData config = (ConfigData)part.Config;
					config.PreventDebris = true;
					config.IncludeInDrag = false;
					config.HeatShield = 2147483647;
				}


				List<BodyData> bodies = new List<BodyData>(craft.CraftScript.Data.Assembly.Bodies);
				foreach (BodyData body in bodies)
				{
					GameObject obj = ((BodyScript)body.BodyScript).GameObject;
					foreach (Joint j in obj.GetComponentsInChildren<Joint>(true))
					{
						//GameObject.DestroyImmediate(j);
					}
					foreach (Rigidbody r in obj.GetComponentsInChildren<Rigidbody>(true))
					{
						//r.isKinematic = true;
					}
					foreach (Collider c in obj.GetComponentsInChildren<Collider>(true))
					{
						c.enabled = false;
					}

				}
				UnityEngine.Debug.Log("Calculations Disabled");
				return true;
			}
			catch (Exception e)
			{
				FlightSceneScript.Instance.FlightSceneUI.ShowMessage(e.ToString(), false, 10f);
				return false;
			}
		}

		public static bool SetBodyTransform(BodyData body, Vector3 position, Quaternion orientation)
		{
			try
			{
				body.BodyScript.Transform.position = position;
				body.BodyScript.Transform.rotation = orientation;
				return true;
			}
			catch (Exception e)
			{
				FlightSceneScript.Instance.FlightSceneUI.ShowMessage(e.ToString(), false, 10f);
				return false;
			}
		}

		public static bool SetBodyTransform(BodyData body, Vector3d surfacePosition, Quaternion groundedTransform, IPlanetNode planet)
		{
			try
			{
				var rotation = new Quaternion((float)planet.RotationInverse.x, (float)planet.RotationInverse.y, (float)planet.RotationInverse.z, (float)planet.RotationInverse.w) * groundedTransform;
				var position = planet.SurfaceVectorToPlanetVector(surfacePosition);
				SetBodyTransform(body, new Vector3((float)position.x, (float)position.y, (float)position.z), rotation);
				return true;
			}
			catch (Exception e)
			{
				FlightSceneScript.Instance.FlightSceneUI.ShowMessage(e.ToString(), false, 10f);
				return false;
			}
		}
		//使用Vector3d.Lerp进行线性插值		
		//使用Quaternion.Slerp进行球面插值（保持旋转姿态的自然过渡）
		//percentage控制插值程度
		public static void InterpolatedTransform(CraftNode craft, Vector3d initialSurfacePosition, Vector3d targetSurfacePosition, Vector3d initialSurfaceVelocity, Vector3d targetSurfaceVelocity, Quaterniond initialGroundedTransform, Quaterniond targetGroundedTransform, double percentage)
		{

			Quaternion surfaceRotation = Quaternion.Slerp(
				new Quaternion(
					(float)initialGroundedTransform.x,
					(float)initialGroundedTransform.y,
					(float)initialGroundedTransform.z,
					(float)initialGroundedTransform.w
				),
				new Quaternion(
					(float)targetGroundedTransform.x,
					(float)targetGroundedTransform.y,
					(float)targetGroundedTransform.z,
					(float)targetGroundedTransform.w
				),
				(float)percentage

				
			);

			IPlanetNode planet = craft.Parent;
			Vector3d interpolatedPos = Vector3d.Lerp(initialSurfacePosition, targetSurfacePosition, percentage);
			Vector3d interpolatedVel = Vector3d.Lerp(initialSurfaceVelocity, targetSurfaceVelocity, percentage);

			SetCraftTransform(craft, interpolatedPos, interpolatedVel, surfaceRotation, planet);
		}

		public static void InterpolatedTransform(CraftNode craft, Vector3d initialSurfaceVelocity, Vector3d targetSurfaceVelocity, Quaterniond initialGroundedTransform, Quaterniond targetGroundedTransform, double percentage)
		{
			Quaterniond surfaceRotation = new Quaterniond(
				Quaternion.Slerp(
					new Quaternion(
						(float)initialGroundedTransform.x,
						(float)initialGroundedTransform.y,
						(float)initialGroundedTransform.z,
						(float)initialGroundedTransform.w
					),
					new Quaternion(
						(float)targetGroundedTransform.x,
						(float)targetGroundedTransform.y,
						(float)targetGroundedTransform.z,
						(float)targetGroundedTransform.w
					),
					(float)percentage
				)
			);

			IPlanetNode planet = craft.Parent;
			Vector3d interpolatedVel = Vector3d.Lerp(initialSurfaceVelocity, targetSurfaceVelocity, percentage);

			SetCraftTransform(craft, Vector3d.zero, interpolatedVel, surfaceRotation, planet);
		}

		public static void InterpolatedBodyTransform(BodyData body, Vector3 initialSurfacePosition, Vector3 targetSurfacePosition, Quaternion initialGroundedTransform, Quaternion targetGroundedTransform, double percentage)
		{

			Quaternion surfaceRotation = Quaternion.Slerp(initialGroundedTransform, targetGroundedTransform, (float)percentage);

			Vector3d interpolatedPos = Vector3d.Lerp(initialSurfacePosition, targetSurfacePosition, percentage);

			SetBodyTransform(body, new Vector3((float)interpolatedPos.x, (float)interpolatedPos.y, (float)interpolatedPos.z), surfaceRotation);
		}

	}
}

