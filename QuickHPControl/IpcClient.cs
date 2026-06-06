using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using System.Text;

namespace QuickHPControl;

public class IpcClient : IDisposable
{
	private const string HOST = "127.0.0.1";
	private const int PORT = 26745;
	private const int CONNECT_TIMEOUT_MS = 5000;
	private const int READ_TIMEOUT_MS = 3000;

	private TcpClient _client;
	private NetworkStream _stream;
	private StreamWriter _writer;
	private StreamReader _reader;
	private readonly object _lock = new object();

	public Action<string> LogCallback { get; set; }

	public bool IsConnected
	{
		get
		{
			lock (_lock)
			{
				try { return _client != null && _client.Connected; }
				catch { return false; }
			}
		}
	}

	private void Log(string msg) => LogCallback?.Invoke(msg);

	public bool Connect()
	{
		lock (_lock)
		{
			DisconnectInternal();
			try
			{
				_client = new TcpClient();
				IAsyncResult result = _client.BeginConnect(HOST, PORT, null, null);
				if (!result.AsyncWaitHandle.WaitOne(CONNECT_TIMEOUT_MS))
				{
					Log($"连接超时 ({CONNECT_TIMEOUT_MS}ms)");
					_client.Close();
					_client = null;
					return false;
				}

				_client.EndConnect(result);
				_stream = _client.GetStream();
				_stream.ReadTimeout = READ_TIMEOUT_MS;
				_writer = new StreamWriter(_stream, new UTF8Encoding(false)) { AutoFlush = true };
				_reader = new StreamReader(_stream, new UTF8Encoding(false));

				string response = SendCommandInternal("PING");
				if (response != "PONG")
				{
					Log("握手失败: " + (response ?? "(null)"));
					DisconnectInternal();
					return false;
				}

				Log($"已连接到 HOOK 端 {HOST}:{PORT}");
				return true;
			}
			catch (Exception ex)
			{
				Log("连接失败: " + ex.Message);
				DisconnectInternal();
				return false;
			}
		}
	}

	public void Disconnect()
	{
		lock (_lock)
		{
			DisconnectInternal();
		}
	}

	private void DisconnectInternal()
	{
		try { _reader?.Dispose(); } catch { }
		try { _writer?.Dispose(); } catch { }
		try { _stream?.Dispose(); } catch { }
		try { _client?.Close(); } catch { }
		_reader = null;
		_writer = null;
		_stream = null;
		_client = null;
	}

	public string SendCommand(string cmd)
	{
		lock (_lock)
		{
			return SendCommandInternal(cmd);
		}
	}

	private string SendCommandInternal(string cmd)
	{
		if (!IsConnected) return null;
		try
		{
			_writer.WriteLine(cmd);
			return _reader.ReadLine();
		}
		catch (Exception ex)
		{
			Log("SendCommand 异常: " + ex.Message);
			DisconnectInternal();
			return null;
		}
	}

	public int GetCurrentMode()
	{
		string resp = SendCommand("GET_MODE");
		if (resp != null && resp.StartsWith("MODE:") && int.TryParse(resp.Substring(5), out var mode))
		{
			return mode;
		}
		return -1;
	}

	public string GetCurrentModeName()
	{
		string resp = SendCommand("GET_MODE_NAME");
		if (resp != null && resp.StartsWith("NAME:"))
		{
			return resp.Substring(5);
		}
		return "Unknown";
	}

	public bool SetMode(int mode) => SendCommand("SET_MODE:" + mode) == "OK";

	public string GetLastError() => null;

	public List<int> GetSupportedModes()
	{
		List<int> list = new List<int>();
		string resp = SendCommand("GET_SUPPORTED_MODES");
		if (resp != null && resp.StartsWith("MODES:"))
		{
			foreach (string item in resp.Substring(6).Split(','))
			{
				if (int.TryParse(item, out var m))
				{
					list.Add(m);
				}
			}
		}
		return list;
	}

	public string GetStatus() => SendCommand("GET_STATUS");

	public int GetVersion()
	{
		string resp = SendCommand("GET_VERSION");
		if (resp != null && resp.StartsWith("VERSION:") && int.TryParse(resp.Substring(8), out var v))
		{
			return v;
		}
		return 0;
	}

	public bool IsSupportSmartSense() => SendCommand("GET_IS_SUPPORT_SMARTSENSE")?.StartsWith("SMARTSENSE:1") ?? false;

	public void Dispose() => Disconnect();
}
