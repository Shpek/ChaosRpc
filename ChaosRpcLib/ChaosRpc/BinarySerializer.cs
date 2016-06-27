using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;

namespace ChaosRpc
{
	[AttributeUsage(AttributeTargets.Field | AttributeTargets.Parameter)]
	public class Nullable : Attribute
	{
	}

	internal interface ISerializer
	{
		void SerializeParameter(BinaryWriter writer, ParameterInfo paramInfo, object value);
		object DeserializeParameter(BinaryReader reader, ParameterInfo paramInfo);
		void Serialize(BinaryWriter writer, Type type, bool nullable, object value);
		object Deserialize(BinaryReader reader, Type type, bool nullable);
	}

	internal static class BinarySerializer
	{
		private class SerializableTypeInfo
		{
			public ConstructorInfo Constructor;
			public MethodInfo SerializeMethod;
			public MethodInfo DeserializeMethod;
		}

		private static readonly Dictionary<Type, Action<BinaryWriter, object, bool>> WriteFunctions =
			new Dictionary<Type, Action<BinaryWriter, object, bool>>
			{
				{typeof (bool), (writer, obj, nullable) => writer.Write((bool) obj)},
				{typeof (byte), (writer, obj, nullable) => writer.Write((byte) obj)},
				{typeof (sbyte), (writer, obj, nullable) => writer.Write((sbyte) obj)},
				{typeof (char), (writer, obj, nullable) => writer.Write((char) obj)},
				{typeof (decimal), (writer, obj, nullable) => writer.Write((decimal) obj)},
				{typeof (double), (writer, obj, nullable) => writer.Write((double) obj)},
				{typeof (float), (writer, obj, nullable) => writer.Write((float) obj)},
				{typeof (int), (writer, obj, nullable) => writer.Write((int) obj)},
				{typeof (uint), (writer, obj, nullable) => writer.Write((uint) obj)},
				{typeof (long), (writer, obj, nullable) => writer.Write((long) obj)},
				{typeof (ulong), (writer, obj, nullable) => writer.Write((ulong) obj)},
				{typeof (short), (writer, obj, nullable) => writer.Write((short) obj)},
				{typeof (ushort), (writer, obj, nullable) => writer.Write((ushort) obj)},
				{typeof (string), (writer, obj, nullable) => writer.Write((string) obj)},
				{typeof (DateTime), (writer, obj, nullable) => writer.Write(((DateTime) obj).ToBinary())},
				{typeof (TimeSpan), (writer, obj, nullable) => writer.Write(((TimeSpan) obj).Ticks)},
			};

		private static readonly Dictionary<Type, Func<BinaryReader, bool, object>> ReadFunctions =
			new Dictionary<Type, Func<BinaryReader, bool, object>>
			{
				{typeof (bool), (reader, nullable) => reader.ReadBoolean()},
				{typeof (byte), (reader, nullable) => reader.ReadByte()},
				{typeof (sbyte), (reader, nullable) => reader.ReadSByte()},
				{typeof (char), (reader, nullable) => reader.ReadChar()},
				{typeof (decimal), (reader, nullable) => reader.ReadDecimal()},
				{typeof (double), (reader, nullable) => reader.ReadDouble()},
				{typeof (float), (reader, nullable) => reader.ReadSingle()},
				{typeof (int), (reader, nullable) => reader.ReadInt32()},
				{typeof (uint), (reader, nullable) => reader.ReadUInt32()},
				{typeof (long), (reader, nullable) => reader.ReadInt64()},
				{typeof (ulong), (reader, nullable) => reader.ReadUInt64()},
				{typeof (short), (reader, nullable) => reader.ReadInt16()},
				{typeof (ushort), (reader, nullable) => reader.ReadUInt16()},
				{typeof (string), (reader, nullable) => reader.ReadString()},
				{typeof (DateTime), (reader, nullable) => DateTime.FromBinary(reader.ReadInt64())},
				{typeof (TimeSpan), (reader, nullable) => TimeSpan.FromTicks(reader.ReadInt64())},
			};

