namespace Tmex.Websockets
{
    public enum ConnectionState
    {
        None = 0,
        Closed,
        Connecting,
        Connected,
        Closing
    }
}