using System;
using System.Net.WebSockets;
using System.Threading.Tasks;

namespace Tmex.Websockets
{
    public class WsMessage
    {
        private readonly TaskCompletionSource<WsMessage> _completion;

        public WebSocketMessageType Type { get; set; }

        public byte[] Binary { get; set; }

        public string Text { get; set; }

        /// <summary>
        /// Optional task to track message sending
        /// </summary>
        public Task<WsMessage> Completion { get; }

        public WsMessage(bool track = false)
        {
            _completion = track ? new TaskCompletionSource<WsMessage>() : null;
            Completion = _completion?.Task;
        }

        public void Complete() => _completion?.SetResult(this);

        public void Fail(Exception e) => _completion?.SetException(e);

        public void Cancel() => _completion?.SetCanceled();
    }
}