		private static readonly Type[] BinaryWriterParam = { typeof (BinaryWriter) };
		private static readonly Type[] BinaryReaderParam = { typeof (BinaryReader) };

		private static readonly HashSet<ICustomAttributeProvider> NullableParamCache =
			new HashSet<ICustomAttributeProvider>();

		private static readonly Dictionary<Type, SerializableTypeInfo> SerializableTypeCache =
			new Dictionary<Type, SerializableTypeInfo>();
		
		public static void SerializeParameter<T>(BinaryWriter writer, ParameterInfo paramInfo, T value)
		{
			Serialize(writer, paramInfo.ParameterType, NullableParamCache.Contains(paramInfo), value);
		}

		public static object DeserializeParameter(BinaryReader reader, ParameterInfo paramInfo)
		{
			return Deserialize(reader, paramInfo.ParameterType, NullableParamCache.Contains(paramInfo));
		}

		public static void Serialize<T>(BinaryWriter writer, Type type, bool nullable, T value)
		{
			if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof (System.Nullable<>)) {
				Type underlyingType = type.GetGenericArguments()[0];
				Serialize(writer, underlyingType, true, value);
				return;
			}

			if (nullable) {
				if (value == null) {
					writer.Write((byte) 0);
					return;
				}

				writer.Write((byte) 1);
			} else if (value == null)
				throw new SerializationException("Trying to serialize a non Nullable null value");

			Action<BinaryWriter, object, bool> serializerFunc;

			if (WriteFunctions.TryGetValue(type, out serializerFunc)) {
				serializerFunc(writer, value, nullable);
				return;
			}

			if (type.IsEnum) {
				Type underlyingType = Enum.GetUnderlyingType(type);

				if (WriteFunctions.TryGetValue(underlyingType, out serializerFunc)) {
					serializerFunc(writer, Convert.ChangeType(value, underlyingType), nullable);
				} else
					throw new SerializationException("Enum " + type.Name + " underlying type is not serializable.");

				return;
			}

			SerializableTypeInfo serTypeInfo = SerializableTypeCache[type];

			if (serTypeInfo != null) {
				if (serTypeInfo.SerializeMethod.IsStatic) {
					serTypeInfo.SerializeMethod.Invoke(null, new object[] {value, writer});
				} else {
					serTypeInfo.SerializeMethod.Invoke(value, new object[] {writer});
				}

				return;
			}

			Type elementType = GetListElementType(type);

			if (elementType != null) {
				Action<BinaryWriter, object, bool> writeFunc = (wrtr, vlue, nlble) => {
					wrtr.Write(checked ((ushort) ((ICollection) vlue).Count));

					foreach (var el in (IEnumerable) vlue)
						Serialize(wrtr, elementType, nlble, el);
				};

				WriteFunctions[type] = writeFunc;
				writeFunc(writer, value, nullable);
				return;
			}

			foreach (var info in RpcInterface.GetOrederedFields(type)) {
				if (!info.IsStatic)
					Serialize(writer, info.FieldType, NullableParamCache.Contains(info), info.GetValue(value));
			}

			foreach (var info in RpcInterface.GetOrderedProperties(type)) {
				if (info.CanRead && info.CanWrite)
					Serialize(writer, info.PropertyType, NullableParamCache.Contains(info), info.GetValue(value, null));
			}
		}

		public static object Deserialize(BinaryReader reader, Type type, bool nullable)
		{
			if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof (System.Nullable<>)) {
				Type underlyingType = type.GetGenericArguments()[0];
				return Deserialize(reader, underlyingType, true);
			}

			if (nullable && reader.ReadByte() == 0)
				return null;

			Func<BinaryReader, bool, object> deserializerFunc;

			if (ReadFunctions.TryGetValue(type, out deserializerFunc))
				return deserializerFunc(reader, nullable);

