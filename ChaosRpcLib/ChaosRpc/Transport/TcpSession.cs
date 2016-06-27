using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using System.Text;

namespace ChaosRpc.Transport
{
	public class TcpSession
	{
		// This is called when a message is received from the other side of the stream.
		public event Action<byte[]> OnMessageReceived;

		// This is called when the session is closed. The second parameter is the reason.
		public event Action<Exception, string> OnSessionClosed;

		// This is called if any of the other event handlers throw an exception.
		// public event Action<Exception> OnEventHandlerError;
		
		// Gives access to the underlying socket if you want to set some parameters on it.
		public Socket Socket { get { return _socket; } }

		private enum MessageType
		{
			DataMessage = 1,
			CloseConnectionMessage,
		}
		
		private struct Message
		{
			public MessageType Type;
			public byte[] Data;
			public string DataAsUtf8String { get { return Encoding.UTF8.GetString(Data); } }
		}

		private const int MessageHeaderLength = 3;

		private readonly Socket _socket;
		private readonly Stream _stream;
		private readonly byte[] _readBuffer = new byte[512];
		private readonly MemoryStream _currentMessage = new MemoryStream();
		private int _currentMessageBytesLeft;

		private readonly Queue<Message> _inputQueue = new Queue<Message>();
		private bool _processingMessage;
		private readonly Queue<Message> _outputQueue = new Queue<Message>();
		private bool _writingMessage;
		private Message _messageBeingWritten;

		private bool _syncRead;
		private bool _syncWrite;

		private volatile bool _closed;
		private string _closingReason = string.Empty;
		private bool Closing { get { return !string.IsNullOrEmpty(_closingReason); } }
		private volatile bool _inBeginReadCall;
		private volatile bool _inBeginWriteCall;

		public TcpSession(Socket socket, Stream stream)
		{
			_socket = socket;
			_stream = stream;
		}

		public void Start()
		{
			BeginRead();
		}

		public void WriteMessage(byte[] message)
		{
			BeginWrite(message, closeMessage: false);
		}

		public void Close()
		{
			ProcessClose(exception: null, notify: true);
		}

		public void Close(string reason)
		{
			if (_closed || Closing)
				return;

			byte[] reasonBytes = Encoding.UTF8.GetBytes(reason);
			BeginWrite(reasonBytes, closeMessage: true);
		}

		private void BeginWrite(byte[] data, bool closeMessage)
		{
			if (_closed || Closing)
				return;

			if (closeMessage)
				_closingReason = Encoding.UTF8.GetString(data);

			lock (_outputQueue) {
				var message = new Message
				{
					Type = closeMessage ? MessageType.CloseConnectionMessage : MessageType.DataMessage,
					Data = data,
				};

				if (_writingMessage) {
					_outputQueue.Enqueue(message);
				} else {
					_writingMessage = true;
					_messageBeingWritten = message;

					try {
						byte[] dataWithHeader = PrefixHeader(message);
						_stream.BeginWrite(dataWithHeader, 0, dataWithHeader.Length, OnWriteComplete, this);
					} catch (ObjectDisposedException) {
						// The socket was closed locally
						return;
					} catch (IOException e) {
						ProcessClose(exception: e, notify: true);
					}
				}
			}
		}

		private void ProcessClose(Exception exception, bool notify)
		{
			if (_closed)
				return;

			_closed = true;

			if (notify) {
				try {
					if (OnSessionClosed != null)
						OnSessionClosed(exception, exception != null ? exception.Message : _closingReason);
				} catch (Exception) {
					// Ignored
				}
			}

			_stream.Close();

			lock (_inputQueue) {
				_inputQueue.Clear();
			}

			lock (_outputQueue) {
				_outputQueue.Clear();
			}
		}

		private void BeginRead()
		{
			try {
				do {
					_syncRead = false;
					_inBeginReadCall = true;
					_stream.BeginRead(_readBuffer, 0, _readBuffer.Length, OnReadComplete, this);
					_inBeginReadCall = false;
				} while (_syncRead);
			} catch (ObjectDisposedException) {
				// The socket was closed locally
				return;
			} catch (Exception e) {
				ProcessClose(exception: e, notify: true);
			}
		}

