using System.Net;

namespace XMATTests
{
	[TestClass]
	public class Utilities
	{
		[TestMethod]
		public void ResolveIPV4Address()
		{
			Task<IPAddress> ipAddrTask = XMAT.PublicUtilities.ResolveIP4AddressAsync("xbox.com");
			ipAddrTask.Wait();

			IPAddress ipAddr = ipAddrTask.Result;
			Assert.IsNotNull(ipAddr);
		}
	}
}