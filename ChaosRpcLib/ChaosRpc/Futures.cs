using System;
using System.IO;

namespace ChaosRpc
{
	public interface IFuture
	{
		bool IsComplete { get; }

		void OnComplete(Action resultCallback);
	}

	public interface IFuture<out TResult>
	{
		TResult Result { get; }
		bool IsComplete { get; }

		void OnComplete(Action<TResult> resultCallback);
	}

	public interface IFutureErr
	{
		string Error { get; }
		bool IsComplete { get; }
		bool IsError { get; }

		IFutureErr OnResult(Action<string> resultCallback);
		IFutureErr OnSuccess(Action successCallback);
		IFutureErr OnError(Action<string> errorCallback);
	}

	public interface IFutureErr<out TResult>
	{
		TResult Result { get; }
		string Error { get; }
		bool IsComplete { get; }
		bool IsError { get; }

		IFutureErr<TResult> OnComplete(Action<TResult, string> resultCallback);
		IFutureErr<TResult> OnSuccess(Action<TResult> successCallback);
		IFutureErr<TResult> OnError(Action<string> errorCallback);
	}

	public static class Res
	{
		public static IFuture Complete()
		{
			return new ResultFuture();
		}

		public static IFuture<TResult> Create<TResult>(TResult result)
		{
			return new ResultFuture<TResult>(result);
		}

		public static IFutureErr<TResult> Success<TResult>(TResult result)
		{
			return ResultFutureErr<TResult>.CreateSuccess(result);
		}

		public static IFutureErr Success()
		{
			return new ResultFutureErr();
		}

		public static IFutureErr<TResult> Error<TResult>(string error)
		{
			return ResultFutureErr<TResult>.CreateError(error);
		}

		public static IFutureErr Error(string error)
		{
			return new ResultFutureErr(error);
		}
	}

	public static class Res<TResult>
	{
		public static IFutureErr<TResult> Error(string error)
		{
			return ResultFutureErr<TResult>.CreateError(error);
		}
	}

	internal class ResultFuture : IFuture
	{
		public bool IsComplete { get { return true; } }

		public void OnComplete(Action resultCallback)
		{
			// empty
		}

		internal void Serialize(BinaryWriter writer)
		{
			// Nothing to serialize here
		}
	}

	internal class ResultFuture<TResult> : IFuture<TResult>
	{
		public TResult Result { get; private set; }
		public bool IsComplete { get { return true; } }

		public ResultFuture(TResult result)
		{
			Result = result;
		}

		public void OnComplete(Action<TResult> resultCallback)
		{
			// empty
		}

		internal void Serialize(BinaryWriter writer)
		{
			BinarySerializer.Serialize(writer, typeof (TResult), true, Result);
		}
	}

	internal class ResultFutureErr : IFutureErr
	{
		public string Error { get; private set; }
		public bool IsComplete { get { return true; } }
		public bool IsError { get { return Error != null; } }

		public ResultFutureErr()
		{
			// empty
		}

		public ResultFutureErr(string error)
		{
			Error = error;
		}

		public IFutureErr OnResult(Action<string> resultCallback)
		{
			return this;
		}

		public IFutureErr OnSuccess(Action successCallback)
		{
			return this;
		}

		public IFutureErr OnError(Action<string> errorCallback)
		{
			return this;
		}

		internal void Serialize(BinaryWriter writer)
		{
			BinarySerializer.Serialize(writer, typeof (string), true, Error);
		}
	}

	internal class ResultFutureErr<TResult> : IFutureErr<TResult>
	{
		public TResult Result { get; private set; }
		public string Error { get; private set; }
		public bool IsComplete { get { return true; } }
		public bool IsError { get { return Error != null; } }

		private ResultFutureErr()
		{
			// empty
		}

		public IFutureErr<TResult> OnComplete(Action<TResult, string> resultCallback)
		{
			return this;
		}

		public IFutureErr<TResult> OnSuccess(Action<TResult> successCallback)
		{
			return this;
		}

		public IFutureErr<TResult> OnError(Action<string> errorCallback)
		{
			return this;
		}

		public static ResultFutureErr<TResult> CreateSuccess(TResult result)
		{
			return new ResultFutureErr<TResult> {Result = result};
		}

		public static ResultFutureErr<TResult> CreateError(string error)
		{
			return new ResultFutureErr<TResult> {Error = error};
		}

