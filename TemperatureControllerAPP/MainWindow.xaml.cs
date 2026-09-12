using System.IO;
using System.IO.Ports;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using Microsoft.Win32;
using TemperatureControllerAPP.Models;
using TemperatureControllerAPP.Services;

namespace TemperatureControllerAPP;

public partial class MainWindow : Window
{
    private static readonly HashSet<string> CriticalWriteCodes =
        new(StringComparer.OrdinalIgnoreCase) { "Lck", "Int", "H_C", "Id", "bPS", "CT", "HYS" };

    private readonly ModbusClientService _modbus = new();
    private readonly CommLogService _log;
    private readonly DispatcherTimer _pollTimer;
    private AppSettings _appSettings = new();
    private int _decimalPoint;
    private bool _suppressControllerEvent;
    private bool _busy;
    private bool _loadingProfiles;
    private DateTime _lastParamPollUtc = DateTime.MinValue;

    private static readonly SolidColorBrush LampOff = new(Color.FromRgb(0xC9, 0xD1, 0xD9));
    private static readonly SolidColorBrush LampOnGreen = new(Color.FromRgb(0x22, 0xC5, 0x5E));
    private static readonly SolidColorBrush LampOnRed = new(Color.FromRgb(0xEF, 0x44, 0x44));
    private static readonly SolidColorBrush LampOnOrange = new(Color.FromRgb(0xF5, 0x9E, 0x0B));

    public MainWindow()
    {
        InitializeComponent();

        _log = new CommLogService(Dispatcher);
        LstCommLog.ItemsSource = _log.Entries;

        LampOff.Freeze();
        LampOnGreen.Freeze();
        LampOnRed.Freeze();
        LampOnOrange.Freeze();

        GridCol1.ItemsSource = NtRegisterMap.CreateColumn1();
        GridCol2.ItemsSource = NtRegisterMap.CreateColumn2();
        GridCol3.ItemsSource = NtRegisterMap.CreateColumn3();

        _pollTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(800) };
        _pollTimer.Tick += async (_, _) => await PollTickAsync();

