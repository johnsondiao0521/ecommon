﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using ECommon.Components;
using ECommon.Logging;
using ECommon.Scheduling;
using ECommon.Socketing.BufferManagement;
using ECommon.Socketing.Framing;
using ECommon.Utilities;

namespace ECommon.Socketing
{
    public interface ITcpConnection
    {
        EndPoint LocalEndPoint { get; }
        EndPoint RemotingEndPoint { get; }
        void Send(byte[] message);
        void Close();
    }
    public class TcpConnection : ITcpConnection
    {
        #region Private Variables

        private const int MaxSendPacketSize = 64 * 1024;

        private readonly Socket _socket;
        private readonly EndPoint _localEndPoint;
        private readonly EndPoint _remotingEndPoint;
        private readonly ConcurrentStack<SocketAsyncEventArgs> _sendSocketArgsStack;
        private readonly SocketAsyncEventArgs _receiveSocketArgs;
        private readonly IBufferPool _bufferPool;
        private readonly IMessageFramer _framer;
        private readonly ILogger _logger;
        private readonly ConcurrentQueue<ArraySegment<byte>> _sendingQueue = new ConcurrentQueue<ArraySegment<byte>>();
        private readonly ConcurrentQueue<ReceivedData> _receiveQueue = new ConcurrentQueue<ReceivedData>();
        private readonly MemoryStream _sendingStream = new MemoryStream();
        private readonly object _sendingLock = new object();
        private readonly object _receivingLock = new object();
        private readonly IScheduleService _scheduleService;

        private Action<ITcpConnection, SocketError> _connectionClosedHandler;
        private Action<ITcpConnection, byte[]> _messageArrivedHandler;

        private int _sending;
        private int _receiving;
        private int _parsing;

        private long _segmentCount;
        private long _flowControlCount;

        #endregion

        public bool IsConnected
        {
            get { return _socket.Connected; }
        }
        public Socket Socket
        {
            get { return _socket; }
        }
        public EndPoint LocalEndPoint
        {
            get { return _localEndPoint; }
        }
        public EndPoint RemotingEndPoint
        {
            get { return _remotingEndPoint; }
        }

        public TcpConnection(Socket socket, IBufferPool bufferPool, Action<ITcpConnection, byte[]> messageArrivedHandler, Action<ITcpConnection, SocketError> connectionClosedHandler)
        {
            Ensure.NotNull(socket, "socket");
            Ensure.NotNull(messageArrivedHandler, "messageArrivedHandler");
            Ensure.NotNull(connectionClosedHandler, "connectionClosedHandler");

            _socket = socket;
            _bufferPool = bufferPool;
            _localEndPoint = socket.LocalEndPoint;
            _remotingEndPoint = socket.RemoteEndPoint;
            _messageArrivedHandler = messageArrivedHandler;
            _connectionClosedHandler = connectionClosedHandler;

            _sendSocketArgsStack = new ConcurrentStack<SocketAsyncEventArgs>();

            _scheduleService = ObjectContainer.Resolve<IScheduleService>();

            for (var i = 0; i < 2; i++)
            {
                var sendSocketArgs = new SocketAsyncEventArgs();
                sendSocketArgs.AcceptSocket = socket;
                sendSocketArgs.Completed += OnSendAsyncCompleted;
                _sendSocketArgsStack.Push(sendSocketArgs);
            }

            _receiveSocketArgs = new SocketAsyncEventArgs();
            _receiveSocketArgs.AcceptSocket = socket;
            _receiveSocketArgs.Completed += OnReceiveAsyncCompleted;

            _logger = ObjectContainer.Resolve<ILoggerFactory>().Create(GetType().FullName);

            _framer = new LengthPrefixMessageFramer();
            _framer.RegisterMessageArrivedCallback(OnMessageArrived);

            TryReceive();
            TrySend();
        }

