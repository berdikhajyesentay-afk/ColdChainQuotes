#ifndef CCQ_NATIVE_H
#define CCQ_NATIVE_H

#include <stdint.h>

#ifdef _WIN32
#define CCQ_EXPORT __declspec(dllexport)
#else
#define CCQ_EXPORT __attribute__((visibility("default")))
#endif

/* Classifies a whole-kilometre distance into a dispatch tier.
   Valid input: 0..300 km. Returns 0 = Local (0-50), 1 = Regional (51-150),
   2 = LongHaul (151-300), and -1 when the distance is outside 0..300.
   Calling convention: cdecl. Allocates no memory and keeps no state. */
CCQ_EXPORT int32_t ccq_distance_tier(int32_t distance_km);

#endif