		private void OnReadComplete(IAsyncResult ar)
		{
			int bytesRead;

			try {
				bytesRead = _stream.EndRead(ar);
			} catch (ObjectDisposedException) {
				// The socket was closed locally
				return;
			} catch (Exception e) {
				ProcessClose(exception: e, notify: true);
				return;
			}

			if (bytesRead == 0) {
				ProcessClose(exception: null, notify: true);	
			} else {
				int bufferPos = 0;
				bool closingMessage = false;

				while (bufferPos < bytesRead) {
					int bytesLeft;

					if (_currentMessage.Length < MessageHeaderLength) {
						// We are still reading the header
						int headerBytesLeft = MessageHeaderLength - (int) _currentMessage.Length;
						bytesLeft = bytesRead - bufferPos;

						if (bytesLeft < headerBytesLeft) {
							_currentMessage.Write(_readBuffer, bufferPos, bytesLeft);
							break;
						}

						_currentMessage.Write(_readBuffer, bufferPos, headerBytesLeft);
						bufferPos += headerBytesLeft;
						OnHeaderRead(out closingMessage);
					}

					bytesLeft = bytesRead - bufferPos;

					if (_currentMessageBytesLeft <= bytesLeft) {
						_currentMessage.Write(_readBuffer, bufferPos, _currentMessageBytesLeft);
						bufferPos += _currentMessageBytesLeft;

						byte[] messageBuf = _currentMessage.GetBuffer();
						var data = new byte[_currentMessage.Length - MessageHeaderLength];
						Array.Copy(messageBuf, MessageHeaderLength, data, 0, data.Length);

						if (closingMessage)
							_closingReason = Encoding.UTF8.GetString(data);

						lock (_inputQueue) {
							var message = new Message
							{
								Type = closingMessage ? MessageType.CloseConnectionMessage : MessageType.DataMessage,
								Data = data,
							};

							if (_processingMessage) {
								_inputQueue.Enqueue(message);
							} else if (OnMessageReceived != null) {
								_processingMessage = true;
								Action<Message> processMessage = ProcessMessage;

								processMessage.BeginInvoke(message, res => {
									processMessage.EndInvoke(res);
								}, null);
							}
						}

						_currentMessage.SetLength(0);
						_currentMessageBytesLeft = 0;
					} else {
						_currentMessage.Write(_readBuffer, bufferPos, bytesLeft);
						bufferPos += bytesLeft;
						_currentMessageBytesLeft -= bytesLeft;
					}
				}

				if (_inBeginReadCall) {
					_syncRead = true;
				} else {
					BeginRead();
				}
			}
		}

		private byte[] PrefixHeader(Message message)
		{
			byte[] data = message.Data;

			if (data.Length > (1 << 23))
				throw new ArgumentOutOfRangeException("message", data.Length, "The maximum message length is " + (1 << 15));

			byte[] messageWithHeader = new byte[data.Length + MessageHeaderLength];
			int length = data.Length;
			Array.Copy(data, 0, messageWithHeader, MessageHeaderLength, data.Length);
			messageWithHeader[0] = (byte) ((length & 0x00FF0000) >> 16);
			messageWithHeader[1] = (byte) ((length & 0x0000FF00) >> 8);
			messageWithHeader[2] = (byte) (length & 0x000000FF);

			if (message.Type == MessageType.CloseConnectionMessage)
				messageWithHeader[0] |= 1 << 7;

			return messageWithHeader;
		}

		private void OnHeaderRead(out bool closingMessage)
		{
			byte[] messageBuffer = _currentMessage.GetBuffer();
			closingMessage = (messageBuffer[0] & (1 << 7)) != 0;
			messageBuffer[0] &= 127; // 0b01111111
			_currentMessageBytesLeft = (messageBuffer[0] << 16) | (messageBuffer[1] << 8) | messageBuffer[2];
		}

		private void OnWriteComplete(IAsyncResult ar)
		{
			try {
				_stream.EndWrite(ar);
			} catch (ObjectDisposedException) {
				// The socket was closed locally
				return;
			} catch (Exception e) {
				ProcessClose(exception: e, notify: true);
				return;
			}

			if (_messageBeingWritten.Type == MessageType.CloseConnectionMessage) {
				ProcessClose(exception: null, notify: true);
				return;
			}

			if (_inBeginWriteCall) {
				_syncWrite = true;
				return;
			}

			lock (_outputQueue) {
				if (_outputQueue.Count > 0) {
					try {
						do {
							Message message = _outputQueue.Dequeue();
							// This overly complicated tracking if the write completes
							// synchronously is here because SslStream.BeginWrite
							// always says that it completed synchronously event if it
							// didn't
							_syncWrite = false;
							_inBeginWriteCall = true;
							_messageBeingWritten = message;
							byte[] dataWithHeader = PrefixHeader(message);
							_stream.BeginWrite(dataWithHeader, 0, dataWithHeader.Length, OnWriteComplete, this);
							_inBeginWriteCall = false;
						} while (_syncWrite && _outputQueue.Count > 0);

						// The last write was synchronous and we don't have more messages
						// so clear the writingMessage flag
						if (_syncWrite && _outputQueue.Count == 0)
							_writingMessage = false;
					} catch (ObjectDisposedException) {
						// The socket was closed locally
						return;
					} catch (IOException e) {
						ProcessClose(exception: e, notify: true);
					}
				} else {
					_writingMessage = false;
				}
			}
		}

		private void ProcessMessage(Message message)
		{
			if (_closed)
				return;

			try {
				if (message.Type == MessageType.DataMessage && OnMessageReceived != null)
					OnMessageReceived(message.Data);
			} catch (Exception e) {
				ProcessClose(exception: e, notify: true);
				return;
			}

			if (message.Type == MessageType.CloseConnectionMessage) {
				ProcessClose(exception: null, notify: true);
				return;
			}

			lock (_inputQueue) {
				if (_inputQueue.Count > 0) {
					Message newMessage = _inputQueue.Dequeue();
					Action<Message> processMessage = ProcessMessage;

					processMessage.BeginInvoke(newMessage, res => {
						processMessage.EndInvoke(res);
					}, null);
				} else {
					_processingMessage = false;
				}
			}
		}
	}
}
