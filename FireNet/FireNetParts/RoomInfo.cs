using System.Collections.Generic;

[System.Serializable]
public class RoomInfo
{
    public string name;
    public int playerCount;
    public int maxPlayers;
    public bool isOpen;
    public bool isVisible;
    public Dictionary<string, object> customProperties;
    public bool IsJoinable => isOpen && playerCount < maxPlayers;
}