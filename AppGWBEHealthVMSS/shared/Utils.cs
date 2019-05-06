using System;
namespace AppGWBEHealthVMSS.shared
{
    public static class Utils
    {
        /// <summary>
        /// Gets the env variable or default.
        /// </summary>
        /// <returns>The env variable or default.</returns>
        /// <param name="name">Name.</param>
        /// <param name="defaultValue">Default value.</param>
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

        /// <summary>
        /// Gets the env variable or default.
        /// </summary>
        /// <returns>The env variable or default.</returns>
        /// <param name="name">Name.</param>
        /// <param name="defaultValue">Default value.</param>
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
