using System;
namespace AppGWBEHealthVMSS.shared
{
    public static class Utils
    {
        public static int GetEnvVariableOrDefault(string name, int defaultValue)
        {
            var val = System.Environment.GetEnvironmentVariable(name);
            if (string.IsNullOrEmpty(val))
            {
                return defaultValue;
            }
            else
            {
                return Int32.Parse(val);
            }
        }

        public static string GetEnvVariableOrDefault(string name, string defaultValue = null)
        {
            var val = System.Environment.GetEnvironmentVariable(name);
            if (string.IsNullOrEmpty(val))
            {
                return defaultValue;
            }
            else
            {
                return val;
            }
        }
    }
}
