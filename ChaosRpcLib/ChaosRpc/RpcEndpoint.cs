using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Reflection;

// SGG Stuff left to do:
// - Make the future to serialize the result at the moment of creation
// so you can return results from inside of locks
// - Add bool compacting for the binary serializer (use a bit for each boolean)
// - Add default values compacting for the binary serializer (write only the non default fields)
namespace ChaosRpc
{
	public struct HandlerCallContext
	{
		public object Context;
		public Type HandlerType;
		public object Handler;
		public MethodInfo Method;
		public object Result;
		public ushort CallId;
	}

	public enum MessageType
	{
		HandlerCall,
		CallResult,
	}

	public struct MessageInfo
	{
		public MessageType Type;
		public Type InterfaceType;
		public MethodInfo InterfaceMethodInfo;
		public object Handler;
	}

	public class RpcEndpoint : ICallInterceptor
	{
		private struct HandlerData
		{
			public Type RpcInterfaceType;
			public object Handler;
			public RpcMethodInfo[] InterfaceMethods;
		}

		private class ClientData
		{
			public Object Proxy;
			public int InterfaceOrdinal;
			public RpcMethodInfo[] InterfaceMethods;
		}

		private const byte ResponseTypeFlag = 1 << 7;

		public event Action<byte[], int, int> OnDataOut;
		public event Action<HandlerCallContext> OnBeforeHandlerCall;
		public event Action<HandlerCallContext> OnAfterHandlerCall;

		private readonly Dictionary<Type, ClientData> _clientsData = new Dictionary<Type, ClientData>();
		private readonly Dictionary<byte, HandlerData> _handlersData = new Dictionary<byte, HandlerData>();
		private readonly Dictionary<int, object> _resultFutures = new Dictionary<int, object>();
		private readonly List<object[]> _argumentArrays = new List<object[]>(); 
		private byte _nextCallId;

		public T GetRpcInterface<T>() where T : class
		{
			ClientData clientData;

			if (_clientsData.TryGetValue(typeof (T), out clientData)) {
				return (T) clientData.Proxy;
			}

			var type = typeof (T);

			if (type.HasAttribute<RpcInterface>()) {
				var newClientData = new ClientData()
				{
					InterfaceOrdinal = type.GetAttribute<RpcInterface>().Ordinal,
					InterfaceMethods = RpcInterface.GetOrderedMethods(type),
				};

				newClientData.Proxy = RpcInterface.CreateDynamicProxy<T>(this, newClientData);
				_clientsData[typeof (T)] = newClientData;
				return (T) newClientData.Proxy;
			}

			throw new NotSupportedException(type.Name + " does not have the RPCInterface attribute");
		}

		public void RegisterRpcHandler(object handler)
		{
			Type handlerType = handler.GetType();
			bool handlerRegistered = false;

			foreach (var intType in handlerType.GetInterfaces()) {
				if (intType.HasAttribute<RpcInterface>()) {
					var iface = intType.GetAttribute<RpcInterface>();
					HandlerData existingHandler;

					if (_handlersData.TryGetValue(iface.Ordinal, out existingHandler)) {
						RemoveRpcHandler(handler);
						throw new ArgumentException("Handler with type " + handler.GetType() + " implements " + iface.GetType() +
						                            " which is already handled by " + existingHandler.Handler.GetType());
					}

					_handlersData[iface.Ordinal] = new HandlerData
					{
						RpcInterfaceType = intType,
						Handler = handler,
						InterfaceMethods = RpcInterface.GetOrderedMethods(intType),
					};

					handlerRegistered = true;
				}
			}

			if (!handlerRegistered)
				throw new ArgumentException("Handler with type " + handler.GetType() + " does not implement any RPC interface.");
		}

		public void RemoveRpcHandler(object handler)
		{
			var keys = new List<byte>();

			foreach (var pair in _handlersData) {
				if (pair.Value.Handler == handler)
					keys.Add(pair.Key);
			}

			foreach (var key in keys)
				_handlersData.Remove(key);
		}

		public void ReceiveData(byte[] data, int offset, int length, object context)
		{
			var reader = new BinaryReader(new MemoryStream(data, offset, length));
			byte header = reader.ReadByte();

			if ((header & ResponseTypeFlag) == 0) {
				byte ifaceOrdinal = header;
				ProcessHandlerCall(reader, ifaceOrdinal, context);
			} else {
				byte callId = (byte) (header & ~ResponseTypeFlag);
				ProcessCallResult(reader, callId);
			}
		}

