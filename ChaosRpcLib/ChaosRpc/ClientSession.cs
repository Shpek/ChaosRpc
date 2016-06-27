using System;

namespace ChaosRpc
{
	public class ClientSession : IClientSession
	{
		public event DataCallback OnDataOut;
		public event Action<object> OnDisconnect;

		public bool Disconnected { get; private set; }
		public DateTime LasActivityTime { get; private set; }
		public RpcEndpoint Endpoint { get { return _endpoint; } }
		private readonly RpcEndpoint _endpoint;

		protected ClientSession()
		{
			_endpoint = new RpcEndpoint();
			_endpoint.OnDataOut += SendDataToClient;
			LasActivityTime = DateTime.Now;
		}

		public T GetRpcInterface<T>() where T: class
		{
			return _endpoint.GetRpcInterface<T>();
		}

		public void Disconnect(object reason = null)
		{
			Disconnected = true;

			if (OnDisconnect != null)
				OnDisconnect(reason);
		}

		public void SendDataToClient(byte[] data, int offset, int length)
		{
			if (OnDataOut != null)
				OnDataOut(data, offset, length);

			LasActivityTime = DateTime.Now;
		}

		public void ReceiveData(byte[] data, int offset, int length)
		{
			LasActivityTime = DateTime.Now;
			Endpoint.ReceiveData(data, offset, length, this);
		}
	}
}
