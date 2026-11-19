//! Capture cancellation and codec backpressure, exercised on every build host.
use std::collections::VecDeque;
use std::sync::atomic::{AtomicU64, Ordering};
use std::time::{Duration, Instant};

#[derive(Default)]
pub(crate) struct Gate {
    current: AtomicU64,
    ended: AtomicU64,
}
impl Gate {
    pub(crate) fn current(&self, generation: u64) -> bool {
        generation > self.ended.load(Ordering::SeqCst) && self.current.load(Ordering::SeqCst) == generation
    }
    pub(crate) fn start(&self, generation: u64) {
        self.current.fetch_max(generation, Ordering::SeqCst);
    }
    pub(crate) fn stop(&self, generation: u64) {
        self.ended.fetch_max(generation, Ordering::SeqCst);
        let _ = self.current.compare_exchange(generation, 0, Ordering::SeqCst, Ordering::SeqCst);
    }
}

#[derive(Default)]
pub(crate) struct EncoderQueue {
    pending: VecDeque<(u64, Instant)>,
}
impl EncoderQueue {
    pub(crate) fn ready(&self) -> bool {
        self.pending.len() < 2
    }
    pub(crate) fn accepted(&mut self, timestamp: u64, now: Instant) {
        debug_assert!(self.ready());
        self.pending.push_back((timestamp, now));
    }
    pub(crate) fn completed(&mut self, timestamp: u64) {
        // Baseline H.264 has no B frames. A newer output also retires older
        // inputs dropped by rate control; event-only output retires nothing.
        while self.pending.front().is_some_and(|(time, _)| *time <= timestamp) {
            self.pending.pop_front();
        }
    }
    pub(crate) fn stalled(&self, now: Instant) -> bool {
        self.pending.front().is_some_and(|(_, since)| now.duration_since(*since) > Duration::from_secs(5))
    }
}

/// Token bucket pacing captures at the active frame rate while keeping a
/// two-frame burst reserve so waking from an idle desktop is never delayed.
#[cfg_attr(not(windows), allow(dead_code))]
pub(crate) struct CaptureBucket {
    interval: f64,
    credit: f64,
    updated: Instant,
}

#[cfg_attr(not(windows), allow(dead_code))]
impl CaptureBucket {
    const BURST: f64 = 2.0;

    pub(crate) fn new(fps: u32, now: Instant) -> Self {
        let interval = 1.0 / f64::from(fps.max(1));
        Self { interval, credit: Self::BURST * interval, updated: now }
    }

    pub(crate) fn reset(&mut self, fps: u32, now: Instant) {
        self.interval = 1.0 / f64::from(fps.max(1));
        self.credit = Self::BURST * self.interval;
        self.updated = now;
    }

    fn refill(&mut self, now: Instant) {
        let elapsed = now.saturating_duration_since(self.updated).as_secs_f64();
        if elapsed > 0.0 {
            self.credit = (self.credit + elapsed).min(Self::BURST * self.interval);
            self.updated = now;
        }
    }

    pub(crate) fn ready(&self, now: Instant) -> bool {
        self.wait_duration(now).is_zero()
    }

    pub(crate) fn spend(&mut self, now: Instant) {
        self.refill(now);
        self.credit = (self.credit - self.interval).max(0.0);
    }

    pub(crate) fn wait_duration(&self, now: Instant) -> Duration {
        let elapsed = now.saturating_duration_since(self.updated).as_secs_f64();
        let available = (self.credit + elapsed).min(Self::BURST * self.interval);
        if available >= self.interval {
            Duration::ZERO
        } else {
            Duration::from_secs_f64((self.interval - available).max(0.0))
        }
    }
}

/// Bounded idle paint-over refinement: after desktop motion stops, re-encodes
/// up to three frames with widened rate limits to sharpen static text.
#[derive(Default)]
#[cfg_attr(not(windows), allow(dead_code))]
pub(crate) struct IdleRefinement {
    attempts: u8,
    first_encoded_size: Option<usize>,
    next_due: Option<Instant>,
    pub(crate) in_flight: bool,
}

#[cfg_attr(not(windows), allow(dead_code))]
impl IdleRefinement {
    pub(crate) const INITIAL_DELAY: Duration = Duration::from_millis(180);

    pub(crate) fn cancel(&mut self) {
        self.attempts = 0;
        self.first_encoded_size = None;
        self.next_due = None;
        self.in_flight = false;
    }

    pub(crate) fn next_due(&self) -> Option<Instant> {
        (!self.in_flight).then_some(self.next_due).flatten()
    }

    pub(crate) fn is_due(&self, now: Instant) -> bool {
        !self.in_flight && self.next_due.is_some_and(|due| now >= due)
    }