		public MessageInfo AnalyzeMessage(byte[] data, int offset, int length)
		{
			var reader = new BinaryReader(new MemoryStream(data, offset, length));
			byte header = reader.ReadByte();

			if ((header & ResponseTypeFlag) == 0) {
				byte ifaceOrdinal = header;
				byte methodOrdinal = reader.ReadByte();
				HandlerData handlerData = _handlersData[ifaceOrdinal];
				RpcMethodInfo rpcMethodInfo = handlerData.InterfaceMethods[methodOrdinal];

				return new MessageInfo
				{
					Type = MessageType.HandlerCall,
					InterfaceType = RpcInterface.GetRpcInterface(ifaceOrdinal),
					InterfaceMethodInfo = rpcMethodInfo.MethodInfo,
					Handler = handlerData.Handler,
				};
			}

			return new MessageInfo
			{
				Type = MessageType.CallResult,
			};
		}

		public object[] GetArgsArray(int paramsCount)
		{
			while (_argumentArrays.Count < paramsCount + 1) {
				_argumentArrays.Add(new object[_argumentArrays.Count]);
			}

			return _argumentArrays[paramsCount];
		}

		public object MethodCall(int methodOrdinal, object context, object[] args)
		{
			var clientData = (ClientData) context;
			RpcMethodInfo rpcMethodInfo = clientData.InterfaceMethods[methodOrdinal];
			MethodInfo methodInfo = rpcMethodInfo.MethodInfo;
			object resultFuture = null;

			if (rpcMethodInfo.HasReturnType) {
				resultFuture = Activator.CreateInstance(rpcMethodInfo.ReturnType);
				++ _nextCallId;

				if (_nextCallId >= ResponseTypeFlag)
					_nextCallId = 1;

				if (_resultFutures.ContainsKey(_nextCallId))
					throw new InvalidOperationException("CallId overrun.");

				_resultFutures[_nextCallId] = resultFuture;
			}

			MemoryStream memStream = new MemoryStream(64);
			var writer = new BinaryWriter(memStream);
			writer.Write((byte) clientData.InterfaceOrdinal);
			writer.Write((byte) methodOrdinal);

			if (resultFuture != null)
				writer.Write((byte) _nextCallId);

			ParameterInfo[] paramInfos = methodInfo.GetParameters();

			for (int i = 0, n = paramInfos.Length; i < n; ++ i) {
				var paramInfo = paramInfos[i];
				BinarySerializer.SerializeParameter(writer, paramInfo, args[i]);
			}

			if (OnDataOut != null)
				OnDataOut(memStream.GetBuffer(), 0, checked((int) memStream.Length));

			var returnType = methodInfo.ReturnType;

			if (resultFuture == null && returnType != typeof (void) && returnType.IsValueType)
				return Activator.CreateInstance(returnType);

			return resultFuture;
		}

		private RpcMethodInfo _currentCallMethod;
		private MemoryStream _currentCallStream;
		private BinaryWriter _currentCallWriter;
		private object _currentCallFuture;
		private int _currentCallArg;

		public void BeginCall(int methodOrdinal, object context)
		{
			var clientData = (ClientData) context;
			_currentCallMethod = clientData.InterfaceMethods[methodOrdinal];
			_currentCallFuture = null;

			if (_currentCallMethod.HasReturnType) {
				_currentCallFuture = Activator.CreateInstance(_currentCallMethod.ReturnType);
				++ _nextCallId;

				if (_nextCallId >= ResponseTypeFlag)
					_nextCallId = 1;

				if (_resultFutures.ContainsKey(_nextCallId))
					throw new InvalidOperationException("CallId overrun.");

				_resultFutures[_nextCallId] = _currentCallFuture;
			}

			_currentCallStream = new MemoryStream(64);
			_currentCallWriter = new BinaryWriter(_currentCallStream);
			_currentCallWriter.Write((byte) clientData.InterfaceOrdinal);
			_currentCallWriter.Write((byte) methodOrdinal);

			if (_currentCallFuture != null)
				_currentCallWriter.Write((byte) _nextCallId);

			_currentCallArg = 0;
		}

		public void PushArg<T>(T arg)
		{
			ParameterInfo pi = _currentCallMethod.ParameterTypes[_currentCallArg];
			BinarySerializer.SerializeParameter(_currentCallWriter, pi, arg);
			++ _currentCallArg;
		}

		public object CompleteCall()
		{
			if (OnDataOut != null)
				OnDataOut(_currentCallStream.GetBuffer(), 0, checked((int) _currentCallStream.Length));

			var returnType = _currentCallMethod.ReturnType;

			if (_currentCallFuture == null && returnType != typeof (void) && returnType.IsValueType)
				return Activator.CreateInstance(returnType);

			return _currentCallFuture;
		}

		public object MethodCall1(int methodOrdinal, object context, object arg1)
		{
			return MethodCall(methodOrdinal, context, new[] {arg1});
		}

