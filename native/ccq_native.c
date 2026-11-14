#include "ccq_native.h"

/* See ccq_native.h for the contract. */
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
