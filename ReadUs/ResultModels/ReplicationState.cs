namespace ReadUs.ResultModels;

public enum ReplicationState
{
    // <summary>
    // The instance needs to connect to its primary.
    // </summary>
    Connect,

    // <summary>
    // The connection to its primary is in progress.
    // </summary>        
    Connecting,

    // <summary>
    // The replica is synchronizing with its primary.
    // </summary>        
    Sync,

    // <summary>
    // The replica is online.
    // </summary>        
    Connected
}

internal static class ReplicationStates
{
    internal const string Connect = "connect";
    internal const string Connecting = "connecting";
    internal const string Sync = "sync";
    internal const string Connected = "connected";
}