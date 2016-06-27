using System;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;

namespace ChaosRpc.Transport
{
	public class TcpClient
	{
		public event Action<TcpSession> OnSessionConnected;
		public event Action<Exception> OnConnectionError;

		public bool NoDelay { get; set; }
		private bool _secureConnect;
		private Socket _socket;
		private TcpSession _session;
		private SslStream _sslStream;

		private readonly X509CertificateCollection _certificates;
		private readonly RemoteCertificateValidationCallback _certValidationCallback;

		public TcpClient()
		{
		}

		public TcpClient(X509CertificateCollection certificates, 
			RemoteCertificateValidationCallback certValidationCallback)
		{
			_certificates = certificates;
			_certValidationCallback = certValidationCallback;
		}

		public void BeginConnect(string address, int port, bool useSsl)
		{
			if (_session != null) {
				_session.Close();
				_session = null;
				_sslStream = null;
				_socket = null;
			}

			_secureConnect = useSsl;
			_socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
			_socket.NoDelay = NoDelay;
			_socket.BeginConnect(address, port, ProcessConnect, this);
		}

		public void SendMessage(byte[] message)
		{
			_session.WriteMessage(message);
		}

		public void Close()
		{
			if (_session != null) {
				_session.Close();
			} else {
				if (_sslStream != null)
					_sslStream.Close();

				if (_socket != null)
					_socket.Close();
			}

			_session = null;
			_socket = null;
			_sslStream = null;
		}

		public void Close(string reason)
		{
			if (_session != null)
				_session.Close(reason);

			_session = null;
			_socket = null;
			_sslStream = null;
		}

		private void ProcessConnect(IAsyncResult ar)
		{
			try {
				_socket.EndConnect(ar);

				if (_secureConnect) {
					string targetAddress = ((IPEndPoint) _socket.RemoteEndPoint).Address.ToString();

					if (_certificates != null) {
						_sslStream = new SslStream(new NetworkStream(_socket, true), false, _certValidationCallback);

						_sslStream.BeginAuthenticateAsClient(targetAddress,
							_certificates, SslProtocols.Tls, false, OnSslAuthenticated, this);
					} else {
						_sslStream = new SslStream(new NetworkStream(_socket, true), false, AcceptServerCertificate);

						_sslStream.BeginAuthenticateAsClient(targetAddress, 
							null, SslProtocols.Tls, false, OnSslAuthenticated, this);
					}
				} else {
					_session = new TcpSession(_socket, new NetworkStream(_socket, true));

					try {
						if (OnSessionConnected != null)
							OnSessionConnected(_session);
					} catch (Exception) {
						// ignored
					}

					_session.Start();
				}
			} catch (Exception e) {
				try {
					if (OnConnectionError != null)
						OnConnectionError(e);
				} catch (Exception) {
					// ignored
				}

				Close();
			}
		}

		private void OnSslAuthenticated(IAsyncResult ar)
		{
			try {
				_sslStream.EndAuthenticateAsClient(ar);
				_session = new TcpSession(_socket, _sslStream);

				if (OnSessionConnected != null)
					OnSessionConnected(_session);

				_session.Start();
			} catch (Exception e) {
				try {
					if (OnConnectionError != null)
						OnConnectionError(e);
				} catch (Exception) {
					// ignored
				}

				Close();
			}
		}
		
		private bool AcceptServerCertificate(object sender, X509Certificate certificate, X509Chain chain, SslPolicyErrors sslpolicyerrors)
		{
			return true;
		}
	}
}
