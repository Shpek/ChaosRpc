using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using ChaosRpc;

namespace ChaosRpc
{
	internal class RpcMethodInfo
	{
		public bool HasReturnType
		{
			get { return ReturnType != null; }
		}

		public MethodInfo MethodInfo;
		public ParameterInfo[] ParameterTypes;
		public Type ReturnType;
	}

	[AttributeUsage(AttributeTargets.Interface)]
	public sealed class RpcInterface : Attribute
	{
		public byte Ordinal { get; private set; }

		private static readonly Dictionary<byte, Type> OrdinalToInterfaceType = new Dictionary<byte, Type>();
		private static readonly Dictionary<Type, Type> InterfaceTypeToProxyType = new Dictionary<Type, Type>();

		private static readonly Dictionary<Type, RpcMethodInfo[]> InterfaceTypeToMethodInfos =
			new Dictionary<Type, RpcMethodInfo[]>();

		private static ModuleBuilder _moduleBuilder;

		public RpcInterface(byte ordinal)
		{
			Ordinal = ordinal;
		}

		static RpcInterface()
		{
			var executingAssembly = Assembly.GetExecutingAssembly();
			var executingAssemblyName = executingAssembly.FullName;
			Assembly[] assemblies = AppDomain.CurrentDomain.GetAssemblies();

			foreach (var assembly in assemblies) {
				if (assembly != executingAssembly) {
					// Find if the executing assembly is referenced by the current.
					// If it is it may contain interfaces marked with RpcInterface
					var referencedAssemblies = assembly.GetReferencedAssemblies();
					var referenced = referencedAssemblies.Any(refAssembly => refAssembly.FullName == executingAssemblyName);

					if (!referenced)
						continue;
				}

				var checkedTypes = new HashSet<Type>();

				foreach (Type type in assembly.GetTypes()) {
					var iface = type.GetAttribute<RpcInterface>();

					if (iface != null) {
						if (OrdinalToInterfaceType.ContainsKey(iface.Ordinal))
							throw new ArgumentException("Duplicate RpcInterface ordinal " + iface.Ordinal + " in " + type.Name);

						OrdinalToInterfaceType[iface.Ordinal] = type;
						InterfaceTypeToProxyType[type] = CreateDynamicProxyType(type, checkedTypes);
					}
				}
			}
		}

		// The generated code for the proxy type is like this:
		//
		//	public class IBlahServiceRpcProxy : IBlahService
		//	{
		//		public ICallInterceptor Interceptor;
		//		public object Context;
		//
		//		public int Blah(int param1, string param2)
		//		{
		//      object argsArray = Interceptor.GetArgsArray(2);
		//      argsArray[0] = param1;
		//      argsArray[1] = param2;
		//			return (int) Interceptor.MethodCall(0, Context, argsArray);
		//		}
		//	}

		private static Type CreateDynamicProxyType(Type type, HashSet<Type> checkedTypes)
		{
			if (_moduleBuilder == null) {
				const string assemblyName = "RpcDynamicProxies";

				AssemblyBuilder assemblyBuilder = AppDomain.CurrentDomain.DefineDynamicAssembly(
					new AssemblyName(assemblyName),
					AssemblyBuilderAccess.Run
					);

				_moduleBuilder = assemblyBuilder.DefineDynamicModule(assemblyName);
			}

			TypeBuilder tb = _moduleBuilder.DefineType(type.Name + "RpcProxy", TypeAttributes.Public);
			tb.AddInterfaceImplementation(type);

			FieldBuilder interceptorFi = tb.DefineField("Interceptor", typeof (ICallInterceptor), FieldAttributes.Public);
			FieldBuilder contextFi = tb.DefineField("Context", typeof (object), FieldAttributes.Public);

			RpcMethodInfo[] methods = RpcInterface.GetOrderedMethods(type);
			MethodInfo methodCall = typeof (ICallInterceptor).GetMethod("MethodCall");
			MethodInfo getArgsArray = typeof (ICallInterceptor).GetMethod("GetArgsArray");
			MethodInfo genericPushArg = typeof (ICallInterceptor).GetMethod("GetArg");
			var genericPushArgMethods = new Dictionary<Type, MethodInfo>();

			for (int ord = 0; ord < methods.Length; ++ ord) {
				RpcMethodInfo rpcMethodInfo = methods[ord];
				BinarySerializer.UpdateParameterCaches(rpcMethodInfo, checkedTypes);
				MethodInfo methodInfo = rpcMethodInfo.MethodInfo;
				ParameterInfo[] paramInfo = rpcMethodInfo.ParameterTypes;
				var paramTypes = new Type[paramInfo.Length];

				for (int i = 0, n = paramInfo.Length; i < n; ++ i) {
					paramTypes[i] = paramInfo[i].ParameterType;

					if (!genericPushArgMethods.ContainsKey(paramTypes[i])) {
						genericPushArgMethods[paramTypes[i]] = genericPushArg.MakeGenericMethod(paramTypes[i]);
					}
				}

				MethodBuilder mtb = tb.DefineMethod(
					methodInfo.Name,
					MethodAttributes.Public | MethodAttributes.Virtual | MethodAttributes.Final,
					methodInfo.ReturnType,
					paramTypes);

				ILGenerator gen = mtb.GetILGenerator();
				gen.DeclareLocal(typeof (object[]));

				gen.Emit(OpCodes.Ldarg_0);
				gen.Emit(OpCodes.Ldfld, interceptorFi);
				gen.Emit(OpCodes.Ldc_I4, paramInfo.Length);
				gen.EmitCall(OpCodes.Callvirt, getArgsArray, null);
				gen.Emit(OpCodes.Stloc_0);
				
				gen.Emit(OpCodes.Ldarg_0);
				gen.Emit(OpCodes.Ldfld, interceptorFi);

				gen.Emit(OpCodes.Ldc_I4, ord);

				gen.Emit(OpCodes.Ldarg_0);
				gen.Emit(OpCodes.Ldfld, contextFi);

				for (int i = 0, n = paramInfo.Length; i < n; ++ i) {
					Type paramType = paramInfo[i].ParameterType;

					if (paramType.IsValueType) {
						gen.Emit(OpCodes.Ldloc_0);
						gen.Emit(OpCodes.Ldc_I4, i);
						gen.Emit(OpCodes.Ldarg, i + 1);
						gen.Emit(OpCodes.Box, paramType);
						gen.Emit(OpCodes.Stelem_Ref);
					} else {
						gen.Emit(OpCodes.Ldloc_0);
						gen.Emit(OpCodes.Ldc_I4, i);
						gen.Emit(OpCodes.Ldarg, i + 1);
						gen.Emit(OpCodes.Stelem_Ref);
					}
				}

				gen.Emit(OpCodes.Ldloc_0);

				gen.EmitCall(OpCodes.Callvirt, methodCall, null);

				if (mtb.ReturnType == typeof (void))
					gen.Emit(OpCodes.Pop);
				else
					gen.Emit(OpCodes.Castclass, mtb.ReturnType);

				gen.Emit(OpCodes.Ret);
			}

			return tb.CreateType();
		}

