/* Delivery policy for the source lease FIFO (problem H-2, 2026-09-11).
 *
 * acquire() reports the number of undelivered frames of the current
 * generation (n). The policy keeps latency bounded while absorbing the
 * "two arrivals inside one compose tick" case:
 *   n == 0            -> no lease (NotReady)
 *   n <= 2            -> lease the oldest (backlog stays <= 1)
 *   n > 2             -> discard the oldest n-2, then lease the older of the
 *                        two remaining (catch up to the newest in one tick)
 *   n == 2, 30x in a row -> discard one extra (steady-state clock drift)
 *
 * The plan is pure so the native shim test can fix each rule without media.
 * License: MIT (same as the shim). */
#ifndef TCS_DELIVERY_POLICY_H
#define TCS_DELIVERY_POLICY_H

#include <stdint.h>

typedef struct TcsDeliveryPlan {
  uint32_t drop_oldest;  /* frames to discard (counted as replaced) */
  uint32_t next_streak;  /* consecutive n==2 count after this call */
  int32_t lease;         /* 1 = lease the oldest remaining frame */
} TcsDeliveryPlan;

static inline TcsDeliveryPlan
tcs_delivery_plan (uint32_t n, uint32_t streak)
{
  TcsDeliveryPlan plan = { 0, 0, 0 };
  if (n == 0)
    return plan;
  if (n == 2) {
    if (streak + 1 >= 30) {
      plan.drop_oldest = 1;
      plan.next_streak = 0;
    } else {
      plan.next_streak = streak + 1;
    }
  } else if (n > 2) {
    plan.drop_oldest = n - 2;
  }
  plan.lease = 1;
  return plan;
}

#endif /* TCS_DELIVERY_POLICY_H */
