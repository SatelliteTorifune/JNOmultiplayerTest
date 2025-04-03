using System.Collections.Generic;
using UnityEngine;


namespace Assets.Scripts
{
    
    public class DataProcess
    {
         Vector3d _Position;
         Vector3d _Velocity;
         Quaterniond _Rotation;
         
         public float Pitch;
         public float Yaw;
         public float Roll;
         public float Throttle;
         public float Brake;

         public float Slider1;
         public float Slider2;
         public float Slider3;
         public float Slider4;

         public float TranslateForward;
         public float TranslateRight;
         public float TranslateUp;

         public List<bool> _ActivationGroupStates;

         public int Stage;


         public void DPupdate(Mod.recdata data)
         {
             Pitch = data.Pitch;
             Yaw = data.Yaw;
             Roll = data.Roll;
             Throttle = data.Throttle;
             Slider1 = data.Slider1;
             Slider2 = data.Slider2;
             Slider3 = data.Slider3;
             Slider4 = data.Slider4;
             TranslateRight = data.TranslateRight;
             TranslateForward = data.TranslateForward;
             TranslateUp = data.TranslateUp;
             Stage = data.Stage;
             List<bool> _ActivationGroupStates = data.ActivationGroupStates;

         }
        public static void LogRecordData(Mod.recdata data)
        {
                //格式化输出速度、位置和朝向
                    Debug.LogFormat("Recorded Frame - Velocity: {0}Position: {1}Heading: {2} Pitch:{3}", 
                    data.Velocity,
                    data.Position.ToString(),
                    data.Heading.ToString(),
                    data.Pitch
                    
            );
        }
    }
}