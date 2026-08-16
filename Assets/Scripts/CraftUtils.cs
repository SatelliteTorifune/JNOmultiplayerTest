using System;
using System.Collections.Generic;
using System.Linq;

using ModApi.Craft;
using ModApi.Craft.Parts;
using ModApi.Craft.Parts.Modifiers;
using ModApi.Flight.GameView;

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
				Mod.Log("Calculations Disabled");
				return true;
			}
			catch (Exception e)
			{
				FlightSceneScript.Instance.FlightSceneUI.ShowMessage(e.ToString(), false, 10f);
				return false;
			}
		}
	}
}
