using Microsoft.Extensions.Logging;
using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net.WebSockets;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Tmex.Websockets.Client
{
    public partial class ClientWebsocketReactive
    {
        private class Manager : IDisposable
        {
            private readonly object _syncRoot = new object();
            private readonly ILogger _logger;
            private readonly ConnectWebSocketAsyncDelegate _factory;
            private volatile bool _disposing = false;
            private bool _disposed = false;

            private Uri _uri;
            private Thread _mainThread;
            private Thread _sendingThread;
            private TaskCompletionSource<bool> _startTask;
            private CancellationTokenSource _stop;
            private CancellationTokenSource _sendingToken;
            private ManualResetEvent _shutdown;

            private readonly AutoResetEvent _socketChange = new AutoResetEvent(false);
            private readonly AutoResetEvent _socketChangeCompleted = new AutoResetEvent(false);
            private readonly object _socketLock = new object();
            private WebSocket _newSocket;

            private long _socketId = 1L;
            private readonly ConcurrentQueue<WsMessageWrapper> _sendingQueue = new ConcurrentQueue<WsMessageWrapper>();
            private readonly AutoResetEvent _hasMessageToSend = new AutoResetEvent(false);

            private readonly Subject<WsMessage> _receivedMessages = new Subject<WsMessage>();
            private readonly Subject<ConnectionState> _stateChanges = new Subject<ConnectionState>();

            public IObservable<WsMessage> ReceivedMessages => _receivedMessages.AsObservable();
            public IObservable<ConnectionState> StateChanges => _stateChanges.AsObservable();

            public ConnectionState State
            {
                get => _state;
                set
                {
                    if (_state == value) return;
                    _state = value;
                    _stateChanges.OnNext(value);
                }
            }

            public bool IsRunning { get; private set; }

            public TimeSpan ReconnectCooldown { get; set; } = TimeSpan.FromSeconds(1);

            public Manager(ILogger logger, ConnectWebSocketAsyncDelegate factory)
            {
                _logger = logger;
                _factory = factory;
            }

            public void Dispose()
            {
                if (_disposed)
                    throw new ObjectDisposedException(nameof(Manager));

                Thread t = null;
                lock (_syncRoot)
                {
                    if (_disposing) return;
                    t = _mainThread;
                    _disposing = true;

                    if (t != null)
                        _stop.Cancel(false);
                    else
                        _stop = null;
                }

                t?.Join();

                _disposed = true;
                _shutdown?.Dispose();
                _stop?.Dispose();

                _receivedMessages.Dispose();
                _stateChanges.Dispose();
            }

            public Task StartAsync(Uri uri)
            {
                if (_disposed)
                    return Task.FromException(new ObjectDisposedException(nameof(Manager)));
                if (_disposing)
                    return Task.FromCanceled(new CancellationToken(true));

                Task result;
                lock (_syncRoot)
                {
                    if (_mainThread != null)
                        return Task.CompletedTask;

                    IsRunning = true;

                    _uri = uri;
                    _stop = new CancellationTokenSource();

                    _shutdown?.Dispose();
                    _shutdown = new ManualResetEvent(false);

                    _mainThread = new Thread(Watch);
                    _startTask = new TaskCompletionSource<bool>();
                    result = _startTask.Task;
                }

                _mainThread.Start();
                return result;
            }

            public Task StopAsync()
            {
                if (_disposed)
                    return Task.FromException(new ObjectDisposedException(nameof(Manager)));
                if (_disposing)
                    return Task.FromCanceled(new CancellationToken(true));

                lock (_syncRoot)
                {
                    if (_mainThread == null)
                        return Task.CompletedTask;
                }

                return Task.Run(() =>
                {
                    EventWaitHandle shutdown = null;
                    lock (_syncRoot)
                    {
                        if (_mainThread == null)
                            return;

                        shutdown = _shutdown;
                        _stop.Cancel(false);
                    }

                    shutdown.WaitOne();
                });
            }

            public void Send(WsMessage msg)
            {
                _sendingQueue.Enqueue(new WsMessageWrapper
                {
                    Message = msg,
                    SocketId = Interlocked.Read(ref _socketId)
                });
                _hasMessageToSend.Set();
            }

            private void Watch()
            {
                _startTask.TrySetResult(true);
                _startTask = null;

                _socketChange.Reset();
                _socketChangeCompleted.Reset();

                _sendingToken = new CancellationTokenSource();
                _sendingThread = new Thread(SendingLoop);
                _sendingThread.Start();

                WebSocket socket = null;

                var buffer = new ArraySegment<byte>(new byte[16384]);
                var messageStream = new MemoryStream(new byte[buffer.Count * 2], true);

                while (!_disposing && !_stop.IsCancellationRequested)
                {
                    try
                    {
                        if (socket == null)
                        {
                            socket = OpenSocket();
                        }
                        else if (socket.State == WebSocketState.Closed || socket.State == WebSocketState.Aborted)
                        {
                            CloseSocket(socket);
                            socket = null;
                            State = ConnectionState.Closed;
                        }
                        else if (socket.State == WebSocketState.Open)
                        {
                            State = ConnectionState.Connected;
                            ReceiveNextMessage(socket, buffer, messageStream).GetAwaiter().GetResult();
                        }
                    }
                    catch (Exception e)
                    {
                        _logger.LogError(e, string.Empty);
                        CloseSocket(socket);
                        socket = null;
                        State = ConnectionState.Closed;
                        continue;
                    }
                    finally { messageStream.Position = 0; }
                }

                messageStream.Dispose();

                CloseSocket(socket);
                socket = null;
                State = ConnectionState.Closed;

                _sendingToken.Cancel(false);
                _sendingThread.Join();
                _sendingToken.Dispose();
                _sendingToken = null;

                var shutdown = _shutdown;
                lock (_syncRoot)
                {
                    _mainThread = null;
                    _stop.Dispose();
                }

                IsRunning = false;
                State = ConnectionState.None;
                shutdown.Set();
            }

            #region Connection delay

            private bool _delayReconnect = false;
            private int _delayRemaining = 0;
            private ConnectionState _state;

            private void SetConnectionDelay(TimeSpan delay)
            {
                _delayReconnect = true;
                _delayRemaining = (int) delay.TotalMilliseconds;
            }

            private bool IsDelayedConnection()
            {
                if (!_delayReconnect)
                    return false;
                if (_delayRemaining == 0)
                {
                    _delayReconnect = false;
                    return false;
                }
                var sleep = _delayRemaining > 100 ? 100 : _delayRemaining;
                _delayRemaining -= sleep;
                Thread.Sleep(sleep);
                return false;
            }

            #endregion

            private async Task ReceiveNextMessage(WebSocket socket, ArraySegment<byte> buffer, MemoryStream messageStream)
            {
                var receivedBytes = 0;
                Task<WebSocketReceiveResult> readTask = null;
                var expectedType = WebSocketMessageType.Text;
                while (!_disposing && !_stop.IsCancellationRequested && socket.State == WebSocketState.Open)
                {
                    if (readTask == null)
                        readTask = socket.ReceiveAsync(buffer, CancellationToken.None);

                    var readTimeout = Task.Delay(500);
                    var completedTask = await Task.WhenAny(readTask, readTimeout);
                    if (completedTask == readTimeout)
                        continue;

                    var result = await readTask;
                    readTask = null;

                    if (result.Count > 0)
                    {
                        if (messageStream.Position == 0)
                            expectedType = result.MessageType;
                        else if (expectedType != result.MessageType)
                            throw new Exception("Unexpected message type for split message");

                        messageStream.Write(buffer.Array, buffer.Offset, result.Count);
                        receivedBytes += result.Count;
                    }

                    if (!result.EndOfMessage)
                        continue;

                    // Reset stream position for reading
                    messageStream.Position = 0;
                    messageStream.SetLength(receivedBytes);

                    OnMessageReceived(socket, result.MessageType, messageStream);

                    // Reset stream position for writing
                    messageStream.Position = 0;
                    receivedBytes = 0;
                }
            }

            private void OnMessageReceived(WebSocket socket, WebSocketMessageType type, MemoryStream data)
            {
                if (type == WebSocketMessageType.Close)
                {
                    _logger.LogTrace("RECV: CLOSE");
                    _receivedMessages.OnNext(new WsMessage { Type = type });
                    CloseSocket(socket);
                    return;
                }

                if (type == WebSocketMessageType.Text)
                {
                    var msg = Encoding.UTF8.GetString(data.ToArray());
                    _logger.LogTrace("RECV: {msg}", msg);
                    _receivedMessages.OnNext(new WsMessage
                    {
                        Text = msg,
                        Type = WebSocketMessageType.Text
                    });
                }
                else if (type == WebSocketMessageType.Binary)
                {
                    _logger.LogTrace("RECV: BINARY {l} bytes", data.Length);
                    _receivedMessages.OnNext(new WsMessage
                    {
                        Binary = data.ToArray(),
                        Type = WebSocketMessageType.Binary
                    });
                }
            }

            private void SendingLoop()
            {
                WebSocket socket = null;
                var socketId = 0L;
                while (!_sendingToken.IsCancellationRequested)
                {
                    try
                    {
                        if (socket == null)
                        {
                            socket = GetNewSocket(50, out socketId);
                            continue;
                        }
                        if (socket.State == WebSocketState.Closed || socket.State == WebSocketState.Aborted)
                        {
                            socket = null;
                            continue;
                        }
                        if (socket.State != WebSocketState.Open)
                        {
                            Thread.Sleep(50);
                            continue;
                        }
                    }
                    catch (Exception e)
                    {
                        _logger.LogError(e, string.Empty);
                        socket = null;
                        continue;
                    }

                    if (_sendingQueue.TryDequeue(out var wrapper))
                    {
                        var msg = wrapper.Message;
                        if (socketId != wrapper.SocketId)
                        {
                            msg.Cancel();
                            continue;
                        }

                        try
                        {
                            if (msg.Completion?.IsCanceled ?? false)
                                continue;
                            SendMessage(socket, msg).GetAwaiter().GetResult();
                            msg.Complete();
                        }
                        catch (Exception e)
                        {
                            _logger.LogError(e, string.Empty);
                            msg.Fail(e);
                        }
                    }
                    else
                    {
                        _hasMessageToSend.WaitOne(10);
                    }
                }
            }

            private async Task SendMessage(WebSocket socket, WsMessage msg)
            {
                ArraySegment<byte> data;
                if (msg.Type == WebSocketMessageType.Text)
                {
                    _logger.LogTrace("SEND: {msg}", msg.Text);
                    data = new ArraySegment<byte>(Encoding.UTF8.GetBytes(msg.Text));
                }
                else if (msg.Type == WebSocketMessageType.Binary)
                {
                    _logger.LogTrace("SEND: BINARY {l} bytes", msg.Binary.Length);
                    data = new ArraySegment<byte>(msg.Binary);
                }
                else if (msg.Type == WebSocketMessageType.Close)
                {
                    _logger.LogTrace("SEND: CLOSE");
                    data = new ArraySegment<byte>(Encoding.UTF8.GetBytes("Close"));
                }

                await socket.SendAsync(data, msg.Type, true, CancellationToken.None);
            }

            private WebSocket OpenSocket()
            {
                try
                {
                    if (IsDelayedConnection())
                        return null;

                    State = ConnectionState.Connecting;
                    var socket = _factory(_uri, _stop.Token).GetAwaiter().GetResult();
                    if (socket != null)
                        SetNewSocket(socket);
                    return socket;
                }
                catch (Exception e)
                {
                    _logger.LogError(e, string.Empty);
                }
                return null;
            }

            private void CloseSocket(WebSocket socket)
            {
                try
                {
                    if (socket == null) return;
                    State = ConnectionState.Closing;
                    socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Close", CancellationToken.None);
                }
                catch (Exception e)
                {
                    _logger.LogError(e, string.Empty);
                }
                finally
                {
                    try { socket.Dispose(); }
                    catch (Exception) { }

                    // todo reconnect attempts
                    SetConnectionDelay(ReconnectCooldown);
                }
            }

            private void SetNewSocket(WebSocket s)
            {
                lock (_socketLock)
                {
                    _newSocket = s;
                    Interlocked.Increment(ref _socketId);
                }
                _socketChangeCompleted.Reset();
                _socketChange.Set();
                _socketChangeCompleted.WaitOne();
            }

            private WebSocket GetNewSocket(int timeoutMs, out long socketId)
            {
                socketId = 0L;
                if (!_socketChange.WaitOne(timeoutMs))
                    return null;
                WebSocket socket;
                lock (_socketLock)
                {
                    socket = _newSocket;
                    _newSocket = null;
                    socketId = _socketId;
                }
                _socketChangeCompleted.Set();
                return socket;
            }

            private class WsMessageWrapper
            {
                public WsMessage Message { get; set; }
                public long SocketId { get; set; }
            }
        }
    }
}