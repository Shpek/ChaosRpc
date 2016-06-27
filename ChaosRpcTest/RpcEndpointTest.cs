using ChaosRpc;
using NUnit.Framework;

namespace ChaosRpcTest
{
	[RpcInterface(1)]
	public interface ITestRpcService
	{
		void Test(int i);
	}

	public class TestRpcService : ITestRpcService
	{
		public int Result;

		public void Test(int i)
		{
			Result = i;
		}
	}

	[TestFixture]
	public class RpcEndpointTest
	{
		[Test]
		public void RpcCallTest()
		{
			var client = new RpcEndpoint();
			var server = new RpcEndpoint();
			client.OnDataOut += (data, offset, length) => server.ReceiveData(data, offset, length, null);
			var service = new TestRpcService();
			server.RegisterRpcHandler(service);
			client.GetRpcInterface<ITestRpcService>().Test(42);
			Assert.AreEqual(42, service.Result);
		}
	}
}
