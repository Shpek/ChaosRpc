using System;
using System.Collections.Generic;

namespace ChaosRpc
{
	internal class ThreadLocal<T> : IDisposable
	{
		[ThreadStatic] private static Dictionary<object, T> _lookupTable;

		private readonly Func<T> _init;

		public ThreadLocal() : this(() => default(T))
		{
		}

		public ThreadLocal(Func<T> init)
		{
			_init = init;
		}

		~ThreadLocal()
		{
			Dispose(false);
		}

		public T Value
		{
			get
			{
				T returnValue;

				if (_lookupTable == null)
				{
					_lookupTable = new Dictionary<object, T>();
					returnValue = _lookupTable[this] = _init();
				}
				else
				{
					if (!_lookupTable.TryGetValue(this, out returnValue))
						returnValue = _lookupTable[this] = _init();
				}

				return returnValue;
			}
			set
			{
				if (_lookupTable == null)
					_lookupTable = new Dictionary<object, T>();
				_lookupTable[this] = value;
			}
		}

		public void Dispose()
		{
			Dispose(true);
			GC.SuppressFinalize(this);
		}

		public void Dispose(bool disposing)
		{
			if (disposing)
			{
				if (_lookupTable != null)
				{
					if (_lookupTable.ContainsKey(this))
						_lookupTable.Remove(this);
				}
			}
		}
	}
}
