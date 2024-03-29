﻿using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System;
using System.Net.WebSockets;
using System.Reactive;
using System.Threading;
using System.Threading.Tasks;

namespace Tmex.Websockets.Client
{
    public delegate Task<WebSocket> ConnectWebSocketAsyncDelegate(Uri uri, CancellationToken token = default(CancellationToken));

    public partial class ClientWebsocketReactive : IClientWebsocketReactive
    {
        private readonly ILogger _logger;
        private readonly ConnectWebSocketAsyncDelegate _factory;
        private readonly Manager _manager;

        public ConnectionState State => _manager.State;

        public bool IsRunning => _manager.IsRunning;

        public IObservable<ConnectionState> StateChanges => _manager.StateChanges;

        public IObservable<WsMessage> Receiver => _manager.ReceivedMessages;

        public IObserver<WsMessage> Sender { get; }

        public ClientWebsocketReactive(ILogger<ClientWebsocketReactive> logger = null, ConnectWebSocketAsyncDelegate factory = null)
        {
            _logger = (ILogger) logger ?? NullLogger.Instance;
            _factory = factory ?? ConnectDefaultSocketAsync;

            _manager = new Manager(_logger, _factory);
            Sender = Observer.Create<WsMessage>(_manager.Send);
        }

        public void Dispose() => _manager.Dispose();

        public Task<WsMessage> SendAsync(byte[] data)
        {
            var message = new WsMessage(true)
            {
                Binary = data,
                Type = WebSocketMessageType.Binary
            };
            _manager.Send(message);
            return message.Completion;
        }

        public Task<WsMessage> SendAsync(string data)
        {
            var message = new WsMessage(true)
            {
                Text = data,
                Type = WebSocketMessageType.Text
            };
            _manager.Send(message);
            return message.Completion;
        }

        public async Task StartAsync(Uri uri) => await _manager.StartAsync(uri);

        public async Task StopAsync() => await _manager.StopAsync();

        private async Task<WebSocket> ConnectDefaultSocketAsync(Uri uri, CancellationToken token = default(CancellationToken))
        {
            var socket = new ClientWebSocket();
            await socket.ConnectAsync(uri, token);
            return socket;
        }
    }
}