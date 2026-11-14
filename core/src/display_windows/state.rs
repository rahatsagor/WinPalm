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
}
