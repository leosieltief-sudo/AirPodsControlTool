using System.Runtime.InteropServices.WindowsRuntime;
using AirPodsControl.Core.Protocol;
using AirPodsControl.Core.Services;
using Windows.Devices.Bluetooth.Advertisement;

namespace AirPodsControl.Windows;

/// <summary>
/// Passive BLE scan for Apple proximity-pairing advertisements (battery in 10 % steps, charging, lid, in-ear).
/// Passive scanning sends nothing over the air and does not interfere with the A2DP/HFP audio link.
/// </summary>
public sealed class BleAdvertisementSource : IDisposable
{
    private readonly ILog _log;
    private BluetoothLEAdvertisementWatcher? _watcher;

    public BleAdvertisementSource(ILog log) => _log = log;

    public event Action<ProximityPairingMessage, short>? Received;
    public bool IsRunning => _watcher?.Status == BluetoothLEAdvertisementWatcherStatus.Started;
    public string? LastError { get; private set; }

    public void Start()
    {
        if (_watcher is not null) return;
        try
        {
            var w = new BluetoothLEAdvertisementWatcher { ScanningMode = BluetoothLEScanningMode.Passive };
            w.AdvertisementFilter.Advertisement.ManufacturerData.Add(
                new BluetoothLEManufacturerData { CompanyId = ProximityPairingMessage.AppleCompanyId });
            w.Received += OnReceived;
            w.Stopped += (_, e) =>
            {
                if (e.Error != global::Windows.Devices.Bluetooth.BluetoothError.Success)
                {
                    LastError = e.Error.ToString();
                    _log.Warn($"BLE watcher stopped: {e.Error}");
                }
            };
            w.Start();
            _watcher = w;
            LastError = null;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            _log.Warn("BLE advertisement watcher could not start", ex);
        }
    }

    public void Stop()
    {
        if (_watcher is null) return;
        try { _watcher.Received -= OnReceived; _watcher.Stop(); } catch { }
        _watcher = null;
    }

    private void OnReceived(BluetoothLEAdvertisementWatcher sender, BluetoothLEAdvertisementReceivedEventArgs args)
    {
        try
        {
            foreach (var md in args.Advertisement.ManufacturerData)
            {
                if (md.CompanyId != ProximityPairingMessage.AppleCompanyId) continue;
                var bytes = md.Data.ToArray();
                // Apple packs several TLVs ([type][len][payload]) into one manufacturer data block; find type 0x07.
                int i = 0;
                while (i + 1 < bytes.Length)
                {
                    int len = bytes[i + 1];
                    if (i + 2 + len > bytes.Length) break;
                    if (bytes[i] == ProximityPairingMessage.ProximityPairingType)
                    {
                        var msg = ProximityPairingMessage.TryParse(bytes.AsSpan(i, len + 2));
                        if (msg is not null) Received?.Invoke(msg, args.RawSignalStrengthInDBm);
                    }
                    i += 2 + len;
                }
            }
        }
        catch (Exception ex) { _log.Debug("BLE parse error: " + ex.Message); }
    }

    public void Dispose() => Stop();
}
