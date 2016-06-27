using System;
using System.Diagnostics;
using System.Net;
using System.Text;

namespace ChaosRpc.Transport
{
	class Program
	{
		private static void Main(string[] args)
		{
			var server = new TcpServer
			{
				NoDealy = true,
			};

			server.OnClientConnected += session => {
				session.OnMessageReceived += bytes => {
					session.WriteMessage(bytes);

					//new Task(() => {
					//	Thread.Sleep(3000);
					//	session.Close("Tralala");	
					//}).Start();
				};

				session.OnSessionClosed += (ex, reason) => {
					Console.WriteLine("--- server closed reason: " + reason);
				};
			};

			server.Start(IPAddress.Any, 9999);
			Console.Read();

			for (int i = 0; i < 4; ++i) {
				var client = new TcpClient
				{
					NoDelay = true,
				};

				var stopwatch = new Stopwatch();

				client.OnSessionConnected += session => {
					bool closed = false;
					stopwatch.Start();
					session.WriteMessage(Encoding.UTF8.GetBytes("Ping"));

					session.OnMessageReceived += bytes => {
						Console.WriteLine(stopwatch.ElapsedMilliseconds);
					};

					session.OnSessionClosed += (ex, reason) => {
						if (closed) {
							Console.WriteLine("--- closed");
						}

						Console.WriteLine("--- client closed reason: " + reason);
						closed = true;
					};
				};

				client.BeginConnect("192.168.0.102", 9999, false);
			}

			//var sessions = new List<TcpSession>();

			//server.OnClientConnected += session => {
			//	sessions.Add(session);
			//	Console.WriteLine("Server connected");

			//	session.OnMessageReceived += bytes => {
			//		string message = Encoding.UTF8.GetString(bytes);
			//		Console.WriteLine("Server: " + message);
			//		//session.WriteMessage(Encoding.UTF8.GetBytes("Pong"));
			//		//session.Close("Tralala");
			//	};

			//	session.OnSessionClosed += (ex, reason) => {
			//		Console.WriteLine("Server session closed, reason: " + reason);
			//	};
			//};

			//server.Start(IPAddress.Any, 9999);
			//var clients = new List<TcpClient>();

			// for (int i = 0; i < 500; ++i) {
			//	clients.Add(RunClient(i));
			//}

			//Console.ReadKey();

			//foreach (var client in clients) {
			//	client.Close();
			//}

			Console.Read();
		}

		static TcpClient RunClient(int idx)
		{
			var client = new TcpClient
			{
				NoDelay = true,
			};

			client.OnSessionConnected += session => {
				session.OnMessageReceived += bytes => {
					string message = Encoding.UTF8.GetString(bytes);
					Console.WriteLine("Client " + idx + ": " + message);
				};

				session.OnSessionClosed += (ex, reason) => {
					Console.WriteLine("Client " + idx + " session closed, reason: " + reason);
				};

				Console.WriteLine("Client " + idx + " connected");
				client.SendMessage(Encoding.UTF8.GetBytes("Ping " + idx));
			};

			client.OnConnectionError += ex => {
				Console.WriteLine("Client " + idx + " connection error: " + ex);
			};

			client.BeginConnect("192.168.0.103", 9999, useSsl: false);
			return client;
		}
	}
}