        RefreshPorts();
        LoadAppSettings();
        ApplyTransportModeUi();
        SetUiConnected(false);
        _log.Info("應用程式啟動。通訊設定已載入。");
    }

    private void RefreshPorts_Click(object sender, RoutedEventArgs e) => RefreshPorts();

    private void TransportMode_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        ApplyTransportModeUi();
        SaveAppSettings();
    }

    private void ApplyTransportModeUi()
    {
        var useTcp = RbTcp.IsChecked == true;
        PanelRtu.Visibility = useTcp ? Visibility.Collapsed : Visibility.Visible;
        PanelTcp.Visibility = useTcp ? Visibility.Visible : Visibility.Collapsed;
        LblUnitId.Text = useTcp ? "Unit ID" : "Slave ID";
        if (!_modbus.IsConnected)
            SetStatus(useTcp
                ? "Modbus TCP：輸入閘道 IP（預設 Port 502），Unit ID 對應溫控器站號。"
                : "Modbus RTU：選擇 COM 埠後連線。");
    }

    private bool IsTcpMode => RbTcp.IsChecked == true;

    private void RefreshPorts()
    {
        var ports = SerialPort.GetPortNames().OrderBy(p => p).ToArray();
        var selected = CmbPort.SelectedItem as string;
        CmbPort.ItemsSource = ports;
        if (ports.Length == 0)
        {
            if (!IsTcpMode)
                SetStatus("找不到可用的 COM 埠。");
            return;
        }

        CmbPort.SelectedItem = ports.Contains(selected) ? selected : ports[0];
    }

    #region Profiles & settings

    private void LoadAppSettings()
    {
        _appSettings = ConnectionSettingsStore.Load();
        TxtDashPollMs.Text = _appSettings.DashboardPollMs.ToString();
        TxtParamPollMs.Text = _appSettings.ParameterPollMs.ToString();
        ApplyConnectionToUi(_appSettings.Current);
        RefreshProfileCombo(_appSettings.SelectedProfileName);
        ApplyPollIntervalsFromUi(save: false);
    }

    private void RefreshProfileCombo(string? selectName)
    {
        _loadingProfiles = true;
        CmbProfiles.ItemsSource = null;
        CmbProfiles.ItemsSource = _appSettings.Profiles.OrderBy(p => p.ProfileName).ToList();
        var match = _appSettings.Profiles.FirstOrDefault(p => p.ProfileName == selectName)
                    ?? _appSettings.Profiles.FirstOrDefault();
        CmbProfiles.SelectedItem = match;
        if (match is not null)
            TxtProfileName.Text = match.ProfileName;
        _loadingProfiles = false;
    }

    private void ApplyConnectionToUi(ConnectionSettings s)
    {
        RbTcp.IsChecked = s.UseTcp;
        RbRtu.IsChecked = !s.UseTcp;

        SelectComboByContent(CmbBaudRate, s.BaudRate.ToString());
        SelectComboByContent(CmbParity, s.Parity);
        SelectComboByContent(CmbDataBits, s.DataBits.ToString());
        SelectComboByContent(CmbStopBits, s.StopBits);

        TxtSlaveId.Text = s.SlaveId.ToString();
        TxtTcpHost.Text = string.IsNullOrWhiteSpace(s.TcpHost) ? "192.168.1.100" : s.TcpHost;
        TxtTcpPort.Text = s.TcpPort is >= 1 and <= 65535 ? s.TcpPort.ToString() : "502";
        TxtProfileName.Text = string.IsNullOrWhiteSpace(s.ProfileName) ? "預設" : s.ProfileName;

        if (!string.IsNullOrWhiteSpace(s.PortName))
        {
            var ports = (CmbPort.ItemsSource as IEnumerable<string>)?.ToList() ?? [];
            if (!ports.Contains(s.PortName))
            {
                ports.Insert(0, s.PortName);
                CmbPort.ItemsSource = ports;
            }
            CmbPort.SelectedItem = s.PortName;
        }
    }

    private ConnectionSettings CaptureConnectionFromUi(string? profileName = null) => new()
    {
        ProfileName = string.IsNullOrWhiteSpace(profileName)
            ? (string.IsNullOrWhiteSpace(TxtProfileName.Text) ? "預設" : TxtProfileName.Text.Trim())
            : profileName.Trim(),
        UseTcp = IsTcpMode,
        PortName = CmbPort.SelectedItem as string ?? string.Empty,
        BaudRate = TryGetComboInt(CmbBaudRate, 9600),
        Parity = GetComboContent(CmbParity) ?? "None",
        DataBits = TryGetComboInt(CmbDataBits, 8),
        StopBits = GetComboContent(CmbStopBits) ?? "1",
        SlaveId = byte.TryParse(TxtSlaveId.Text.Trim(), out var id) && id != 0 ? id : (byte)1,
        TcpHost = TxtTcpHost.Text.Trim(),
        TcpPort = int.TryParse(TxtTcpPort.Text.Trim(), out var port) ? port : 502
    };

    private void SaveAppSettings()
    {
        ApplyPollIntervalsFromUi(save: false);
        _appSettings.Current = CaptureConnectionFromUi();
        _appSettings.SelectedProfileName = _appSettings.Current.ProfileName;
        ConnectionSettingsStore.Save(_appSettings);
    }

    private void ApplyPollIntervalsFromUi(bool save)
    {
        if (!int.TryParse(TxtDashPollMs.Text.Trim(), out var dash) || dash < 200)
            dash = 800;
        if (!int.TryParse(TxtParamPollMs.Text.Trim(), out var param) || param < dash)
            param = Math.Max(dash * 3, 3000);

        _appSettings.DashboardPollMs = dash;
        _appSettings.ParameterPollMs = param;
        TxtDashPollMs.Text = dash.ToString();
        TxtParamPollMs.Text = param.ToString();
        _pollTimer.Interval = TimeSpan.FromMilliseconds(dash);
        if (save)
            ConnectionSettingsStore.Save(_appSettings);
    }

    private void Profiles_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingProfiles || CmbProfiles.SelectedItem is not ConnectionSettings profile)
            return;
        ApplyConnectionToUi(profile);
        ApplyTransportModeUi();
        _log.Info($"已選擇設定檔：{profile.ProfileName}");
    }

    private void ApplyProfile_Click(object sender, RoutedEventArgs e)
    {
        if (CmbProfiles.SelectedItem is not ConnectionSettings profile)
        {
            MessageBox.Show("請先選擇設定檔。", "裝置設定檔", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        ApplyConnectionToUi(profile);
        ApplyTransportModeUi();
        SaveAppSettings();
        _log.Info($"已套用設定檔：{profile.ProfileName}");
        SetStatus($"已套用設定檔「{profile.ProfileName}」。");
    }

    private void SaveProfile_Click(object sender, RoutedEventArgs e)
    {
        var name = string.IsNullOrWhiteSpace(TxtProfileName.Text) ? "預設" : TxtProfileName.Text.Trim();
        UpsertProfile(name);
        _log.Info($"已儲存設定檔：{name}");
        SetStatus($"設定檔「{name}」已儲存。");
    }

    private void SaveProfileAs_Click(object sender, RoutedEventArgs e)
    {
        var name = string.IsNullOrWhiteSpace(TxtProfileName.Text) ? $"設定_{DateTime.Now:HHmmss}" : TxtProfileName.Text.Trim();
        if (_appSettings.Profiles.Any(p => p.ProfileName.Equals(name, StringComparison.OrdinalIgnoreCase)))
        {
            MessageBox.Show("同名設定檔已存在，請改名後再「另存」。", "另存設定檔", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        UpsertProfile(name);
        _log.Info($"已另存設定檔：{name}");
        SetStatus($"設定檔「{name}」已另存。");
    }

    private void DeleteProfile_Click(object sender, RoutedEventArgs e)
    {
        if (CmbProfiles.SelectedItem is not ConnectionSettings profile)
            return;

        if (_appSettings.Profiles.Count <= 1)
        {
            MessageBox.Show("至少保留一個設定檔。", "刪除", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (MessageBox.Show($"確定刪除設定檔「{profile.ProfileName}」？", "刪除",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;

        _appSettings.Profiles.RemoveAll(p => p.ProfileName == profile.ProfileName);
        RefreshProfileCombo(_appSettings.Profiles.First().ProfileName);
        ApplyConnectionToUi(_appSettings.Profiles.First());
        SaveAppSettings();
        _log.Warn($"已刪除設定檔：{profile.ProfileName}");
    }

    private void OpenSettingsFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(ConnectionSettingsStore.DirectoryPath);
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = File.Exists(ConnectionSettingsStore.FilePath)
                    ? $"/select,\"{ConnectionSettingsStore.FilePath}\""
                    : $"\"{ConnectionSettingsStore.DirectoryPath}\"",
                UseShellExecute = true
            };
            System.Diagnostics.Process.Start(psi);
            _log.Info($"已開啟設定檔資料夾：{ConnectionSettingsStore.DirectoryPath}");
        }
        catch (Exception ex)
        {
            _log.Error($"開啟設定檔資料夾失敗：{ex.Message}");
            MessageBox.Show(ex.Message, "開啟資料夾失敗", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void UpsertProfile(string name)
    {
        var snap = CaptureConnectionFromUi(name);
        var existing = _appSettings.Profiles.FirstOrDefault(p =>
            p.ProfileName.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (existing is null)
            _appSettings.Profiles.Add(snap);
        else
        {
            var idx = _appSettings.Profiles.IndexOf(existing);
            _appSettings.Profiles[idx] = snap;
        }

        _appSettings.Current = ConnectionSettingsStore.Clone(snap);
        _appSettings.SelectedProfileName = name;
        ApplyPollIntervalsFromUi(save: false);
        ConnectionSettingsStore.Save(_appSettings);
        RefreshProfileCombo(name);
    }

    #endregion

    private async void Connect_Click(object sender, RoutedEventArgs e)
    {
        if (!byte.TryParse(TxtSlaveId.Text.Trim(), out var slaveId) || slaveId is 0)
        {
            MessageBox.Show("Slave / Unit ID 必須為 1–255。", "連線", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        ApplyPollIntervalsFromUi(save: false);

        try
        {
            SetBusy(true);
            string statusText;

            if (IsTcpMode)
            {
                var host = TxtTcpHost.Text.Trim();
                if (string.IsNullOrWhiteSpace(host))
                {
                    MessageBox.Show("請輸入閘道 IP / Host。", "連線", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                if (!int.TryParse(TxtTcpPort.Text.Trim(), out var tcpPort) || tcpPort is < 1 or > 65535)
                {
                    MessageBox.Show("TCP Port 必須為 1–65535（預設 502）。", "連線", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                _log.Info($"連線 Modbus TCP {host}:{tcpPort}, UnitId={slaveId} …");
                await Task.Run(() => _modbus.ConnectTcp(host, tcpPort, slaveId));
                statusText = $"已連線 Modbus TCP：{host}:{tcpPort}, Unit ID={slaveId}";
            }
            else
            {
                if (CmbPort.SelectedItem is not string portName || string.IsNullOrWhiteSpace(portName))
                {
                    MessageBox.Show("請先選擇 COM Port。", "連線", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                var baud = int.Parse(((ComboBoxItem)CmbBaudRate.SelectedItem).Content.ToString()!);
                var parity = ParseParity(((ComboBoxItem)CmbParity.SelectedItem).Content.ToString()!);
                var dataBits = int.Parse(((ComboBoxItem)CmbDataBits.SelectedItem).Content.ToString()!);
                var stopBits = ParseStopBits(((ComboBoxItem)CmbStopBits.SelectedItem).Content.ToString()!);

                _log.Info($"連線 Modbus RTU {portName} @ {baud}, Slave={slaveId} …");
                await Task.Run(() => _modbus.ConnectRtu(portName, baud, parity, dataBits, stopBits, slaveId));
                statusText = $"已連線 Modbus RTU：{portName} @ {baud}, Slave={slaveId}";
            }

            SetUiConnected(true);
            SetStatus(statusText + "  ·  正在讀取裝置資料…");
            TxtConnectionState.Text = IsTcpMode ? "已連線 (TCP)" : "已連線 (RTU)";
            TxtConnectionState.Foreground = new SolidColorBrush(Color.FromRgb(0x0B, 0x6E, 0x4F));
            _log.Info(statusText);

            await InitialReadAfterConnectAsync();
            TglPolling.IsChecked = true;
            SaveAppSettings();
        }
        catch (Exception ex)
        {
            _modbus.Disconnect();
            SetUiConnected(false);
            SetStatus($"連線失敗：{ex.Message}");
            _log.Error($"連線失敗：{ex.Message}");
            SaveAppSettings();
            MessageBox.Show(ex.Message, "連線失敗", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void Disconnect_Click(object sender, RoutedEventArgs e)
    {
        _pollTimer.Stop();
        TglPolling.IsChecked = false;
        _modbus.Disconnect();
        SetUiConnected(false);
        TxtConnectionState.Text = "未連線";
        TxtConnectionState.Foreground = Brushes.Black;
        SetStatus("已斷開連線。");
        _log.Info("已斷開連線。");
    }

    private void Polling_Changed(object sender, RoutedEventArgs e)
    {
        if (TglPolling.IsChecked == true)
        {
            TglPolling.Content = "輪詢 ON";
            ApplyPollIntervalsFromUi(save: true);
            if (_modbus.IsConnected)
                _pollTimer.Start();
        }
        else
        {
            TglPolling.Content = "輪詢 OFF";
            _pollTimer.Stop();
        }
    }

    private async Task InitialReadAfterConnectAsync()
    {
        await ReadDashboardCoreAsync();
        await RefreshAllParametersAsync();
        _lastParamPollUtc = DateTime.UtcNow;

        if (!string.IsNullOrWhiteSpace(TxtSvDisplay.Text) && TxtSvDisplay.Text != "—")
            TxtSvInput.Text = TxtSvDisplay.Text;

        SetStatus($"連線後初次讀取完成 {DateTime.Now:HH:mm:ss}  ·  dP={_decimalPoint}");
        _log.Info($"初次讀取完成（含參數表批次讀取），dP={_decimalPoint}");
    }

    private async Task PollTickAsync()
    {
        if (!_modbus.IsConnected || _busy)
            return;

        try
        {
            _busy = true;
            await ReadDashboardCoreAsync();

            var paramDue = (DateTime.UtcNow - _lastParamPollUtc).TotalMilliseconds >= _appSettings.ParameterPollMs;
            if (paramDue)
            {
                await RefreshAllParametersAsync(updateEditValue: false);
                _lastParamPollUtc = DateTime.UtcNow;
                SetStatus($"輪詢更新 {DateTime.Now:HH:mm:ss}  · Dashboard+參數  ·  dP={_decimalPoint}");
            }
            else
            {
                SetStatus($"輪詢更新 {DateTime.Now:HH:mm:ss}  · Dashboard  ·  dP={_decimalPoint}");
            }
        }
        catch (Exception ex)
        {
            SetStatus($"輪詢錯誤：{ex.Message}");
            _log.Error($"輪詢錯誤：{ex.Message}");
        }
        finally
        {
            _busy = false;
        }
    }

    private async Task ReadDashboardCoreAsync()
    {
        var addrs = new ushort[]
        {
            NtRegisterMap.DecimalPoint,
            NtRegisterMap.Pv,
            NtRegisterMap.Sv,
            NtRegisterMap.ControllerOnOff,
            NtRegisterMap.OutputAlarmStatus,
            NtRegisterMap.AlarmErrorStatus
        };

        var map = await Task.Run(() => _modbus.ReadHoldingRegistersMany(addrs));

        var dp = map[NtRegisterMap.DecimalPoint];
        var pv = map[NtRegisterMap.Pv];
        var sv = map[NtRegisterMap.Sv];
        var onOff = map[NtRegisterMap.ControllerOnOff];
        var status = map[NtRegisterMap.OutputAlarmStatus];
        var err = map[NtRegisterMap.AlarmErrorStatus];

        _decimalPoint = dp is >= 0 and <= 1 ? dp : 0;
        TxtPv.Text = NtRegisterMap.FormatSigned(pv, _decimalPoint);
        TxtSvDisplay.Text = NtRegisterMap.FormatSigned(sv, _decimalPoint);

        _suppressControllerEvent = true;
        TglController.IsChecked = onOff == 0;
        TglController.Content = onOff == 0 ? "ON（可控制輸出）" : "OFF（輸出禁用）";
        _suppressControllerEvent = false;

        SetLamp(LampOut1, (status & 0x0001) != 0, LampOnGreen);
        SetLamp(LampOut2, (status & 0x0002) != 0, LampOnGreen);
        SetLamp(LampAl1, (status & 0x0004) != 0, LampOnRed);
        SetLamp(LampAl2, (status & 0x0008) != 0, LampOnRed);

        SetLamp(LampErrFff, (err & 0x0001) != 0, LampOnOrange);
        SetLamp(LampErrDash, (err & 0x0002) != 0, LampOnOrange);
        SetLamp(LampErrHtEr, (err & 0x0004) != 0, LampOnRed);
        SetLamp(LampErrOhEr, (err & 0x0008) != 0, LampOnRed);
    }

    private async Task RefreshAllParametersAsync(bool updateEditValue = true)
    {
        var parameters = EnumerateAllParameters().ToList();
        if (parameters.Count == 0) return;

        var addresses = parameters.Select(p => p.Address).ToArray();
        Dictionary<ushort, ushort> map;
        try
        {
            map = await Task.Run(() => _modbus.ReadHoldingRegistersMany(addresses));
        }
        catch (Exception ex)
        {
            _log.Error($"參數批次讀取失敗：{ex.Message}");
            throw;
        }

        foreach (var param in parameters)
        {
            if (!map.TryGetValue(param.Address, out var raw))
                continue;
            var formatted = NtRegisterMap.FormatRaw(raw, param, _decimalPoint);
            param.CurrentValue = formatted;
            if (updateEditValue)
                param.EditValue = ExtractNumericEdit(formatted, raw, param);
        }
    }

    private async void ReadSv_Click(object sender, RoutedEventArgs e)
    {
        if (!EnsureConnected()) return;
        try
        {
            SetBusy(true);
            await RefreshDecimalPointAsync();
            var raw = await Task.Run(() => _modbus.ReadHoldingRegister(NtRegisterMap.Sv));
            var text = NtRegisterMap.FormatSigned(raw, _decimalPoint);
            TxtSvDisplay.Text = text;
            TxtSvInput.Text = text;
            SetStatus($"已讀取 SV ({NtRegisterMap.Sv:X4}h) = {text}");
            _log.Info($"讀取 SV @0x{NtRegisterMap.Sv:X4} = {text}");
        }
        catch (Exception ex)
        {
            _log.Error($"讀取 SV 失敗：{ex.Message}");
            MessageBox.Show(ex.Message, "讀取 SV 失敗", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void WriteSv_Click(object sender, RoutedEventArgs e)
    {
        if (!EnsureConnected()) return;
        await RefreshDecimalPointAsync();

        var dummy = new RegisterParameter
        {
            Address = NtRegisterMap.Sv,
            Code = "SV",
            IsSigned = true,
            DisplayKind = ValueDisplayKind.DecimalPointAware
        };

        if (!NtRegisterMap.TryParseWriteValue(TxtSvInput.Text, dummy, _decimalPoint, out var value))
        {
            MessageBox.Show("SV 數值格式不正確。範圍 -0999 ~ 9999。", "寫入 SV", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        try
        {
            SetBusy(true);
            var verified = await Task.Run(() => _modbus.WriteAndVerify(NtRegisterMap.Sv, value));
            var text = NtRegisterMap.FormatSigned(verified, _decimalPoint);
            TxtSvDisplay.Text = text;
            var ok = verified == value;
            SetStatus(ok ? $"已寫入並回讀確認 SV = {text}" : $"SV 寫入後回讀不一致：期望 {value}，實際 {verified}");
            if (ok) _log.Info($"寫入 SV 成功並回讀確認 = {text}");
            else _log.Warn($"SV 回讀不一致：寫入 {value}，回讀 {verified}");
        }
        catch (Exception ex)
        {
            _log.Error($"寫入 SV 失敗：{ex.Message}");
            MessageBox.Show(ex.Message, "寫入 SV 失敗", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void ControllerToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppressControllerEvent || !_modbus.IsConnected)
            return;

        ushort value = TglController.IsChecked == true ? (ushort)0 : (ushort)1;
        try
        {
            SetBusy(true);
            var verified = await Task.Run(() => _modbus.WriteAndVerify(NtRegisterMap.ControllerOnOff, value));
            TglController.Content = verified == 0 ? "ON（可控制輸出）" : "OFF（輸出禁用）";
            if (verified != value)
            {
                _suppressControllerEvent = true;
                TglController.IsChecked = verified == 0;
                _suppressControllerEvent = false;
                _log.Warn($"ON/OFF 回讀不一致：寫入 {value}，回讀 {verified}");
            }
            else
            {
                _log.Info($"Controller {(verified == 0 ? "ON" : "OFF")} 寫入並回讀確認");
            }

            SetStatus($"Controller {(verified == 0 ? "ON" : "OFF")}（寫入 {value}，回讀 {verified}）");
        }
        catch (Exception ex)
        {
            _suppressControllerEvent = true;
            TglController.IsChecked = !TglController.IsChecked;
            _suppressControllerEvent = false;
            _log.Error($"寫入 ON/OFF 失敗：{ex.Message}");
            MessageBox.Show(ex.Message, "寫入 Controller ON/OFF 失敗", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void ParamRead_Click(object sender, RoutedEventArgs e)
    {
        if (!EnsureConnected()) return;
        if (sender is not Button { Tag: RegisterParameter param })
            return;

        try
        {
            SetBusy(true);
            await RefreshDecimalPointAsync();
            var raw = await Task.Run(() => _modbus.ReadHoldingRegister(param.Address));
            param.CurrentValue = NtRegisterMap.FormatRaw(raw, param, _decimalPoint);
            param.EditValue = ExtractNumericEdit(param.CurrentValue, raw, param);
            SetStatus($"讀取 {param.Code} ({param.HexAddress}) = {param.CurrentValue}");
            _log.Info($"讀取 {param.Code} @ {param.HexAddress} = {param.CurrentValue}");
        }
        catch (Exception ex)
        {
            _log.Error($"讀取 {param.Code} 失敗：{ex.Message}");
            MessageBox.Show(ex.Message, $"讀取 {param.Code} 失敗", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void ParamWrite_Click(object sender, RoutedEventArgs e)
    {
        if (!EnsureConnected()) return;
        if (sender is not Button { Tag: RegisterParameter param })
            return;

        if (!param.CanWrite)
        {
            MessageBox.Show("此參數為唯讀。", "寫入", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        await RefreshDecimalPointAsync();
        if (!NtRegisterMap.TryParseWriteValue(param.EditValue, param, _decimalPoint, out var value))
        {
            MessageBox.Show($"無法解析寫入值。\n{param.RangeOrOptions}", $"寫入 {param.Code}",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (RequiresWriteConfirm(param))
        {
            var confirm = MessageBox.Show(
                $"即將寫入關鍵參數：\n\n{param.Code} ({param.HexAddress})\n值 = {value}\n\n{param.RangeOrOptions}\n\n寫入後會回讀確認。確定？",
                "寫入確認",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);
            if (confirm != MessageBoxResult.Yes)
                return;
        }

        try
        {
            SetBusy(true);
            var verified = await Task.Run(() => _modbus.WriteAndVerify(param.Address, value));
            param.CurrentValue = NtRegisterMap.FormatRaw(verified, param, _decimalPoint);
            param.EditValue = ExtractNumericEdit(param.CurrentValue, verified, param);

            if (verified == value)
            {
                SetStatus($"寫入 {param.Code} 成功並回讀確認 = {param.CurrentValue}");
                _log.Info($"寫入 {param.Code} @ {param.HexAddress} = {value}（回讀 OK）");
            }
            else
            {
                SetStatus($"寫入 {param.Code} 後回讀不一致：期望 {value}，實際 {verified}");
                _log.Warn($"寫入 {param.Code} 回讀不一致：寫入 {value}，回讀 {verified}");
                MessageBox.Show($"回讀值與寫入值不一致。\n寫入：{value}\n回讀：{verified}",
                    $"寫入 {param.Code}", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        catch (Exception ex)
        {
            _log.Error($"寫入 {param.Code} 失敗：{ex.Message}");
            MessageBox.Show(ex.Message, $"寫入 {param.Code} 失敗", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private static bool RequiresWriteConfirm(RegisterParameter param)
        => param.IsSiteDefault || CriticalWriteCodes.Contains(param.Code);

    private async Task RefreshDecimalPointAsync()
    {
        if (!_modbus.IsConnected) return;
        try
        {
            _decimalPoint = await Task.Run(() => _modbus.ReadHoldingRegister(NtRegisterMap.DecimalPoint));
            if (_decimalPoint is < 0 or > 1)
                _decimalPoint = 0;
        }
        catch (Exception ex)
        {
            _log.Warn($"讀取 dP 失敗，沿用上次值：{ex.Message}");
        }
    }

    private async void ApplySiteDefaults_Click(object sender, RoutedEventArgs e)
    {
        if (!EnsureConnected()) return;

        var summary = string.Join("\n", NtRegisterMap.SiteDefaults.Select(d => $"  {d.Code} = {d.Value}"));
        var confirm = MessageBox.Show(
            $"將寫入站點預設參數（通常設定一次即可）：\n\n{summary}\n\n寫入後會逐筆回讀確認。\n確定寫入？",
            "套用站點預設",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (confirm != MessageBoxResult.Yes)
            return;

        try
        {
            SetBusy(true);
            var results = new List<(string Code, ushort Address, ushort Written, ushort Verified, bool Ok)>();

            await Task.Run(() =>
            {
                foreach (var (address, value, code) in NtRegisterMap.SiteDefaults)
                {
                    var verified = _modbus.WriteAndVerify(address, value);
                    results.Add((code, address, value, verified, verified == value));
                }
            });

            // 以回讀值更新參數表
            foreach (var r in results)
            {
                var item = EnumerateAllParameters().FirstOrDefault(p => p.Address == r.Address);
                if (item is null) continue;
                item.CurrentValue = NtRegisterMap.FormatRaw(r.Verified, item, _decimalPoint);
                item.EditValue = r.Verified.ToString();
            }

            var lines = results.Select(r =>
                r.Ok
                    ? $"✓ {r.Code} (0x{r.Address:X4})  寫入 {r.Written} → 回讀 {r.Verified}  OK"
                    : $"✗ {r.Code} (0x{r.Address:X4})  寫入 {r.Written} → 回讀 {r.Verified}  不一致");
            var detail = string.Join("\n", lines);
            var allOk = results.All(r => r.Ok);
            var okCount = results.Count(r => r.Ok);

            foreach (var r in results)
            {
                if (r.Ok)
                    _log.Info($"站點預設 {r.Code}: 寫入 {r.Written}，回讀 OK");
                else
                    _log.Warn($"站點預設 {r.Code}: 寫入 {r.Written}，回讀 {r.Verified}（不一致）");
            }

            if (allOk)
            {
                SetStatus($"站點預設套用成功（{okCount}/{results.Count} 全部回讀確認）");
                _log.Info($"站點預設全部成功：{okCount}/{results.Count}");
                MessageBox.Show(
                    $"站點預設已套用成功。\n\n回讀結果：\n{detail}",
                    "套用站點預設 — 成功",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
            else
            {
                SetStatus($"站點預設部分失敗（{okCount}/{results.Count} 成功）");
                _log.Warn($"站點預設部分失敗：{okCount}/{results.Count}");
                MessageBox.Show(
                    $"站點預設寫入完成，但有回讀不一致。\n\n回讀結果：\n{detail}\n\n請確認裝置連線或暫存器是否可寫。",
                    "套用站點預設 — 部分失敗",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
        }
        catch (Exception ex)
        {
            SetStatus($"套用站點預設失敗：{ex.Message}");
            _log.Error($"套用站點預設失敗：{ex.Message}");
            MessageBox.Show(
                $"套用站點預設失敗。\n\n{ex.Message}",
                "套用站點預設 — 失敗",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void SyncSiteDefaultsToGrids()
    {
        foreach (var item in EnumerateAllParameters())
        {
            if (!item.IsSiteDefault || item.PreferredDefault is not ushort def)
                continue;

            item.CurrentValue = NtRegisterMap.FormatRaw(def, item, _decimalPoint);
            item.EditValue = def.ToString();
        }
    }

    private void ClearLog_Click(object sender, RoutedEventArgs e) => _log.Clear();

    private void ExportLog_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new SaveFileDialog
        {
            Filter = "文字檔 (*.txt)|*.txt|所有檔案 (*.*)|*.*",
            FileName = $"modbus-log-{DateTime.Now:yyyyMMdd-HHmmss}.txt"
        };
        if (dlg.ShowDialog() != true)
            return;

        try
        {
            File.WriteAllText(dlg.FileName, _log.ExportText());
            SetStatus($"Log 已匯出：{dlg.FileName}");
            _log.Info($"Log 已匯出：{dlg.FileName}");
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "匯出失敗", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private IEnumerable<RegisterParameter> EnumerateAllParameters()
    {
        foreach (var grid in new[] { GridCol1, GridCol2, GridCol3 })
        {
            if (grid.ItemsSource is not System.Collections.IEnumerable items)
                continue;
            foreach (var x in items.OfType<RegisterParameter>())
                yield return x;
        }
    }

    private static string ExtractNumericEdit(string display, ushort raw, RegisterParameter param)
    {
        if (param.DisplayKind == ValueDisplayKind.EnumMap)
            return raw.ToString();
        var token = display.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        return token ?? raw.ToString();
    }

    private static void SetLamp(Ellipse lamp, bool on, SolidColorBrush onBrush)
        => lamp.Fill = on ? onBrush : LampOff;

    private bool EnsureConnected()
    {
        if (_modbus.IsConnected) return true;
        MessageBox.Show("請先建立 Modbus 連線（RTU 或 TCP）。", "未連線", MessageBoxButton.OK, MessageBoxImage.Warning);
        return false;
    }

    private void SetUiConnected(bool connected)
    {
        BtnConnect.IsEnabled = !connected;
        BtnDisconnect.IsEnabled = connected;
        TglPolling.IsEnabled = connected;
        RbRtu.IsEnabled = !connected;
        RbTcp.IsEnabled = !connected;
        TxtSlaveId.IsEnabled = !connected;
        CmbPort.IsEnabled = !connected;
        CmbBaudRate.IsEnabled = !connected;
        CmbParity.IsEnabled = !connected;
        CmbDataBits.IsEnabled = !connected;
        CmbStopBits.IsEnabled = !connected;
        TxtTcpHost.IsEnabled = !connected;
        TxtTcpPort.IsEnabled = !connected;
        BtnApplySiteDefaults.IsEnabled = connected;
        CmbProfiles.IsEnabled = !connected;
        TxtProfileName.IsEnabled = !connected;
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        Cursor = busy ? System.Windows.Input.Cursors.Wait : System.Windows.Input.Cursors.Arrow;
    }

    private void SetStatus(string message) => TxtStatus.Text = message;

    private static void SelectComboByContent(ComboBox combo, string content)
    {
        foreach (var item in combo.Items.OfType<ComboBoxItem>())
        {
            if (string.Equals(item.Content?.ToString(), content, StringComparison.OrdinalIgnoreCase))
            {
                combo.SelectedItem = item;
                return;
            }
        }
    }

    private static string? GetComboContent(ComboBox combo)
        => (combo.SelectedItem as ComboBoxItem)?.Content?.ToString();

    private static int TryGetComboInt(ComboBox combo, int fallback)
        => int.TryParse(GetComboContent(combo), out var v) ? v : fallback;

    private static Parity ParseParity(string text) => text switch
    {
        "Odd" => Parity.Odd,
        "Even" => Parity.Even,
        _ => Parity.None
    };

    private static StopBits ParseStopBits(string text) => text switch
    {
        "2" => StopBits.Two,
        _ => StopBits.One
    };

    private void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        SaveAppSettings();
        _pollTimer.Stop();
        _modbus.Dispose();
    }
}
