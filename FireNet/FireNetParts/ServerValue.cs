using System.Collections.Generic;

namespace FireNet
{
    public static class ServerValue
    {
        public static Dictionary<string, object> Timestamp => new Dictionary<string, object> { [".sv"] = "timestamp" };
    }
}