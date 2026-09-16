/* Delivery policy for the source lease FIFO (problem H-3, 2026-09-11).
 *
 * acquire() reports the number of undelivered frames of the current
 * generation (n). The policy keeps latency bounded while absorbing the
 * "two arrivals inside one compose tick" case:
 *   n == 0 -> no lease (NotReady)
 *   n <= 2 -> lease the oldest (backlog stays <= 1). When n == 2 and the
 *             oldest arrival is older than 21 ms (1.25 frames at 60 fps),
 *             discard that oldest one first, so steady clock drift does not
 *             grow the backlog (problem H-3; replaces the former
 *             30-consecutive-calls rule).
 *   n > 2  -> discard the oldest n-2, then lease the older of the two
 *             remaining (catch up to the newest in one tick)
 *
 * The plan is pure so the native shim test can fix each rule without media.
 * License: MIT (same as the shim). */
#ifndef TCS_DELIVERY_POLICY_H
#define TCS_DELIVERY_POLICY_H

#include <stdint.h>

/* 1.25 frames at 60 fps. The caller converts this to QPC ticks. */
#define TCS_DELIVERY_AGE_LIMIT_US 21000

typedef struct TcsDeliveryPlan {
  uint32_t drop_oldest;  /* frames to discard (counted as replaced) */
  int32_t lease;         /* 1 = lease the oldest remaining frame */
} TcsDeliveryPlan;

static inline TcsDeliveryPlan
tcs_delivery_plan (uint32_t n, uint64_t oldest_age_ticks, uint64_t age_limit_ticks)
{
  TcsDeliveryPlan plan = { 0, 0 };
  if (n == 0)
    return plan;
  if (n == 2) {
    if (age_limit_ticks > 0 && oldest_age_ticks >= age_limit_ticks)
      plan.drop_oldest = 1;
  } else if (n > 2) {
    plan.drop_oldest = n - 2;
  }
  plan.lease = 1;
  return plan;
}

/* ---- shared ring slot policy (stage 6b, pure) ----
 * The GPU path keeps at most 3 frames in flight in the shared texture ring:
 * the current lease (at most 1) and the undelivered FIFO (bounded by the
 * delivery plan above). Slot allocation and eviction follow the same
 * latest-first rule as the FIFO. */

/* Pick the first ring slot whose `used[i]` is 0. -1 = all used: the caller
 * must evict the oldest undelivered GPU item, then retry. */
static inline int32_t
tcs_ring_pick_slot (const uint8_t* used, uint32_t slots)
{
  if (!used)
    return -1;
  for (uint32_t i = 0; i < slots; i++)
    if (!used[i])
      return (int32_t) i;
  return -1;
}

/* D8: mark a ring slot as occupied only when the item belongs to the current
 * ring epoch. Items from an older epoch referenced the ring the shim already
 * destroyed (the compositor keeps its own opened resources alive), so they
 * must not block slots of the rebuilt ring. */
static inline void
tcs_ring_mark_occupied (uint8_t* used, uint32_t slots, int32_t slot,
                        uint32_t item_epoch, uint32_t current_epoch)
{
  if (!used || slot < 0 || (uint32_t) slot >= slots)
    return;
  if (item_epoch != current_epoch || current_epoch == 0)
    return;
  used[slot] = 1;
}

/* Index (oldest first) of the oldest undelivered item that occupies a ring
 * slot (item_slots[i] >= 0), or -1 when the queue has no GPU item. */
static inline int32_t
tcs_ring_evict_index (const int32_t* item_slots, uint32_t n)
{
  if (!item_slots)
    return -1;
  for (uint32_t i = 0; i < n; i++)
    if (item_slots[i] >= 0)
      return (int32_t) i;
  return -1;
}

#endif /* TCS_DELIVERY_POLICY_H */
