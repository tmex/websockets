using System;
using System.Threading.Tasks;

namespace Tmex.Websockets.Client
{
    public interface IClientWebsocketReactive : IDisposable
    {
        /// <summary>
        /// Current connection state
        /// </summary>
        ConnectionState State { get; }

        /// <summary>
        /// If started
        /// </summary>
        bool IsRunning { get; }

        /// <summary>
        /// Connection state changes events
        /// </summary>
        IObservable<ConnectionState> StateChanges { get; }

        /// <summary>
        /// Incoming messages stream
        /// </summary>
        IObservable<WsMessage> Receiver { get; }

        /// <summary>
        /// Outgoing messages stream
        /// </summary>
        IObserver<WsMessage> Sender { get; }

        /// <summary>
        /// Starts receiving and sending threads,
        /// completes when receiving thread has been started
        /// </summary>
        Task StartAsync(Uri uri);

        /// <summary>
        /// Signals internal threads to stop and waits for completion
        /// </summary>
        Task StopAsync();

        /// <summary>
        /// Force reconnect if running. Returns false if not running.
        /// </summary>
        bool Reconnect();

        /// <summary>
        /// Create and send binary message
        /// </summary>
        /// <returns>message that was sent or exception</returns>
        Task<WsMessage> SendAsync(byte[] data);

        /// <summary>
        /// Create and send text message
        /// </summary>
        /// <returns>message that was sent or exception</returns>
        Task<WsMessage> SendAsync(string data);
    }
}