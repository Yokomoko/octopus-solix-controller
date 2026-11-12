using System.Net;
using FluentModbus;
using Microsoft.Extensions.Options;
using Solix.Core;

namespace Solix.Worker.Battery;

public sealed class BatteryOptions
{
    public string Host { get; set; } = "";
    public int Port { get; set; } = 502;
    public byte UnitId { get; set; } = 1;
}

/// <summary>Modbus TCP access to the Solarbank 4 E5000 Pro, using the register map from Anker's HA integration.</summary>
public sealed class SolixModbus(IOptions<BatteryOptions> options, ILogger<SolixModbus> logger) : IDisposable
{
    public const ushort ThirdPartyMode = 3;
    public const ushort CustomMode = 4;

    private const int BatteryStatusReg = 10001;
    private const int BatteryPowerReg = 10008;
    private const int SocReg = 10014;
    private const int MaxDischargeReg = 10038;
    private const int AcOutputReg = 10208;
    private const int ModeCapabilityReg = 0x8006;
    private const int ModeReg = 10064;
    private const int SetpointReg = 10071;

    private readonly BatteryOptions _options = options.Value;
    private readonly ModbusTcpClient _client = new() { ReadTimeout = 3000, WriteTimeout = 3000, ConnectTimeout = 5000 };

    // The suspend handler runs on another thread, so requests are serialised.
    private readonly Lock _gate = new();

    public BatteryState Read()
    {
        lock (_gate)
        {
            Connect();
            var acOutput = _client.ReadInputRegisters<int>(_options.UnitId, AcOutputReg, 1)[0];
            var batteryPower = _client.ReadInputRegisters<int>(_options.UnitId, BatteryPowerReg, 1)[0];
            var soc = _client.ReadInputRegisters<ushort>(_options.UnitId, SocReg, 1)[0];
            var mode = _client.ReadHoldingRegisters<ushort>(_options.UnitId, ModeReg, 1)[0];
            return new BatteryState(acOutput, batteryPower, soc, mode);
        }
    }

    public void LogCapabilities()
    {
        lock (_gate) LogCapabilitiesCore();
    }

    private void LogCapabilitiesCore()
    {
        Connect();
        var modes = _client.ReadInputRegisters<ushort>(_options.UnitId, ModeCapabilityReg, 1)[0];
        var maxDischarge = _client.ReadInputRegisters<int>(_options.UnitId, MaxDischargeReg, 1)[0];
        var status = _client.ReadInputRegisters<ushort>(_options.UnitId, BatteryStatusReg, 1)[0];
        logger.LogInformation(
            "Battery: mode mask 0x{Mask:X4} (third-party supported: {ThirdParty}), max discharge {MaxW} W, status {Status}",
            modes, (modes & (1 << ThirdPartyMode)) != 0, maxDischarge, status);
    }

    public void SetMode(ushort mode)
    {
        lock (_gate)
        {
            Connect();
            _client.WriteSingleRegister(_options.UnitId, ModeReg, mode);
        }
    }

    public void SetSetpoint(int watts)
    {
        lock (_gate)
        {
            Connect();
            _client.WriteMultipleRegisters(_options.UnitId, SetpointReg, new[] { watts });
        }
    }

    public void Reset()
    {
        lock (_gate)
        {
            try { _client.Disconnect(); } catch (Exception) { }
        }
    }

    private void Connect()
    {
        if (_client.IsConnected) return;
        if (!IPAddress.TryParse(_options.Host, out _))
            throw new InvalidOperationException($"Battery:Host '{_options.Host}' isn't an IP address. Set it to the battery's fixed IP.");
        _client.Connect(new IPEndPoint(IPAddress.Parse(_options.Host), _options.Port), ModbusEndianness.BigEndian);
    }

    public void Dispose() => Reset();
}