			if (type.IsEnum) {
				Type underlyingType = Enum.GetUnderlyingType(type);

				if (ReadFunctions.TryGetValue(underlyingType, out deserializerFunc)) {
					object value = deserializerFunc(reader, nullable);
					return Enum.ToObject(type, value);
				}

				throw new SerializationException("Enum " + type.Name + " underlying type is not serializable.");
			}

			SerializableTypeInfo serTypeInfo = SerializableTypeCache[type];

			if (serTypeInfo != null) {
				if (serTypeInfo.DeserializeMethod != null)
					return serTypeInfo.DeserializeMethod.Invoke(null, new object[] {reader});

				if (serTypeInfo.Constructor != null)
					return Activator.CreateInstance(type, reader);
			}

			Type elementType = GetListElementType(type);

			if (elementType != null) { 
				Func<BinaryReader, bool, object> readFunc;

				if (type.IsArray) {
					readFunc = (rdr, nlble) => {
						int length = rdr.ReadUInt16();
						var array = (Array) Activator.CreateInstance(type, length);
	
						for (int i = 0; i < length; ++ i)
							array.SetValue(Deserialize(rdr, elementType, nlble), i);

						return array;
					};
				} else {
					Type listType = typeof (List<>).MakeGenericType(elementType);

					readFunc = (rdr, nlble) => {
						int length = rdr.ReadUInt16();
						var list = (IList) Activator.CreateInstance(listType);
	
						for (int i = 0; i < length; ++ i)
							list.Add(Deserialize(rdr, elementType, nlble));

						return list;
					};
				}

				ReadFunctions[type] = readFunc;
				return readFunc(reader, nullable);
			}

			var obj = Activator.CreateInstance(type);

			foreach (var info in RpcInterface.GetOrederedFields(type)) {
				if (!info.IsStatic) {
					var fieldValue = Deserialize(reader, info.FieldType, NullableParamCache.Contains(info));
					info.SetValue(obj, fieldValue);
				}
			}

			foreach (var info in RpcInterface.GetOrderedProperties(type)) {
				if (info.CanRead && info.CanWrite) {
					var propValue = Deserialize(reader, info.PropertyType, NullableParamCache.Contains(info));
					info.SetValue(obj, propValue, null);
				}
			}