		private void ProcessHandlerCall(BinaryReader reader, byte ifaceOrdinal, object context)
		{
			HandlerData handlerData;

			if (!_handlersData.TryGetValue(ifaceOrdinal, out handlerData)) {
				Type ifaceType = RpcInterface.GetRpcInterface(ifaceOrdinal);

				if (ifaceType == null)
					throw new ProtocolViolationException("Unknown RPC interface ordinal received: " + ifaceOrdinal);

				MethodInfo mi = RpcInterface.GetOrderedMethods(ifaceType)[reader.ReadByte()].MethodInfo;

				throw new ProtocolViolationException("RPC call received for " + ifaceType.FullName + "." + mi.Name +
				                                     " which does not have a handler registered.");
			}

			byte methodOrdinal = reader.ReadByte();
			RpcMethodInfo rpcMethodInfo = handlerData.InterfaceMethods[methodOrdinal];
			MethodInfo methodInfo = rpcMethodInfo.MethodInfo;
			byte callId = 0;

			if (rpcMethodInfo.HasReturnType)
				callId = reader.ReadByte();

			var paramsInfo = methodInfo.GetParameters();
			var paramValues = new object[paramsInfo.Length];

			for (int i = 0, n = paramValues.Length; i < n; ++ i)
				paramValues[i] = BinarySerializer.DeserializeParameter(reader, paramsInfo[i]);

			var callContext = new HandlerCallContext
			{
				HandlerType = handlerData.RpcInterfaceType,
				Handler = handlerData.Handler,
				Method = methodInfo,
				CallId = callId,
				Context = context,
			};

			if (OnBeforeHandlerCall != null)
				OnBeforeHandlerCall(callContext);

			callContext.Result = methodInfo.Invoke(handlerData.Handler, paramValues);

			if (callContext.Result != null && OnDataOut != null) {
				Type resultFutureType = callContext.Result.GetType();
				var stream = new MemoryStream(64);
				var writer = new BinaryWriter(stream);
				var header = (byte) (ResponseTypeFlag | callId);
				writer.Write((byte) header);

				resultFutureType.GetMethod("Serialize", BindingFlags.NonPublic | BindingFlags.Instance)
					.Invoke(callContext.Result, new object[] {writer});

				OnDataOut(stream.ToArray(), 0, (int) stream.Length);
			}

			if (OnAfterHandlerCall != null)
				OnAfterHandlerCall(callContext);
		}

		private void ProcessCallResult(BinaryReader reader, byte callId)
		{
			object resultFuture;

			if (_resultFutures.TryGetValue(callId, out resultFuture)) {
				_resultFutures.Remove(callId);
				Type futureType = resultFuture.GetType();

				futureType.GetMethod("Complete", BindingFlags.NonPublic | BindingFlags.Instance)
					.Invoke(resultFuture, new object[] {reader});
			} else {
				throw new ProtocolViolationException("Invalid callId received: " + callId);
			}
		}
	}
}

//[RpcInterface(119)]
//public interface ITestis
//{
//	void Work(bool a, int b);
//	IFuture<bool> IsItWorking(bool a, int b);
//	IFuture<DualResult<bool>> TestDualResult(bool success);
//}

//public class TestisHandler : ITestis
//{
//	public void Work(bool a, int b)
//	{
//	}

//	public IFuture<bool> IsItWorking(bool a, int b)
//	{
//		return Res.Create(!a);
//	}

//	public IFuture<DualResult<bool>> TestDualResult(bool success)
//	{
//		if (success)
//			return Res.Success(true);

//		return Res.Error<bool>("Error message");
//	}
//}

//public class Testis
//{
//	public static void Main()
//	{
//		var client = new RpcEndpoint();
//		var server = new RpcEndpoint();

//		client.OnDataOut += server.ReceiveData;
//		server.OnDataOut += client.ReceiveData;
//		server.RegisterRpcHandler(new TestisHandler());

//		var testis = client.GetRpcInterface<ITestis>();

//		var watch = new Stopwatch();
//		watch.Start();

//		for (int i = 0; i < 100000; ++i) {
//			testis.IsItWorking(true, 5).OnComplete(working => {
//			});
//		}

//		Console.WriteLine(watch.ElapsedMilliseconds);

//		testis.TestDualResult(true).OnComplete(dr => {
//			if (dr.IsError)
//				Console.WriteLine(dr.Error);
//			else
//				Console.WriteLine(dr.Result);
//		});


//		testis.TestDualResult(false).OnComplete(dr => {
//			if (dr.IsError)
//				Console.WriteLine(dr.Error);
//			else
//				Console.WriteLine(dr.Result);
//		});

//		Console.ReadKey();
//	}
//}
