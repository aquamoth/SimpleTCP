using System;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace SimpleTCP.Tests
{
	[TestClass]
	public class ClientTests : IDisposable
	{
		readonly int _serverPort;
		readonly SimpleTcpServer _server;
		readonly string _serverAddress;

		public ClientTests()
		{
			_serverPort = 8911;
			_server = new SimpleTcpServer();
			_server.Start(_serverPort);
			_serverAddress = _server.GetListeningIPs()
				.FirstOrDefault(ip => ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
				.ToString();
		}

		public void Dispose()
		{
			if (_server.IsStarted)
				_server.Stop();
		}

		[TestMethod]
		[TestCategory("Connect Timeout")]
		public void Connect_succeeds_within_timeout()
		{
			Assert.IsTrue(ServerTests.IsTcpPortListening(_serverPort));

			var client = new SimpleTcpClient();
			client.Connect(_serverAddress, _serverPort, 5000);
		}

		[TestMethod]
		[TestCategory("Connect Timeout")]
		[ExpectedException(typeof(TimeoutException))]
		public void Connect_can_timeout()
		{
			_server.Stop();

			var client = new SimpleTcpClient();
			client.Connect(_serverAddress, _serverPort, 1);
		}

		[TestMethod]
		[TestCategory("Connect Timeout")]
		[ExpectedException(typeof(System.Net.Sockets.SocketException))]
		public void Connect_with_timeout_fails_when_server_is_closed()
		{
			_server.Stop();

			var client = new SimpleTcpClient();
			client.Connect(_serverAddress, _serverPort, 5000);
		}
	}
}
