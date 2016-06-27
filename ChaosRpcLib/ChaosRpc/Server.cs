using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;

namespace ChaosRpc
{
	public interface IClientSession
	{
		RpcEndpoint Endpoint { get; }
		void SendDataToClient(byte[] data, int offset, int length);
		void ReceiveData(byte[] data, int offset, int length);
	}

	public interface IClientSessionOwner<TSession>
	{
		bool Authenticate(MethodInfo method);
		TSession Session { get; set; }
	}

	public class RpcCallInfo
	{
		public String MethondName;
		public int NumCalls;
		public long TotalTimeUs;
		public long MeanTimeUs { get { return (long) (TotalTimeUs / (double) NumCalls); } }
		public long MaxTimeUs = long.MinValue;
		public long MinTimeUs = long.MaxValue;
	}

	public class Server<TSession> where TSession: IClientSession
	{
		public bool ProfileMethodCalls;

		private readonly List<object> _handlers = new List<object>(); 
		private readonly Dictionary<string, RpcCallInfo> _methodNameToCallInfo = new Dictionary<string, RpcCallInfo>();

		[ThreadStatic] 
		private static Stopwatch _callTimeCounter;

		public void RegisterSession(TSession session)
		{
			foreach (var handler in _handlers)
				session.Endpoint.RegisterRpcHandler(handler);

			session.Endpoint.OnBeforeHandlerCall += OnSessionBeforeHandlerCall;
			session.Endpoint.OnAfterHandlerCall += OnSessionAfterHandlerCall;
		}

		/// <summary>
		/// Registers RPC handler into this server. This method is NOT THREAD SAFE. You should
		/// register your handlers when starting the server or do your own synchronization.
		/// </summary>
		/// <param name="handler">The object that implements one or more RPCInterfaces</param>
		public void RegisterRpcHandler(object handler)
		{
			_handlers.Add(handler);			
		}

		/// <summary>
		/// Receives a data frame from a client and passes it to the RPC server endpoint. This
		/// can be called safely from multiple threads since it doesn't modify any state.
		/// </summary>
		/// <param name="session">The session object of your server. This will be set in the handler if it implements IClientSessionOwner.</param>
		/// <param name="data">The array with the data from the client.</param>
		/// <param name="offset">The offset in data from which the actual data starts.</param>
		/// <param name="length">The length of the data</param>
		public void ReceiveData(TSession session, byte[] data, int offset, int length)
		{
			session.ReceiveData(data, offset, length);
		}

		/// <summary>
		/// Clears the statistics for RPC method calls
		/// </summary>
		public void ClearCallStats()
		{
			lock (_methodNameToCallInfo) {
				_methodNameToCallInfo.Clear();
			}
		}

		/// <summary>
		/// Returns the statistics for RPC method calls
		/// </summary>
		/// <returns>An array of RpcCallInfo structures each of which contains stats for a single RPC method.</returns>
		public RpcCallInfo[] GetCallStats()
		{
			lock (_methodNameToCallInfo) {
				return _methodNameToCallInfo.Values.ToArray();
			}
		}

		private void OnSessionBeforeHandlerCall(HandlerCallContext callContext)
		{
			// If we profile the RPC handler calls start a stopwatch for the calling thread.
			if (ProfileMethodCalls) {
				if (_callTimeCounter == null)
					_callTimeCounter = new Stopwatch();

				_callTimeCounter.Stop();
				_callTimeCounter.Start();
			}

			var clientSessionOwner = callContext.Handler as IClientSessionOwner<TSession>;

			if (clientSessionOwner != null) {
				TSession session = (TSession) callContext.Context;
				clientSessionOwner.Session = session;

				if (!clientSessionOwner.Authenticate(callContext.Method)) {
					if (_callTimeCounter != null && _callTimeCounter.IsRunning)
						_callTimeCounter.Stop();

					throw new InvalidOperationException("RPC method call " + callContext.Handler.GetType() + "." +
					                                    callContext.Method.Name + " was not authenticated. The session is: " + session);
				}
			}
		}

		private void OnSessionAfterHandlerCall(HandlerCallContext callContext)
		{
			var clientSessionOwner = callContext.Handler as IClientSessionOwner<TSession>;

			if (clientSessionOwner != null) {
				clientSessionOwner.Session = default(TSession);
			}

			// If we profile the RPC handler calls calculate and store the data for this call.
			if (_callTimeCounter != null && _callTimeCounter.IsRunning) {
				long elapsedTicks = _callTimeCounter.ElapsedTicks;
				_callTimeCounter.Stop();
				string methodName = callContext.HandlerType.Name + "." + callContext.Method.Name;

				lock (_methodNameToCallInfo) {
					RpcCallInfo info;

					if (!_methodNameToCallInfo.TryGetValue(methodName, out info)) {
						info = _methodNameToCallInfo[methodName] = new RpcCallInfo
						{
							MethondName = methodName,
						};
					}

					++ info.NumCalls;
					long us = (long) ((elapsedTicks / (double) Stopwatch.Frequency) * 1000000);
					info.TotalTimeUs += us;

					if (us > info.MaxTimeUs)
						info.MaxTimeUs = us;

					if (us < info.MinTimeUs)
						info.MinTimeUs = us;
				}
			}
		}
	}

	/// <summary>
	/// Helper class that can be used to construct RPC services. If your RPC handler
	/// inherits this class when a RPC method is called on it you will have the session
	/// object of the client that is calling the method set as the Session member. You
	/// can use the session to send a response to the client. Your service is thread safe
	/// since the Session member is a thread local variable.
	/// </summary>
	/// <typeparam name="TSession">The type of the client session object that your server uses.</typeparam>
	public abstract class ServiceHelper<TSession> : IClientSessionOwner<TSession>
	{
		// This is checked before each call (Session is already set when this is called). 
		// If this returns false - an exception is thrown and the client will be disconnected.
		public virtual bool Authenticate(MethodInfo method)
		{
			return true;
		}

		// The session is set before each call that is made to this service to the
		// session of the calling client
		public TSession Session
		{
			get
			{
				TSession session = _clientSession.Value;

				if (session == null)
					throw new InvalidOperationException("Client session is not set - this probably means that a RPC method that needs to respond to its client is called directly on the handler.");

				return session; 
			}

			set { _clientSession.Value = value; }
		}

		private readonly ThreadLocal<TSession> _clientSession = new ThreadLocal<TSession>();
	}
}
