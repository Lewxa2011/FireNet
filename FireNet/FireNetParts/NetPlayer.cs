using Firebase.Database;
using System;
using System.Collections.Generic;
using UnityEngine;

[System.Serializable]
public class NetPlayer
{
    public string userId;
    public string nickName;
    public bool isMasterClient;
    public Dictionary<string, object> customProperties = new Dictionary<string, object>();

    public NetPlayer(string userId, string nickName, bool isMasterClient)
    {
        this.userId = userId;
        this.nickName = nickName;
        this.isMasterClient = isMasterClient;
    }

    public Dictionary<string, object> ToDict()
    {
        return new Dictionary<string, object>
        {
            ["userId"] = userId,
            ["nickName"] = nickName,
            ["isMasterClient"] = isMasterClient,
            ["customProperties"] = customProperties
        };
    }

    public static NetPlayer FromSnapshot(DataSnapshot snapshot)
    {
        try
        {
            var data = snapshot.Value as Dictionary<string, object>;
            if (data == null) return null;

            string userId;
            if (data.ContainsKey("userId"))
            {
                userId = data["userId"]?.ToString();
            }
            else
            {
                
                userId = snapshot.Key;
                Debug.LogWarning($"userId missing in snapshot for {snapshot.Key}, using snapshot.Key instead.");
            }

            string nickName = data.ContainsKey("nickName")
                ? data["nickName"]?.ToString()
                : $"Player_{userId.Substring(0, 6)}";

            bool isMasterClient = data.ContainsKey("isMasterClient") &&
                                  Convert.ToBoolean(data["isMasterClient"]);

            var player = new NetPlayer(userId, nickName, isMasterClient);

            if (data.ContainsKey("customProperties"))
            {
                player.customProperties =
                    data["customProperties"] as Dictionary<string, object> ??
                    new Dictionary<string, object>();
            }

            return player;
        }
        catch (Exception e)
        {
            Debug.LogError($"Error parsing player data: {e.Message}");
            return null;
        }
    }
}