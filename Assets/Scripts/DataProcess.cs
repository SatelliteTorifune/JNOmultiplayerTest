using UnityEngine;


namespace Assets.Scripts
{
    public static class DataProcess
    {
        
        public static void LogRecordData(Mod.recdata data)
        {
            // 格式化输出速度、位置和朝向
                Debug.LogFormat("Recorded Frame - Velocity: {0}Position: {1}Heading: {2}", 
                    data.Velocity,
                    data.Position.ToString(),
                    data.Heading.ToString()
                    
            );
        }
    }
}