			return obj;
		}

		public static void UpdateParameterCaches(RpcMethodInfo rpcMethodInfo, HashSet<Type> alreadyCheckedTypes)
		{
			MethodInfo methodInfo = rpcMethodInfo.MethodInfo;
			var paramsInfo = methodInfo.GetParameters();
			

			for (int i = 0, n = paramsInfo.Length; i < n; ++i) {
				ParameterInfo paramInfo = paramsInfo[i];

				// Store if this parameter is Nullable
				if (paramInfo.HasAttribute<Nullable>())
					NullableParamCache.Add(paramInfo);

				Type type = paramInfo.ParameterType;
				UpdateType(type, alreadyCheckedTypes);
			}

			if (rpcMethodInfo.HasReturnType) {
				if (rpcMethodInfo.ReturnType.IsGenericType) {
					foreach (Type genericArg in rpcMethodInfo.ReturnType.GetGenericArguments()) {
						UpdateType(genericArg, alreadyCheckedTypes);
					}
				} else {
					UpdateType(rpcMethodInfo.ReturnType, alreadyCheckedTypes);
				}
			}
		}

		private static void UpdateType(Type type, HashSet<Type> checkedTypes)
		{
			if (checkedTypes.Contains(type))
				return;

			checkedTypes.Add(type);
			SerializableTypeInfo typeInfo = CheckSerializableTypeInfo(type);
			SerializableTypeCache[type] = typeInfo;

			if (typeInfo == null) {
				if (typeof (IList).IsAssignableFrom(type)) {
					Type elementType = GetListElementType(type);

					if (elementType != null)
						UpdateType(elementType, checkedTypes);

					return;
				}

				if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof (System.Nullable<>)) {
					UpdateType(type.GetGenericArguments()[0], checkedTypes);
					return;
				}

				foreach (var info in type.GetFields()) {
					if (!info.IsStatic) {
						if (info.HasAttribute<Nullable>())
							NullableParamCache.Add(info);

						UpdateType(info.FieldType, checkedTypes);
					}
				}

				foreach (var info in type.GetProperties()) {
					if (info.CanRead && info.CanWrite) {
						if (info.HasAttribute<Nullable>())
							NullableParamCache.Add(info);

						UpdateType(info.PropertyType, checkedTypes);
					}
				}
			}
		}

		private static SerializableTypeInfo CheckSerializableTypeInfo(Type type)
		{
			MethodInfo serializeMethod = GetNonGenericMethod(type, "Serialize", BinaryWriterParam);
			
			if (serializeMethod == null) {
				serializeMethod = GetNonGenericMethod(type, "Serialize", new[] {type, typeof (BinaryWriter)});

				if (serializeMethod == null || !serializeMethod.IsStatic) {
					return null;
				}
			}

			MethodInfo deserializeMethod;
			Type currentType = type;
			Type baseType = type.IsValueType ? typeof (ValueType) : typeof (object);

			do {
				Debug.Assert(currentType != null, "currentType != null");
				deserializeMethod = GetNonGenericMethod(currentType,"Deserialize", BinaryReaderParam);

				if (deserializeMethod != null)
					break;

				currentType = currentType.BaseType;
			} while (currentType != baseType);

			if (deserializeMethod != null && deserializeMethod.ReturnType == currentType && deserializeMethod.IsStatic) {
				// The type has Serialize() and static Deserialize() methods
				return new SerializableTypeInfo
				{
					DeserializeMethod = deserializeMethod,
					SerializeMethod = serializeMethod,
				};
			}

			ConstructorInfo constructor = type.GetConstructor(BinaryReaderParam);

			if (constructor != null) {
				// The type has Serialize() method and a constructor with a BinaryReader
				return new SerializableTypeInfo
				{
					Constructor = constructor,
					SerializeMethod = serializeMethod,
				};
			}

			return null;
		}

		private static Type GetListElementType(Type listType)
		{
			Type elementType = null;

			if (listType.IsArray) {
				elementType = listType.GetElementType();
			} else {
				if (listType.IsGenericType && listType.GetGenericTypeDefinition() == typeof (IList<>)) {
					elementType = listType.GetGenericArguments()[0];
				} 
				
				//else {
				//	Type[] interfaces = listType.FindInterfaces(
				//		(t, c) => t.IsGenericType && t.GetGenericTypeDefinition() == typeof (IList<>)
				//		, null);

				//	if (interfaces.Length > 0)
				//		elementType = interfaces[0].GetGenericArguments()[0];
				//}
			}

			return elementType;
		}

		private static void MsbEncode(BinaryWriter writer, ulong num)
		{
			while (num > 127) {
				byte b = (byte) (((byte) num & 127) | 128);
				writer.Write(b);
				num >>= 7;
			}

			writer.Write((byte) num & 127);
		}

		private static ulong MsbDecode(BinaryReader reader)
		{
			ulong num = 0;
			int numBytes = 0;

			while (true) {
				var b = reader.ReadByte();
				num |= (ulong) (b & 127) << (7 * numBytes);

				if ((b & 128) == 0)
					break;

				++ numBytes;
			}

			return num;
		}

		private static MethodInfo GetNonGenericMethod(Type type, string name, Type[] paramTypes)
		{
			foreach (var mi in type.GetMethods().Where(mi => !mi.IsGenericMethod && mi.Name == name)) {
				ParameterInfo[] parameters = mi.GetParameters();

				if (parameters.Length == paramTypes.Length) {
					bool match = true;

					for (int i = 0, n = parameters.Length; i < n; ++ i) {
						if (parameters[i].ParameterType != paramTypes[i]) {
							match = false;
							break;
						}
					}

					if (match) {
						return mi;
					}
				}
			}

			return null;
		}
	}
}