    pub(crate) fn schedule_initial(&mut self, now: Instant) {
        self.attempts = 0;
        self.first_encoded_size = None;
        self.next_due = Some(now + Self::INITIAL_DELAY);
    }

    pub(crate) fn mark_started(&mut self) {
        self.in_flight = true;
        self.next_due = None;
    }

    /// A dropped frame still uses one of the three attempts for this idle image.
    /// Returns `true` when another refinement pass should be scheduled.
    pub(crate) fn should_continue(&mut self, bytes: Option<usize>, small_frame_size: usize) -> bool {
        self.in_flight = false;
        self.attempts = self.attempts.saturating_add(1);
        if self.attempts >= 3 {
            self.next_due = None;
            return false;
        }
        let Some(bytes) = bytes else {
            return true;
        };
        let baseline = *self.first_encoded_size.get_or_insert(bytes);
        bytes > small_frame_size && bytes.saturating_mul(4) > baseline
    }

    pub(crate) fn schedule_next(&mut self, now: Instant, fps: u32) {
        let pace_ms = (1000 / u64::from(fps.max(1))).max(33);
        self.next_due = Some(now + Duration::from_millis(pace_ms));
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn a_stop_invalidates_a_delayed_native_start() {
        let gate = Gate::default();
        gate.stop(10);
        gate.start(10);
        assert!(!gate.current(10));
    }
    #[test]
    fn old_stops_and_starts_cannot_replace_a_new_viewer() {
        let gate = Gate::default();
        gate.start(20);
        gate.stop(10);
        gate.start(10);
        assert!(gate.current(20));
        assert!(!gate.current(10));
        gate.stop(20);
        assert!(!gate.current(20));
        gate.start(21);
        assert!(gate.current(21));
    }
    #[test]
    fn event_only_output_cannot_hide_a_stalled_picture() {
        let now = Instant::now();
        let mut queue = EncoderQueue::default();
        queue.accepted(10, now);
        queue.accepted(20, now);
        assert!(!queue.ready());
        assert!(!queue.stalled(now + Duration::from_secs(4)));
        assert!(queue.stalled(now + Duration::from_secs(6)));
        queue.completed(10);
        assert!(queue.ready());
        assert!(queue.stalled(now + Duration::from_secs(6)));
        queue.completed(20);
        assert!(!queue.stalled(now + Duration::from_secs(6)));
    }
    #[test]
    fn a_newer_picture_retires_dropped_inputs_without_leaking_queue_capacity() {
        let now = Instant::now();
        let mut queue = EncoderQueue::default();
        queue.accepted(10, now);
        queue.accepted(20, now);
        queue.completed(20);
        queue.accepted(30, now + Duration::from_secs(4));
        queue.accepted(40, now + Duration::from_secs(4));
        assert!(!queue.ready());
        assert!(!queue.stalled(now + Duration::from_secs(6)));
        queue.completed(20); // A duplicate or stale output releases no credit.
        assert!(!queue.ready());
        assert!(queue.stalled(now + Duration::from_secs(10)));
    }

    #[test]
    fn capture_bucket_allows_two_frame_burst_then_paces_at_configured_fps() {
        let start = Instant::now();
        let mut bucket = CaptureBucket::new(60, start);
        assert!(bucket.ready(start));
        bucket.spend(start);
        assert!(bucket.ready(start));
        bucket.spend(start);
        assert!(!bucket.ready(start));
        assert!(bucket.wait_duration(start) > Duration::from_millis(15));

        let next = start + Duration::from_micros(16_800);
        assert!(bucket.ready(next));
        bucket.spend(next);
        assert!(!bucket.ready(next));

        let later = next + Duration::from_secs(1);
        bucket.reset(30, later);
        assert!(bucket.ready(later));
        bucket.spend(later);
        bucket.spend(later);
        assert!(!bucket.ready(later));
        assert!(bucket.wait_duration(later) > Duration::from_millis(32));
    }

    #[test]
    fn idle_refinement_bounds_attempts_and_stops_on_convergence() {
        let mut refinement = IdleRefinement::default();
        assert!(refinement.should_continue(Some(40_000), 1024));
        assert!(refinement.should_continue(Some(20_000), 1024));
        assert!(!refinement.should_continue(Some(15_000), 1024));

        let mut converged = IdleRefinement::default();
        assert!(converged.should_continue(Some(40_000), 1024));
        assert!(!converged.should_continue(Some(8_000), 1024));

        let mut dropped = IdleRefinement::default();
        assert!(dropped.should_continue(None, 1024));
        assert!(dropped.should_continue(None, 1024));
        assert!(!dropped.should_continue(None, 1024));
    }
}
