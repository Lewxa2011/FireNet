using System.Collections.Generic;

[System.Serializable]
public class RoomOptions
{
    public int maxPlayers = 4;
    public bool isOpen = true;
    public bool isVisible = true;
    public Dictionary<string, object> customRoomProperties = new Dictionary<string, object>();

    public RoomOptions() { }
    public RoomOptions(int maxPlayers, bool isOpen = true, bool isVisible = true)
    {
        this.maxPlayers = maxPlayers;
        this.isOpen = isOpen;
        this.isVisible = isVisible;
    }
}