using System.Net.Sockets;
using NModbus.IO;

namespace FOTEK_NT_SETTING_APP.Services;

/// <summary>
/// 將 TcpClient 的 NetworkStream 包成 NModbus IStreamResource，
/// 供 CreateRtuMaster 使用（透明串列閘道：TCP 上傳輸 RTU 幀，非 Modbus TCP/MBAP）。
/// </summary>
internal sealed class TcpClientStreamResource : IStreamResource
{
    private readonly TcpClient _client;
    private bool _disposed;

    public TcpClientStreamResource(TcpClient client)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
    }

    public int InfiniteTimeout => Timeout.Infinite;

    public int ReadTimeout
    {
        get => Stream.ReadTimeout;
        set => Stream.ReadTimeout = value;
    }

    public int WriteTimeout
    {
        get => Stream.WriteTimeout;
        set => Stream.WriteTimeout = value;
    }

    private NetworkStream Stream
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_client.Connected)
                throw new InvalidOperationException("TCP 連線已中斷。");
            return _client.GetStream();
        }
    }

    public void DiscardInBuffer()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_client.Connected)
            return;

        var stream = _client.GetStream();
        var buffer = new byte[256];
        while (_client.Available > 0)
        {
            var toRead = Math.Min(buffer.Length, _client.Available);
            if (toRead <= 0)
                break;
            _ = stream.Read(buffer, 0, toRead);
        }
    }

    public int Read(byte[] buffer, int offset, int count)
        => Stream.Read(buffer, offset, count);

    public void Write(byte[] buffer, int offset, int count)
        => Stream.Write(buffer, offset, count);

    public void Dispose()
    {
        _disposed = true;
        // TcpClient 生命週期由 ModbusClientService 管理，此處不關閉連線。
    }
}