        public void Send(byte[] message)
        {
            var segments = _framer.FrameData(new ArraySegment<byte>(message, 0, message.Length));
            lock (_sendingLock)
            {
                foreach (var segment in segments)
                {
                    _sendingQueue.Enqueue(segment);
                    Interlocked.Increment(ref _segmentCount);
                }
            }
            TrySend();

            if (_segmentCount >= 50000)
            {
                Thread.Sleep(5);
                var count = Interlocked.Increment(ref _flowControlCount);
                if (count % 1000 == 0)
                {
                    _logger.InfoFormat("Send message flow control count: {0}", count);
                }
            }
        }
        public void TryReceive()
        {
            if (!EnterReceiving()) return;

            var buffer = _bufferPool.Get();
            if (buffer == null)
            {
                CloseInternal(SocketError.Shutdown, "Socket receive allocate buffer failed.");
                ExitReceiving();
                return;
            }

            _receiveSocketArgs.SetBuffer(buffer, 0, buffer.Length);
            if (_receiveSocketArgs.Buffer == null)
            {
                CloseInternal(SocketError.Shutdown, "Socket receive set buffer failed.");
                ExitReceiving();
                return;
            }

            try
            {
                bool firedAsync = _receiveSocketArgs.AcceptSocket.ReceiveAsync(_receiveSocketArgs);
                if (!firedAsync)
                {
                    ProcessReceive(_receiveSocketArgs);
                }
            }
            catch (Exception ex)
            {
                ReturnReceivingSocketBuffer();
                CloseInternal(SocketError.Shutdown, "Socket receive error, errorMessage:" + ex.Message);
                ExitReceiving();
            }
        }
        public void Close()
        {
            CloseInternal(SocketError.Success, "Socket normal close.");
        }

        private void TrySend()
        {
            if (!EnterSending()) return;

            _sendingStream.SetLength(0);

            ArraySegment<byte> sendPiece;
            while (_sendingQueue.TryDequeue(out sendPiece))
            {
                Interlocked.Decrement(ref _segmentCount);
                _sendingStream.Write(sendPiece.Array, sendPiece.Offset, sendPiece.Count);
                if (_sendingStream.Length >= MaxSendPacketSize)
                {
                    break;
                }
            }

            if (_sendingStream.Length == 0)
            {
                ExitSending();
                if (_sendingQueue.Count > 0)
                {
                    TrySend();
                }
                return;
            }

            try
            {
                var sendSocktArgs = GetSendSocketEventArgs();
                sendSocktArgs.SetBuffer(_sendingStream.GetBuffer(), 0, (int)_sendingStream.Length);
                var firedAsync = sendSocktArgs.AcceptSocket.SendAsync(sendSocktArgs);
                if (!firedAsync)
                {
                    ProcessSend(sendSocktArgs);
                }
            }
            catch (Exception ex)
            {
                CloseInternal(SocketError.Shutdown, "Socket send error, errorMessage:" + ex.Message);
                ExitSending();
            }
        }
        private void OnSendAsyncCompleted(object sender, SocketAsyncEventArgs e)
        {
            ProcessSend(e);
        }
        private void ProcessSend(SocketAsyncEventArgs socketArgs)
        {
            if (socketArgs.Buffer != null)
            {
                socketArgs.SetBuffer(null, 0, 0);
            }
            ReturnSendSocketEventArgs(socketArgs);
            ExitSending();

            if (socketArgs.SocketError == SocketError.Success)
            {
                TrySend();
            }
            else
            {
                CloseInternal(socketArgs.SocketError, "Socket send error.");
            }
        }

        private void OnReceiveAsyncCompleted(object sender, SocketAsyncEventArgs e)
        {
            ProcessReceive(e);
        }
        private void ProcessReceive(SocketAsyncEventArgs socketArgs)
        {
            if (socketArgs.BytesTransferred == 0 || socketArgs.SocketError != SocketError.Success)
            {
                ReturnReceivingSocketBuffer();
                CloseInternal(socketArgs.SocketError, socketArgs.SocketError != SocketError.Success ? "Socket receive error" : "Socket normal close");
                return;
            }

            lock (_receivingLock)
            {
                var segment = new ArraySegment<byte>(socketArgs.Buffer, socketArgs.Offset, socketArgs.Count);
                _receiveQueue.Enqueue(new ReceivedData(segment, socketArgs.BytesTransferred));
                socketArgs.SetBuffer(null, 0, 0);
            }

            TryParsingReceivedData();
            ExitReceiving();
            TryReceive();
        }

