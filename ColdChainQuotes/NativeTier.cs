using System.Runtime.InteropServices;

namespace ColdChainQuotes;

public enum DistanceTier { Local = 0, Regional = 1, LongHaul = 2 }

// Adapter for ccq_distance_tier in native/ccq_native.c (ccq_native.dll, win-x64, cdecl).
// Validates the domain value before the native call and turns load or binding failures into Result failures.
public static class NativeTier
{
    [DllImport("ccq_native", EntryPoint = "ccq_distance_tier", CallingConvention = CallingConvention.Cdecl)]
    static extern int ccq_distance_tier(int distanceKm);

    public static Result<DistanceTier> Classify(decimal distanceKm) =>
        Classify(distanceKm, ccq_distance_tier);

    // The call is injectable so the failure paths can be tested without unloading a loaded library.
    public static Result<DistanceTier> Classify(decimal distanceKm, Func<int, int> nativeCall)
    {
        if (distanceKm != decimal.Truncate(distanceKm) || distanceKm < 0 || distanceKm > 300)
            return Result<DistanceTier>.Fail(new("invalid_distance", $"Distance {distanceKm} km must be a whole number from 0 to 300."));

        try
        {
            return nativeCall((int)distanceKm) switch
            {
                0 => Result<DistanceTier>.Ok(DistanceTier.Local),
                1 => Result<DistanceTier>.Ok(DistanceTier.Regional),
                2 => Result<DistanceTier>.Ok(DistanceTier.LongHaul),
                -1 => Result<DistanceTier>.Fail(new("native_rejected", $"Native library rejected distance {distanceKm} km.")),
                var other => Result<DistanceTier>.Fail(new("native_unexpected", $"Native library returned unknown tier {other}."))
            };
        }
        catch (DllNotFoundException ex)
        {
            return Result<DistanceTier>.Fail(new("native_load_failed", $"Cannot load ccq_native.dll: {ex.Message}"));
        }
        catch (BadImageFormatException ex)
        {
            return Result<DistanceTier>.Fail(new("native_load_failed", $"ccq_native.dll does not match this process (win-x64 required): {ex.Message}"));
        }
        catch (EntryPointNotFoundException ex)
        {
            return Result<DistanceTier>.Fail(new("native_binding_failed", $"ccq_distance_tier is missing from ccq_native.dll: {ex.Message}"));
        }
    }
}
