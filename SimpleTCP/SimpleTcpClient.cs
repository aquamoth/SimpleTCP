using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace SimpleTCP
{
	public class SimpleTcpClient : IDisposable
	{
		public SimpleTcpClient()
		{
			StringEncoder = System.Text.Encoding.UTF8;
			ReadLoopIntervalMs = 10;
			Delimiter = 0x13;
		}

		private Thread _rxThread = null;
		private byte[] _queuedMsg = new byte[0];
		public byte Delimiter { get; set; }
		public System.Text.Encoding StringEncoder { get; set; }
		private TcpClient _client = null;

		public event EventHandler<Message> DelimiterDataReceived;
		public event EventHandler<Message> DataReceived;
		public event EventHandler Disconnected;

		internal bool QueueStop { get; set; }
		internal int ReadLoopIntervalMs { get; set; }
		public bool AutoTrimStrings { get; set; }

		public SimpleTcpClient Connect(string hostNameOrIpAddress, int port)
		{
			if (string.IsNullOrEmpty(hostNameOrIpAddress))
			{
				throw new ArgumentNullException("hostNameOrIpAddress");
			}

			_client = new TcpClient();
			_client.Connect(hostNameOrIpAddress, port);

			StartRxThread();

			return this;
		}

		#region Connect with timeout

		public SimpleTcpClient Connect(string hostNameOrIpAddress, int port, int connectionTimeout)
		{
			if (string.IsNullOrEmpty(hostNameOrIpAddress))
			{
				throw new ArgumentNullException("hostNameOrIpAddress");
			}

			var hostEntry = System.Net.Dns.GetHostEntry(hostNameOrIpAddress);
			var addresses = hostEntry.AddressList;

			DebugInfo("Connecting with timeout");
			var state = new ConnectState(new TcpClient());
			var result = state.Client.BeginConnect(addresses, port, EndConnect, state);
			state.CompletedWithinTimeout = result.AsyncWaitHandle.WaitOne(connectionTimeout, false);
			DebugInfo("CompletedWithinTimeout: {0}", state.CompletedWithinTimeout);

			if (!state.CompletedWithinTimeout)
				throw new TimeoutException("Failed to connect within connection timeout.");

			DebugInfo("Waiting for EndConnect to complete");
			state.Sync.WaitOne();

			DebugInfo("Checking if connection succeeded");
			if (!state.Client.Connected)
			{
				throw state.InnerException
					?? new ApplicationException("Connection failed without inner exception.");
			}

			DebugInfo("Starting RxTread");
			_client = state.Client;
			StartRxThread();

			DebugInfo("Returning this");
			return this;
		}

		void EndConnect(IAsyncResult ar)
		{
			DebugInfo("Connection callback started");
			var state = ar.AsyncState as ConnectState;
			try
			{
				state.Client.Client.EndConnect(ar);
				DebugInfo("Connection process ended");
			}
			catch (Exception ex)
			{
				DebugInfo("Ending connection failed: " + ex.Message);
				state.InnerException = ex;
			}

			if (state.Client.Connected && !state.CompletedWithinTimeout)
			{
				DebugInfo("Connection succeeded after timeout. Closing connection gracefully.");
				state.Client.Close();
			}
			else
			{
				DebugInfo("Setting the autoreset event");
				state.Sync.Set();
			}
			DebugInfo("Connection callback ended");
		}

		class ConnectState
		{
			public AutoResetEvent Sync { get; private set; }
			public TcpClient Client { get; private set; }
			public bool CompletedWithinTimeout { get; set; }
			public Exception InnerException { get; set; }

			public ConnectState(TcpClient client)
			{
				Sync = new AutoResetEvent(false);
				Client = client;
			}
		}

		#endregion Connect with timeout

		private void StartRxThread()
		{
			if (_rxThread != null) { return; }

			_rxThread = new Thread(ListenerLoop);
			_rxThread.IsBackground = true;
			_rxThread.Start();
		}

		public SimpleTcpClient Disconnect()
		{
			if (_client == null) { return this; }
			QueueStop = true;
			_client.Close();
			_client = null;
			return this;
		}

		public TcpClient TcpClient { get { return _client; } }

		private void ListenerLoop(object state)
		{
			while (!QueueStop)
			{
				try
				{
					RunLoopStep();
				}
				catch (Exception ex)
				{
					DebugInfo(ex.Message);
				}

				Thread.Sleep(ReadLoopIntervalMs);
			}

			_rxThread = null;
		}

		private void RunLoopStep()
		{
			if (_client == null)
			{
				DebugInfo("RunLoopStep() run when _client == NULL!");
				return;
			}

			if (!_client.Connected)
			{
				OnDisconnected(EventArgs.Empty);
			}
			else if (_client.Available > 0)
			{
				var buffer = new byte[_client.Available];
				_client.Client.Receive(buffer);

				notifyDelimiterMessagesRx(buffer);
				NotifyEndTransmissionRx(_client, buffer);
			}
		}

		protected virtual void OnDisconnected(EventArgs e)
		{
			QueueStop = true;
			Disconnected?.Invoke(this, e);
		}

		private void notifyDelimiterMessagesRx(byte[] buffer)
		{
			var bufferStart = 0;
			for (var i = 0; i < buffer.Length; i++)
			{
				if (buffer[i] == this.Delimiter)
				{
					var bufferPart = buffer.Skip(bufferStart).Take(i - bufferStart);
					bufferStart = i + 1;

					var message = _queuedMsg.Concat(bufferPart).ToArray();
					_queuedMsg = new byte[0];

					NotifyDelimiterMessageRx(_client, message);
				}
			}
			_queuedMsg = buffer.Skip(bufferStart).ToArray();
		}

		private void NotifyDelimiterMessageRx(TcpClient client, byte[] msg)
		{
			if (DelimiterDataReceived != null)
			{
				Message m = new Message(msg, client, StringEncoder, Delimiter, AutoTrimStrings);
				DelimiterDataReceived(this, m);
			}
		}

		private void NotifyEndTransmissionRx(TcpClient client, byte[] msg)
		{
			if (DataReceived != null)
			{
				Message m = new Message(msg, client, StringEncoder, Delimiter, AutoTrimStrings);
				DataReceived(this, m);
			}
		}

		public void Write(byte[] data)
		{
			if (_client == null) { throw new Exception("Cannot send data to a null TcpClient (check to see if Connect was called)"); }
			_client.GetStream().Write(data, 0, data.Length);
		}

		public void Write(string data)
		{
			if (data == null) { return; }
			Write(StringEncoder.GetBytes(data));
		}

		public void WriteLine(string data)
		{
			if (string.IsNullOrEmpty(data)) { return; }
			if (data.LastOrDefault() != Delimiter)
			{
				Write(data + StringEncoder.GetString(new byte[] { Delimiter }));
			}
			else
			{
				Write(data);
			}
		}

		public Message WriteLineAndGetReply(string data, TimeSpan timeout)
		{
			Message mReply = null;
			this.DataReceived += (s, e) => { mReply = e; };
			WriteLine(data);

			Stopwatch sw = new Stopwatch();
			sw.Start();

			while (mReply == null && sw.Elapsed < timeout)
			{
				System.Threading.Thread.Sleep(10);
			}

			return mReply;
		}


		#region IDisposable Support
		private bool disposedValue = false; // To detect redundant calls

		protected virtual void Dispose(bool disposing)
		{
			if (!disposedValue)
			{
				if (disposing)
				{
					// TODO: dispose managed state (managed objects).

				}

				// TODO: free unmanaged resources (unmanaged objects) and override a finalizer below.
				// TODO: set large fields to null.
				QueueStop = true;
				if (_client != null)
				{
					try
					{
						_client.Close();
					}
					catch { }
					_client = null;
				}

				disposedValue = true;
			}
		}

		// TODO: override a finalizer only if Dispose(bool disposing) above has code to free unmanaged resources.
		// ~SimpleTcpClient() {
		//   // Do not change this code. Put cleanup code in Dispose(bool disposing) above.
		//   Dispose(false);
		// }

		// This code added to correctly implement the disposable pattern.
		public void Dispose()
		{
			// Do not change this code. Put cleanup code in Dispose(bool disposing) above.
			Dispose(true);
			// TODO: uncomment the following line if the finalizer is overridden above.
			// GC.SuppressFinalize(this);
		}
		#endregion


		#region Debug logging

		[Conditional("DEBUG")]
		void DebugInfo(string format, params object[] args)
		{
			if (_debugInfoTime == null)
			{
				_debugInfoTime = new Stopwatch();
				_debugInfoTime.Start();
			}
			Debug.WriteLine(_debugInfoTime.ElapsedMilliseconds + ": " + format, args);
		}
		Stopwatch _debugInfoTime;

		#endregion Debug logging
	}
}