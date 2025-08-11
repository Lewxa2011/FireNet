public interface INetworkBehaviour
{
    string NetworkId { get; }
    void OnRPC(string methodName, object[] parameters);
}