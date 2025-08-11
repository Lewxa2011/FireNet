using System;
using System.Collections.Generic;

namespace FireNet
{
    public static class DictionaryExtensions
    {
        public static T GetValueOrDefault<T>(this Dictionary<string, object> dictionary, string key, T defaultValue = default(T))
        {
            if (dictionary.TryGetValue(key, out object value))
            {
                try
                {
                    return (T)Convert.ChangeType(value, typeof(T));
                }
                catch
                {
                    return defaultValue;
                }
            }
            return defaultValue;
        }
    }
}