        private void TryParsingReceivedData()
        {
            if (!EnterParsing()) return;

            try
            {
                var dataList = new List<ReceivedData>(_receiveQueue.Count);
                var segmentList = new List<ArraySegment<byte>>();

                ReceivedData data;
                while (_receiveQueue.TryDequeue(out data))
                {
                    dataList.Add(data);
                    segmentList.Add(new ArraySegment<byte>(data.Buf.Array, data.Buf.Offset, data.DataLen));
                }

                _framer.UnFrameData(segmentList);

                for (int i = 0, n = dataList.Count; i < n; ++i)
                {
                    _bufferPool.Return(dataList[i].Buf.Array);
                }
            }
            catch (Exception ex)
            {
                _logger.Error("Parsing received data error.", ex);
            }
            finally
            {
                ExitParsing();
            }
        }
        private void OnMessageArrived(ArraySegment<byte> messageSegment)
        {
            byte[] message = new byte[messageSegment.Count];
            Array.Copy(messageSegment.Array, messageSegment.Offset, message, 0, messageSegment.Count);
            try
            {
                _messageArrivedHandler(this, message);
            }
            catch (Exception ex)
            {
                _logger.Error("Call message arrived handler failed.", ex);
            }
        }
        private void ReturnReceivingSocketBuffer()
        {
            try
            {
                if (_receiveSocketArgs.Buffer != null)
                {
                    _bufferPool.Return(_receiveSocketArgs.Buffer);
                    _receiveSocketArgs.SetBuffer(null, 0, 0);
                }
            }
            catch (Exception ex)
            {
                _logger.Error("Return receiving socket event buffer failed.", ex);
            }
        }

        private void CloseInternal(SocketError socketError, string reason)
        {
            SocketUtils.ShutdownSocket(_socket);
            _logger.InfoFormat("Socket closed, remote endpoint:{0} socketError:{1}, reason:{2}", RemotingEndPoint, socketError, reason);

            if (_connectionClosedHandler != null)
            {
                try
                {
                    _connectionClosedHandler(this, socketError);
                }
                catch (Exception ex)
                {
                    _logger.Error("Call connection closed handler failed.", ex);
                }
            }
        }

        private SocketAsyncEventArgs GetSendSocketEventArgs()
        {
            SocketAsyncEventArgs args;
            if (_sendSocketArgsStack.TryPop(out args))
            {
                return args;
            }

            var spinWait = new SpinWait();
            while (!_sendSocketArgsStack.TryPop(out args))
            {
                spinWait.SpinOnce();
            }
            return args;
        }
        private void ReturnSendSocketEventArgs(SocketAsyncEventArgs args)
        {
            _sendSocketArgsStack.Push(args);
        }
        private bool EnterSending()
        {
            return Interlocked.CompareExchange(ref _sending, 1, 0) == 0;
        }
        private void ExitSending()
        {
            Interlocked.Exchange(ref _sending, 0);
        }
        private bool EnterReceiving()
        {
            return Interlocked.CompareExchange(ref _receiving, 1, 0) == 0;
        }
        private void ExitReceiving()
        {
            Interlocked.Exchange(ref _receiving, 0);
        }
        private bool EnterParsing()
        {
            return Interlocked.CompareExchange(ref _parsing, 1, 0) == 0;
        }
        private void ExitParsing()
        {
            Interlocked.Exchange(ref _parsing, 0);
        }

        struct ReceivedData
        {
            public readonly ArraySegment<byte> Buf;
            public readonly int DataLen;

            public ReceivedData(ArraySegment<byte> buf, int dataLen)
            {
                Buf = buf;
                DataLen = dataLen;
            }
        }
    }
}
