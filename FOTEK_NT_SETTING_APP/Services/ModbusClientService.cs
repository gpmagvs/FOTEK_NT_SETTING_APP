using System.IO.Ports;
using System.Net.Sockets;
using NModbus;
using NModbus.Serial;

namespace FOTEK_NT_SETTING_APP.Services;

public enum ModbusTransportKind
{
    /// <summary>序列埠直連 Modbus RTU。</summary>
    Rtu,
    /// <summary>TCP socket 上傳輸 Modbus RTU 幀（透明閘道，非 MBAP）。</summary>
    Tcp
}

/// <summary>
/// Thread-safe Modbus master for NT Series controllers.
/// Supports Modbus RTU (serial) and RTU-over-TCP via transparent RS485↔Ethernet gateway
/// (socket 上傳輸 RTU 幀 + CRC，非 Modbus TCP / MBAP)。
/// Uses Protocol Base 0 addressing (register hex is the Modbus address).
/// </summary>
public sealed class ModbusClientService : IDisposable
{
    private readonly object _sync = new();
    private SerialPort? _port;
    private TcpClient? _tcpClient;
    private TcpClientStreamResource? _tcpStream;
    private IModbusMaster? _master;
    private ModbusTransportKind _transport;
    private bool _disposed;

    public bool IsConnected
    {
        get
        {
            lock (_sync)
            {
                if (_master is null) return false;
                return _transport switch
                {
                    ModbusTransportKind.Rtu => _port is { IsOpen: true },
                    ModbusTransportKind.Tcp => _tcpClient is { Connected: true },
                    _ => false
                };
            }
        }
    }

    public ModbusTransportKind Transport
    {
        get { lock (_sync) return _transport; }
    }

    public byte SlaveId { get; private set; } = 1;

    public void ConnectRtu(
        string portName,
        int baudRate,
        Parity parity,
        int dataBits,
        StopBits stopBits,
        byte slaveId,
        int readTimeoutMs = 1000,
        int writeTimeoutMs = 1000)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        lock (_sync)
        {
            DisconnectInternal();

            var port = new SerialPort(portName)
            {
                BaudRate = baudRate,
                Parity = parity,
                DataBits = dataBits,
                StopBits = stopBits,
                ReadTimeout = readTimeoutMs,
                WriteTimeout = writeTimeoutMs
            };

            try
            {
                port.Open();
                var factory = new ModbusFactory();
                var adapter = new SerialPortAdapter(port);
                var master = factory.CreateRtuMaster(adapter);
                master.Transport.ReadTimeout = readTimeoutMs;
                master.Transport.WriteTimeout = writeTimeoutMs;

                _port = port;
                _master = master;
                _transport = ModbusTransportKind.Rtu;
                SlaveId = slaveId;
            }
            catch
            {
                port.Dispose();
                throw;
            }
        }
    }

    /// <summary>
    /// 經 Ethernet 閘道連線：以 TCP socket 承載 Modbus RTU 幀（與 COM RTU 相同 PDU/CRC）。
    /// 適用透明串列裝置伺服器；不適用「真」Modbus TCP 閘道。
    /// Unit ID 對應 RS-485 Slave Address。
    /// </summary>
    public void ConnectTcp(
        string host,
        int port,
        byte unitId,
        int readTimeoutMs = 2000,
        int writeTimeoutMs = 2000)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (string.IsNullOrWhiteSpace(host))
            throw new ArgumentException("請輸入 IP 位址或主機名稱。", nameof(host));
        if (port is < 1 or > 65535)
            throw new ArgumentOutOfRangeException(nameof(port), "TCP Port 必須為 1–65535。");

        lock (_sync)
        {
            DisconnectInternal();

            var client = new TcpClient();
            TcpClientStreamResource? stream = null;
            try
            {
                // Connect with timeout
                var connectTask = client.ConnectAsync(host.Trim(), port);
                if (!connectTask.Wait(readTimeoutMs))
                {
                    client.Close();
                    throw new TimeoutException($"連線逾時：{host}:{port}");
                }

                connectTask.GetAwaiter().GetResult();

                stream = new TcpClientStreamResource(client);
                stream.ReadTimeout = readTimeoutMs;
                stream.WriteTimeout = writeTimeoutMs;

                var factory = new ModbusFactory();
                var master = factory.CreateRtuMaster(stream);
                master.Transport.ReadTimeout = readTimeoutMs;
                master.Transport.WriteTimeout = writeTimeoutMs;

                _tcpClient = client;
                _tcpStream = stream;
                _master = master;
                _transport = ModbusTransportKind.Tcp;
                SlaveId = unitId;
            }
            catch
            {
                try { stream?.Dispose(); } catch { /* ignore */ }
                try { client.Dispose(); } catch { /* ignore */ }
                throw;
            }
        }
    }

    public void Disconnect()
    {
        lock (_sync)
            DisconnectInternal();
    }

    public ushort ReadHoldingRegister(ushort address)
        => ReadHoldingRegisters(address, 1)[0];

    public ushort[] ReadHoldingRegisters(ushort startAddress, ushort count)
    {
        lock (_sync)
        {
            EnsureConnected();
            return _master!.ReadHoldingRegisters(SlaveId, startAddress, count);
        }
    }

    /// <summary>Batch-read many addresses; consecutive ranges are merged into fewer requests.</summary>
    public Dictionary<ushort, ushort> ReadHoldingRegistersMany(IEnumerable<ushort> addresses)
    {
        lock (_sync)
        {
            EnsureConnected();
            return RegisterBatchReader.ReadMany(
                (start, count) => _master!.ReadHoldingRegisters(SlaveId, start, count),
                addresses);
        }
    }

    public void WriteSingleRegister(ushort address, ushort value)
    {
        lock (_sync)
        {
            EnsureConnected();
            _master!.WriteSingleRegister(SlaveId, address, value);
        }
    }

    /// <summary>Write then read-back; returns actual device value.</summary>
    public ushort WriteAndVerify(ushort address, ushort value)
    {
        lock (_sync)
        {
            EnsureConnected();
            _master!.WriteSingleRegister(SlaveId, address, value);
            return _master.ReadHoldingRegisters(SlaveId, address, 1)[0];
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        Disconnect();
        _disposed = true;
    }

    private void EnsureConnected()
    {
        if (!IsConnectedUnlocked())
            throw new InvalidOperationException("尚未連線至 Modbus 裝置。");
    }

    private bool IsConnectedUnlocked()
    {
        if (_master is null) return false;
        return _transport switch
        {
            ModbusTransportKind.Rtu => _port is { IsOpen: true },
            ModbusTransportKind.Tcp => _tcpClient is { Connected: true },
            _ => false
        };
    }

    private void DisconnectInternal()
    {
        try { _master?.Dispose(); } catch { /* ignore */ }
        _master = null;

        try { _tcpStream?.Dispose(); } catch { /* ignore */ }
        _tcpStream = null;

        try
        {
            if (_port is { IsOpen: true })
                _port.Close();
        }
        catch { /* ignore */ }

        try { _port?.Dispose(); } catch { /* ignore */ }
        _port = null;

        try { _tcpClient?.Close(); } catch { /* ignore */ }
        try { _tcpClient?.Dispose(); } catch { /* ignore */ }
        _tcpClient = null;
    }
}