		public static T CreateDynamicProxy<T>(ICallInterceptor interceptor, object context)
		{
			Type proxyType = InterfaceTypeToProxyType[typeof (T)];
			var proxy = (T) Activator.CreateInstance(proxyType);
			proxy.GetType().GetField("Interceptor").SetValue(proxy, interceptor);
			proxy.GetType().GetField("Context").SetValue(proxy, context);
			return proxy;
		}

		public static Type GetRpcInterface(byte interfaceOrdinal)
		{
			Type type;
			OrdinalToInterfaceType.TryGetValue(interfaceOrdinal, out type);
			return type;
		}

		// This should return the methods of the type in the same order every time it is called.
		internal static RpcMethodInfo[] GetOrderedMethods(Type type)
		{
			RpcMethodInfo[] rpcMethodInfos;

			if (!InterfaceTypeToMethodInfos.TryGetValue(type, out rpcMethodInfos)) {
				rpcMethodInfos = type.GetMethods()
					.OrderBy(m => m.MetadataToken)
					.Select(m => GetRpcMethodInfo(m)).ToArray();

				InterfaceTypeToMethodInfos[type] = rpcMethodInfos;
			}

			return rpcMethodInfos;
		}

		// This should return the fields of the type in the same order every time it is called.
		internal static IEnumerable<FieldInfo> GetOrederedFields(Type type)
		{
			return type.GetFields().OrderBy(f => f.MetadataToken);
		}

		// This should return the properties of the type in the same order every time it is called.
		internal static IEnumerable<PropertyInfo> GetOrderedProperties(Type type)
		{
			return type.GetProperties().OrderBy(p => p.MetadataToken);
		}

		private static RpcMethodInfo GetRpcMethodInfo(MethodInfo methodInfo)
		{
			var rpcMethodInfo = new RpcMethodInfo
			{
				MethodInfo = methodInfo,
				ParameterTypes = methodInfo.GetParameters(),
			};

			Type returnType = methodInfo.ReturnType;

			if (returnType == typeof (void))
				return rpcMethodInfo;

			if (returnType.IsGenericType) {
				Type genericTypeDef = returnType.GetGenericTypeDefinition();
				var genericArguments = returnType.GetGenericArguments();
				Type concreteGenericTypeDef;

				if (genericTypeDef == typeof (IFuture<>)) {
					concreteGenericTypeDef = typeof (Future<>);
				} else if (genericTypeDef == typeof (IFutureErr<>)) {
					concreteGenericTypeDef = typeof (FutureErr<>);
				} else {
					throw new InvalidOperationException("Invalid return type of method " +
					                                    methodInfo.DeclaringType.Name + "." + methodInfo.Name +
					                                    ". Allowed types are IFuture and IFutureErr type found is " +
					                                    returnType.Name);
				}

				rpcMethodInfo.ReturnType = concreteGenericTypeDef.MakeGenericType(genericArguments);
			} else {
				if (returnType == typeof (IFuture))
					rpcMethodInfo.ReturnType = typeof (Future);
				else if (returnType == typeof (IFutureErr)) {
					rpcMethodInfo.ReturnType = typeof (FutureErr);
				} else {
					throw new InvalidOperationException("Invalid return type of method " +
					                                    methodInfo.DeclaringType.Name + "." + methodInfo.Name +
					                                    ". Allowed types are IFuture and IFutureErr type found is " +
					                                    returnType.Name);
				}
			}

			return rpcMethodInfo;
		}
	}

	public delegate void DataCallback(byte[] data, int offset, int length);

	public interface ICallInterceptor
	{
		object[] GetArgsArray(int paramsCount);
		object MethodCall(int methodOrdinal, object context, object[] args);

		void BeginCall(int methodOrdinal, object context);
		void PushArg<T>(T arg);
		object CompleteCall();
	}

	public static class AttributeHelper
	{
		public static T GetAttribute<T>(this Type type)
		{
			return (T) type.GetCustomAttributes(typeof (T), false).FirstOrDefault();
		}

		public static bool HasAttribute<T>(this ICustomAttributeProvider type)
		{
			return type.IsDefined(typeof (T), false);
		}
	}
}

public class TestInterceptor
{
	private ICallInterceptor _interceptor;
	private object _context;

	public IFuture Test()
	{
		_interceptor.BeginCall(3, _context);
		_interceptor.PushArg(5);
		return (IFuture) _interceptor.CompleteCall();
	}
}
