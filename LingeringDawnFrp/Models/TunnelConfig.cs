namespace LingeringDawnFrp.Models;

public class TunnelConfig
{
    public string ProxyName { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public string LocalIp { get; set; } = string.Empty;
    public int LocalPort { get; set; }
    public int RemotePort { get; set; }
    public bool UseEncryption { get; set; }
    public bool UseCompression { get; set; }
}
