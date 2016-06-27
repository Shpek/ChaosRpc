using System;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;

namespace ChaosRpc.Transport
{
	public class TcpServer
	{
		public event Action<TcpSession> OnClientConnected;
		public event Action<Exception> OnError;

		public bool SecureServer { get; set; }
		public bool NoDealy { get; set; }
		public X509Certificate ServerCertificate { get; set; }
		public bool ClientCertificateReqired { get; set; }
		public RemoteCertificateValidationCallback CertificateValidationCallback {get; set; }

		private class SslConnection
		{
			public Socket Socket;
			public SslStream SslStream;
		}

		public void Start(IPAddress address, int port)
		{
			if (SecureServer && ServerCertificate == null)
				throw new InvalidOperationException("Secure server requires server certificate");

			var listener = new TcpListener(address, port);
			listener.Server.NoDelay = NoDealy;

			try {
				listener.Start(1500);
				listener.BeginAcceptSocket(OnClientAccepted, listener);
			} catch (Exception e) {
				if (OnError != null)
					OnError(e);

				listener.Stop();
			}
		}

		private void OnClientAccepted(IAsyncResult ar)
		{
			var listener = (TcpListener) ar.AsyncState;
			Socket socket = null;
			TcpSession session = null;
			SslConnection sslConnection = null;

			try {
				socket = listener.EndAcceptSocket(ar);

				if (SecureServer) {
					sslConnection = new SslConnection
					{
						Socket = socket,
					};

					sslConnection.SslStream = new SslStream(
						new NetworkStream(socket, true), false, CertificateValidationCallback);

					sslConnection.SslStream.BeginAuthenticateAsServer(
						ServerCertificate, ClientCertificateReqired,
						SslProtocols.Tls, true, OnClientAutenticated, 
						sslConnection);
				} else {
					session = new TcpSession(socket, new NetworkStream(socket, true));

					try {
						if (OnClientConnected != null)
							OnClientConnected(session);
					} catch (Exception e) {
						Console.WriteLine(e);
						// ignored
					}

					session.Start();
				}
			} catch (Exception e) {
				try {
					if (OnError != null)
						OnError(e);
				} catch (Exception) {
					// ignored
				}

				if (session != null) {
					session.Close();
				} else if (sslConnection != null) {
					if (sslConnection.SslStream != null)
						sslConnection.SslStream.Close();
					else {
						sslConnection.Socket.Close();
					}
				} else if (socket != null) {
					socket.Close();
				}
			}

			listener.BeginAcceptTcpClient(OnClientAccepted, listener);
		}

		private void OnClientAutenticated(IAsyncResult ar)
		{
			var sslConnection = (SslConnection) ar.AsyncState;
			TcpSession session = null;

			try {
				sslConnection.SslStream.EndAuthenticateAsServer(ar);
				session = new TcpSession(sslConnection.Socket, sslConnection.SslStream);

				try {
					if (OnClientConnected != null)
						OnClientConnected(session);
				} catch (Exception) {
					// ignored
				}

				session.Start();
			} catch (Exception e) {
				try {
					if (OnError != null)
						OnError(e);
				} catch (Exception) {
					// ignored
				}

				if (session != null) {
					session.Close();
				} else {
					sslConnection.SslStream.Close();
					sslConnection.Socket.Close();
				}
			}
		}
	}
}
