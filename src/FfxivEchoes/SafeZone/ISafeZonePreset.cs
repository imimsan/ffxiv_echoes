using FfxivEchoes.Triggers.Models;

namespace FfxivEchoes.SafeZone;

public interface ISafeZonePreset
{
    string Method { get; }
    SafeZoneResult? Calculate(SafeZoneCalculation calc, SafeZoneContext context);
}
