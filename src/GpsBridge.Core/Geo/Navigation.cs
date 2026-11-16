namespace GpsBridge.Core.Geo;

public sealed record NavInfo(double DistanceM, double BearingDeg, string Cardinal, double? RelativeDeg, TimeSpan? Eta);

public static class Navigation
{
    /// <summary>Distanza e rotta verso un punto. RelativeDeg = di quanto girare rispetto alla rotta attuale (-180..180); Eta con velocita' &gt; 0,5 km/h.</summary>
    public static NavInfo To(double lat, double lon, double? courseDeg, double? speedKnots, double targetLat, double targetLon)
    {
        double d = GeoMath.DistanceMeters(lat, lon, targetLat, targetLon);
        double b = GeoMath.BearingDeg(lat, lon, targetLat, targetLon);
        double? rel = courseDeg is { } c ? ((b - c + 540) % 360) - 180 : null;
        TimeSpan? eta = speedKnots is { } k && k * 0.514444 > 0.14 ? TimeSpan.FromSeconds(d / (k * 0.514444)) : null;
        return new NavInfo(d, b, GeoMath.Cardinal(b), rel, eta);
    }
}
