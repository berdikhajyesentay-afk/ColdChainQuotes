#include <stdint.h>

#ifdef _WIN32
#define CCQ_EXPORT __declspec(dllexport)
#else
#define CCQ_EXPORT __attribute__((visibility("default")))
#endif

/* Classifies a whole-kilometre distance into a dispatch tier.
   Valid input: 0..300 km. Returns 0 = Local (0-50), 1 = Regional (51-150),
   2 = LongHaul (151-300), and -1 when the distance is outside 0..300.
   Uses fixed-width types only and allocates no memory. */
CCQ_EXPORT int32_t ccq_distance_tier(int32_t distance_km)
{
    if (distance_km < 0 || distance_km > 300)
        return -1;
    if (distance_km <= 50)
        return 0;
    if (distance_km <= 150)
        return 1;
    return 2;
}
