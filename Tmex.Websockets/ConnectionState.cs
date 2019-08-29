namespace Tmex.Websockets
{
    public enum ConnectionState
    {
        /// <summary>
        /// Socket manager is stopped
        /// </summary>
        None = 0,

        /// <summary>
        /// Socket is closed
        /// </summary>
        Closed = 1,

        /// <summary>
        /// Trying to open socket connection
        /// </summary>
        Connecting = 2,

        /// <summary>
        /// Successful connection
        /// </summary>
        Connected = 3,

        /// <summary>
        /// Closing socket normally
        /// </summary>
        Closing = 4
    }
}