		internal void Serialize(BinaryWriter writer)
		{
			BinarySerializer.Serialize(writer, typeof (string), true, Error);

			if (Error == null)
				BinarySerializer.Serialize(writer, typeof (TResult), true, Result);
		}
	}

	internal class Future : IFuture
	{
		public bool IsComplete { get { return _complete; } }

		private bool _complete;
		private Action _completeCallback;

		public void OnComplete(Action completeCallback)
		{
			_completeCallback = completeCallback;

			if (_complete)
				completeCallback();
		}

		internal void Complete(BinaryReader reader)
		{
			_complete = true;

			if (_completeCallback != null)
				_completeCallback();
		}
	}

	internal class Future<TResult> : IFuture<TResult>
	{
		public TResult Result { get { if (!_isComplete) throw new InvalidOperationException("There's no result yet"); return _result; } }
		public bool IsComplete { get { return _isComplete; } }

		private TResult _result;
		private bool _isComplete;
		private Action<TResult> _resultCallback;

		public void OnComplete(Action<TResult> resultCallback)
		{
			_resultCallback = resultCallback;

			if (_isComplete)
				resultCallback(_result);
		}

		internal void Complete(BinaryReader reader)
		{
			var result = (TResult) BinarySerializer.Deserialize(reader, typeof(TResult), true);
			_isComplete = true;
			_result = result;

			if (_resultCallback != null)
				_resultCallback(result);
		}
	}

	internal class FutureErr : IFutureErr
	{
		public string Error { get { return _error; } }
		public bool IsComplete { get { return _isComplete; } }
		public bool IsError { get { return _error != null; } }

		private string _error;
		private bool _isComplete;

		private Action<string> _resultCallback;
		private Action _successCallback;
		private Action<string> _errorCallback;

		public IFutureErr OnResult(Action<string> resultCallback)
		{
			_resultCallback = resultCallback;

			if (_isComplete)
				resultCallback(_error);

			return this;
		}

		public IFutureErr OnSuccess(Action successCallback)
		{
			_successCallback = successCallback;

			if (_isComplete && !IsError)
				successCallback();

			return this;
		}

		public IFutureErr OnError(Action<string> errorCallback)
		{
			_errorCallback = errorCallback;

			if (_isComplete && IsError)
				errorCallback(_error);

			return this;
		}

		internal void Complete(BinaryReader reader)
		{
			var error = (string) BinarySerializer.Deserialize(reader, typeof (string), true);

			_isComplete = true;
			_error = error;

			if (_resultCallback != null)
				_resultCallback(error);

			if (_successCallback != null && !IsError)
				_successCallback();

			if (_errorCallback != null && IsError)
				_errorCallback(_error);
		}
	}

	internal class FutureErr<TResult> : IFutureErr<TResult>
	{
		public TResult Result { get { return _result; } }
		public string Error { get { return _error; } }
		public bool IsComplete { get { return _isComplete; } }
		public bool IsError { get { return _error != null; } }

		private TResult _result;
		private string _error;
		private bool _isComplete;

		private Action<TResult, string> _resultCallback;
		private Action<TResult> _successCallback;
		private Action<string> _errorCallback;

		public IFutureErr<TResult> OnComplete(Action<TResult, string> resultCallback)
		{
			_resultCallback = resultCallback;

			if (_isComplete)
				resultCallback(_result, _error);

			return this;
		}

		public IFutureErr<TResult> OnSuccess(Action<TResult> successCallback)
		{
			_successCallback = successCallback;

			if (_isComplete && !IsError)
				successCallback(_result);

			return this;
		}

		public IFutureErr<TResult> OnError(Action<string> errorCallback)
		{
			_errorCallback = errorCallback;

			if (_isComplete && IsError)
				errorCallback(_error);

			return this;
		}

		internal void Complete(BinaryReader reader)
		{
			var error = (string) BinarySerializer.Deserialize(reader, typeof (string), true);
			TResult result = default (TResult);

			if (error == null)
				result = (TResult) BinarySerializer.Deserialize(reader, typeof (TResult), true);

			_isComplete = true;
			_result = result;
			_error = error;

			if (_resultCallback != null)
				_resultCallback(result, error);

			if (_successCallback != null && !IsError)
				_successCallback(_result);

			if (_errorCallback != null && IsError)
				_errorCallback(_error);
		}
	}
}
